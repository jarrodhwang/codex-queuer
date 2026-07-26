using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public sealed record GpuResourceTelemetry(
    int Index,
    string Name,
    double? UtilizationPercent,
    double? MemoryUsagePercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? TemperatureCelsius,
    double? PowerWatts);

public sealed record MachineResourceTelemetry(
    bool Available,
    string? Error,
    double? CpuUsagePercent,
    double? MemoryUsagePercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? CpuTemperatureCelsius,
    double? SystemTemperatureCelsius,
    double? SystemPowerWatts,
    string? SystemPowerSource,
    IReadOnlyList<GpuResourceTelemetry> Gpus,
    DateTimeOffset CollectedAt);

public interface IMachineResourceTelemetryService
{
    Task<MachineResourceTelemetry> CollectAsync(
        TargetMachine machine,
        CancellationToken cancellationToken);
}

internal sealed record ResourceTelemetryCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated = false);

internal interface IResourceTelemetryCommandExecutor
{
    Task<ResourceTelemetryCommandResult> ExecuteAsync(
        TargetMachine machine,
        CancellationToken cancellationToken);
}

internal sealed class MachineResourceTelemetryService(
    IResourceTelemetryCommandExecutor commandExecutor) : IMachineResourceTelemetryService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);
    private const int MaximumConcurrentCollections = 4;
    private const int CollectionGateCount = 64;
    private const int MaximumCacheEntries = 256;
    private readonly ConcurrentDictionary<Guid, CacheEntry> cache = new();
    private readonly SemaphoreSlim[] collectionGates = Enumerable
        .Range(0, CollectionGateCount)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly SemaphoreSlim executionGate =
        new(MaximumConcurrentCollections, MaximumConcurrentCollections);

    public async Task<MachineResourceTelemetry> CollectAsync(
        TargetMachine machine,
        CancellationToken cancellationToken)
    {
        var machineVersion = machine.UpdatedAt;
        if (TryGetCached(machine.Id, machineVersion, out var cached))
        {
            return cached;
        }

        var collectionGate = collectionGates[
            (int)((uint)machine.Id.GetHashCode() % CollectionGateCount)];
        await collectionGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCached(machine.Id, machineVersion, out cached))
            {
                return cached;
            }

            MachineResourceTelemetry telemetry;
            if (machine.Platform is MachinePlatform.Windows or MachinePlatform.MacOs
                || (machine.Kind == MachineKind.Local && !OperatingSystem.IsLinux()))
            {
                telemetry = Unavailable("Resource monitoring currently supports Linux machines.");
            }
            else
            {
                await executionGate.WaitAsync(cancellationToken);
                try
                {
                    var result = await commandExecutor.ExecuteAsync(machine, cancellationToken);
                    telemetry = Parse(result);
                }
                finally
                {
                    executionGate.Release();
                }
            }

            StoreCached(machine.Id, new CacheEntry(
                machineVersion,
                DateTimeOffset.UtcNow,
                telemetry));
            return telemetry;
        }
        finally
        {
            collectionGate.Release();
        }
    }

    private bool TryGetCached(
        Guid machineId,
        DateTimeOffset machineVersion,
        out MachineResourceTelemetry telemetry)
    {
        if (cache.TryGetValue(machineId, out var entry)
            && entry.MachineUpdatedAt == machineVersion
            && DateTimeOffset.UtcNow - entry.CachedAt < CacheLifetime)
        {
            telemetry = entry.Telemetry;
            return true;
        }

        telemetry = null!;
        return false;
    }

    private void StoreCached(Guid machineId, CacheEntry entry)
    {
        cache[machineId] = entry;
        if (cache.Count <= MaximumCacheEntries)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in cache)
        {
            if (now - candidate.Value.CachedAt >= CacheLifetime)
            {
                TryRemoveUnchanged(candidate);
            }
        }

        var overflow = cache.Count - MaximumCacheEntries;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var candidate in cache
                     .OrderBy(item => item.Value.CachedAt)
                     .Take(overflow))
        {
            TryRemoveUnchanged(candidate);
        }
    }

    private void TryRemoveUnchanged(KeyValuePair<Guid, CacheEntry> candidate)
    {
        if (cache.TryGetValue(candidate.Key, out var current)
            && ReferenceEquals(current, candidate.Value))
        {
            cache.TryRemove(candidate.Key, out _);
        }
    }

    internal static MachineResourceTelemetry Parse(ResourceTelemetryCommandResult result)
    {
        double? cpuUsage = null;
        double? memoryUsage = null;
        long? memoryUsed = null;
        long? memoryTotal = null;
        double? cpuTemperature = null;
        double? systemTemperature = null;
        double? systemPower = null;
        string? systemPowerSource = null;
        var systemPowerRank = -1;
        var gpus = new List<GpuResourceTelemetry>();
        string? reportedError = null;

        foreach (var rawLine in result.StandardOutput.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = rawLine.TrimEnd('\r').Split('|');
            if (fields.Length == 0)
            {
                continue;
            }

            switch (fields[0])
            {
                case "CQ_CPU" when fields.Length >= 2:
                    cpuUsage = ParsePercent(fields[1]);
                    break;

                case "CQ_MEM" when fields.Length >= 4:
                    memoryUsed = ParseNonNegativeInt64(fields[1]);
                    memoryTotal = ParseNonNegativeInt64(fields[2]);
                    memoryUsage = ParsePercent(fields[3]);
                    if (memoryUsage is null
                        && memoryUsed is { } used
                        && memoryTotal is > 0)
                    {
                        memoryUsage = Math.Clamp(used * 100d / memoryTotal.Value, 0d, 100d);
                    }
                    break;

                case "CQ_TEMP" when fields.Length >= 4:
                    var source = fields[1].Trim();
                    var label = fields[2].Trim();
                    var temperature = ParseTemperature(fields[3], isMilliCelsius: true);
                    if (temperature is null)
                    {
                        break;
                    }

                    if (IsCpuTemperature(source, label))
                    {
                        cpuTemperature = Max(cpuTemperature, temperature);
                    }
                    else if (IsSystemTemperature(source, label))
                    {
                        systemTemperature = Max(systemTemperature, temperature);
                    }
                    break;

                case "CQ_SYSPOWER" when fields.Length >= 3:
                    var powerSource = fields[1].Trim();
                    var power = ParsePower(fields[2], isMicrowatts: true);
                    var rank = RankSystemPowerSource(powerSource);
                    if (power is not null && rank > systemPowerRank)
                    {
                        systemPower = power;
                        systemPowerSource = string.IsNullOrWhiteSpace(powerSource) ? null : powerSource;
                        systemPowerRank = rank;
                    }
                    break;

                case "CQ_NVIDIA_GPU" when fields.Length >= 9:
                    gpus.Add(ParseNvidiaGpu(fields, gpus.Count));
                    break;

                case "CQ_DRM_GPU" when fields.Length >= 9:
                    gpus.Add(ParseDrmGpu(fields, gpus.Count));
                    break;

                case "CQ_UNSUPPORTED" when fields.Length >= 2:
                case "CQ_ERROR" when fields.Length >= 2:
                    reportedError = SanitizeError(string.Join('|', fields.Skip(1)));
                    break;
            }
        }

        var usableGpus = gpus
            .Where(gpu => gpu.UtilizationPercent is not null
                || gpu.MemoryUsagePercent is not null
                || gpu.TemperatureCelsius is not null
                || gpu.PowerWatts is not null)
            .OrderBy(gpu => gpu.Index)
            .ToArray();
        var available = cpuUsage is not null
            || memoryUsage is not null
            || cpuTemperature is not null
            || systemTemperature is not null
            || systemPower is not null
            || usableGpus.Length > 0;

        string? error = null;
        if (!available)
        {
            error = reportedError
                ?? SanitizeError(result.StandardError)
                ?? (result.OutputTruncated
                    ? "The telemetry response exceeded the safe output limit."
                    : result.ExitCode == 0
                        ? "No supported resource sensors were found."
                        : "Resource monitoring command failed with exit code " + result.ExitCode + ".");
        }

        return new MachineResourceTelemetry(
            available,
            error,
            Round(cpuUsage),
            Round(memoryUsage),
            memoryUsed,
            memoryTotal,
            Round(cpuTemperature),
            Round(systemTemperature),
            Round(systemPower),
            systemPowerSource,
            usableGpus,
            DateTimeOffset.UtcNow);
    }

    private static GpuResourceTelemetry ParseNvidiaGpu(string[] fields, int fallbackIndex)
    {
        var index = ParseNonNegativeInt32(fields[1]) ?? fallbackIndex;
        var name = SanitizeName(fields[2], "NVIDIA GPU " + index);
        var utilization = ParsePercent(fields[3]);
        var memoryUsed = MebibytesToBytes(ParseNonNegativeDouble(fields[4]));
        var memoryTotal = MebibytesToBytes(ParseNonNegativeDouble(fields[5]));
        var memoryUsage = MemoryPercent(memoryUsed, memoryTotal);
        var temperature = ParseTemperature(fields[6], isMilliCelsius: false);
        var power = ParsePower(fields[7], isMicrowatts: false);

        return new GpuResourceTelemetry(
            index,
            name,
            Round(utilization),
            Round(memoryUsage),
            memoryUsed,
            memoryTotal,
            Round(temperature),
            Round(power));
    }

    private static GpuResourceTelemetry ParseDrmGpu(string[] fields, int fallbackIndex)
    {
        var index = ParseNonNegativeInt32(fields[1]) ?? fallbackIndex;
        var name = SanitizeName(fields[2], "GPU " + index);
        var utilization = ParsePercent(fields[3]);
        var memoryUsed = ParseNonNegativeInt64(fields[4]);
        var memoryTotal = ParseNonNegativeInt64(fields[5]);
        var memoryUsage = MemoryPercent(memoryUsed, memoryTotal);
        var temperature = ParseTemperature(fields[6], isMilliCelsius: true);
        var power = ParsePower(fields[7], isMicrowatts: true);

        return new GpuResourceTelemetry(
            index,
            name,
            Round(utilization),
            Round(memoryUsage),
            memoryUsed,
            memoryTotal,
            Round(temperature),
            Round(power));
    }

    private static MachineResourceTelemetry Unavailable(string error) =>
        new(
            false,
            error,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<GpuResourceTelemetry>(),
            DateTimeOffset.UtcNow);

    private sealed record CacheEntry(
        DateTimeOffset MachineUpdatedAt,
        DateTimeOffset CachedAt,
        MachineResourceTelemetry Telemetry);

    private static bool IsCpuTemperature(string source, string label)
    {
        var combined = (source + " " + label).ToLowerInvariant();
        return combined.Contains("coretemp", StringComparison.Ordinal)
            || combined.Contains("k10temp", StringComparison.Ordinal)
            || combined.Contains("zenpower", StringComparison.Ordinal)
            || combined.Contains("cpu_thermal", StringComparison.Ordinal)
            || combined.Contains("cpu-thermal", StringComparison.Ordinal)
            || combined.Contains("x86_pkg_temp", StringComparison.Ordinal)
            || combined.Contains("package id", StringComparison.Ordinal)
            || combined.Contains("tctl", StringComparison.Ordinal)
            || combined.Contains("tdie", StringComparison.Ordinal);
    }

    private static bool IsSystemTemperature(string source, string label)
    {
        var combined = (source + " " + label).ToLowerInvariant();
        if (combined.Contains("nvme", StringComparison.Ordinal)
            || combined.Contains("amdgpu", StringComparison.Ordinal)
            || combined.Contains("nouveau", StringComparison.Ordinal))
        {
            return false;
        }

        return combined.Contains("acpitz", StringComparison.Ordinal)
            || combined.Contains("system", StringComparison.Ordinal)
            || combined.Contains("motherboard", StringComparison.Ordinal)
            || combined.Contains("ambient", StringComparison.Ordinal);
    }

    private static int RankSystemPowerSource(string source)
    {
        var normalized = source.ToLowerInvariant();
        if (normalized.Contains("psys", StringComparison.Ordinal))
        {
            return 3;
        }

        if (normalized.Contains("system", StringComparison.Ordinal)
            || normalized.Contains("total", StringComparison.Ordinal))
        {
            return 2;
        }

        return normalized.Contains("battery", StringComparison.Ordinal) ? 1 : 0;
    }

    private static double? ParsePercent(string value)
    {
        var parsed = ParseNonNegativeDouble(value);
        return parsed is null ? null : Math.Clamp(parsed.Value, 0d, 100d);
    }

    private static double? ParseTemperature(string value, bool isMilliCelsius)
    {
        var parsed = ParseDouble(value);
        if (parsed is null)
        {
            return null;
        }

        var celsius = isMilliCelsius ? parsed.Value / 1_000d : parsed.Value;
        return celsius is >= -50d and <= 250d ? celsius : null;
    }

    private static double? ParsePower(string value, bool isMicrowatts)
    {
        var parsed = ParseNonNegativeDouble(value);
        if (parsed is null)
        {
            return null;
        }

        var watts = isMicrowatts ? parsed.Value / 1_000_000d : parsed.Value;
        return double.IsFinite(watts) ? watts : null;
    }

    private static double? ParseDouble(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0
            || normalized.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("[Not Supported]", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(
            normalized,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            && double.IsFinite(parsed)
                ? parsed
                : null;
    }

    private static double? ParseNonNegativeDouble(string value)
    {
        var parsed = ParseDouble(value);
        return parsed is >= 0d ? parsed : null;
    }

    private static long? ParseNonNegativeInt64(string value) =>
        long.TryParse(
            value.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            && parsed >= 0
                ? parsed
                : null;

    private static int? ParseNonNegativeInt32(string value) =>
        int.TryParse(
            value.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            && parsed >= 0
                ? parsed
                : null;

    private static long? MebibytesToBytes(double? mebibytes)
    {
        if (mebibytes is null || mebibytes > long.MaxValue / 1_048_576d)
        {
            return null;
        }

        return (long)Math.Round(mebibytes.Value * 1_048_576d);
    }

    private static double? MemoryPercent(long? used, long? total) =>
        used is { } usedBytes && total is > 0
            ? Math.Clamp(usedBytes * 100d / total.Value, 0d, 100d)
            : null;

    private static double? Max(double? current, double? candidate) =>
        current is null ? candidate : candidate is null ? current : Math.Max(current.Value, candidate.Value);

    private static double? Round(double? value) =>
        value is null ? null : Math.Round(value.Value, 1, MidpointRounding.AwayFromZero);

    private static string SanitizeName(string value, string fallback)
    {
        var sanitized = new string(value
            .Where(character => !char.IsControl(character) && character != '|')
            .Take(120)
            .ToArray())
            .Trim();
        return sanitized.Length == 0 ? fallback : sanitized;
    }

    private static string? SanitizeError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var words = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sanitized = string.Join(' ', words);
        return sanitized[..Math.Min(sanitized.Length, 500)];
    }
}

internal sealed class ResourceTelemetryCommandExecutor(
    ILogger<ResourceTelemetryCommandExecutor> logger) : IResourceTelemetryCommandExecutor
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(6);
    private const int MaximumOutputCharacters = 64 * 1024;

    // This fixed, read-only Linux collector intentionally uses only /proc, /sys and
    // vendor utilities already present on the target. It never invokes sudo or accepts
    // user-provided shell fragments. Missing sensors simply produce no matching record.
    private const string CollectorScript = """
        kernel="$(uname -s 2>/dev/null || true)"
        if [ "$kernel" != "Linux" ]; then
          printf '%s\n' 'CQ_UNSUPPORTED|Resource monitoring currently supports Linux machines.'
          exit 0
        fi
        if [ ! -r /proc/stat ] || [ ! -r /proc/meminfo ]; then
          printf '%s\n' 'CQ_ERROR|Linux resource counters are not readable.'
          exit 3
        fi

        read -r cpu_label cpu_user cpu_nice cpu_system cpu_idle cpu_iowait cpu_irq cpu_softirq cpu_steal cpu_rest < /proc/stat
        cpu_total_1=$((cpu_user + cpu_nice + cpu_system + cpu_idle + cpu_iowait + cpu_irq + cpu_softirq + cpu_steal))
        cpu_idle_1=$((cpu_idle + cpu_iowait))
        sleep 0.20
        read -r cpu_label cpu_user cpu_nice cpu_system cpu_idle cpu_iowait cpu_irq cpu_softirq cpu_steal cpu_rest < /proc/stat
        cpu_total_2=$((cpu_user + cpu_nice + cpu_system + cpu_idle + cpu_iowait + cpu_irq + cpu_softirq + cpu_steal))
        cpu_idle_2=$((cpu_idle + cpu_iowait))
        awk -v total1="$cpu_total_1" -v total2="$cpu_total_2" -v idle1="$cpu_idle_1" -v idle2="$cpu_idle_2" \
          'BEGIN { delta=total2-total1; idle=idle2-idle1; if (delta > 0) printf "CQ_CPU|%.1f\n", (delta-idle)*100/delta }'

        mem_total_kb="$(awk '$1=="MemTotal:" { print $2; exit }' /proc/meminfo)"
        mem_available_kb="$(awk '$1=="MemAvailable:" { print $2; exit }' /proc/meminfo)"
        if [ -z "$mem_available_kb" ]; then
          mem_available_kb="$(awk '
            $1=="MemFree:" { free=$2 }
            $1=="Buffers:" { buffers=$2 }
            $1=="Cached:" { cached=$2 }
            END { print free+buffers+cached }' /proc/meminfo)"
        fi
        awk -v total="$mem_total_kb" -v available="$mem_available_kb" \
          'BEGIN { if (total > 0) { used=total-available; printf "CQ_MEM|%.0f|%.0f|%.1f\n", used*1024, total*1024, used*100/total } }'

        for hwmon in /sys/class/hwmon/hwmon*; do
          [ -d "$hwmon" ] || continue
          sensor_name="$(cat "$hwmon/name" 2>/dev/null || printf unknown)"
          for input in "$hwmon"/temp*_input; do
            [ -r "$input" ] || continue
            label_path="${input%_input}_label"
            sensor_label="$(cat "$label_path" 2>/dev/null || basename "${input%_input}")"
            sensor_value="$(cat "$input" 2>/dev/null || true)"
            case "$sensor_value" in ''|*[!0-9-]*) continue ;; esac
            sensor_name_safe="$(printf '%s' "$sensor_name" | tr '|\r\n' '   ')"
            sensor_label_safe="$(printf '%s' "$sensor_label" | tr '|\r\n' '   ')"
            printf 'CQ_TEMP|%s|%s|%s\n' "$sensor_name_safe" "$sensor_label_safe" "$sensor_value"
          done
        done

        for zone in /sys/class/thermal/thermal_zone*; do
          [ -r "$zone/temp" ] || continue
          zone_type="$(cat "$zone/type" 2>/dev/null || printf thermal)"
          zone_value="$(cat "$zone/temp" 2>/dev/null || true)"
          case "$zone_value" in ''|*[!0-9-]*) continue ;; esac
          zone_type_safe="$(printf '%s' "$zone_type" | tr '|\r\n' '   ')"
          printf 'CQ_TEMP|%s|%s|%s\n' "$zone_type_safe" "$zone_type_safe" "$zone_value"
        done

        system_power_found=0
        for hwmon in /sys/class/hwmon/hwmon*; do
          [ -d "$hwmon" ] || continue
          sensor_name="$(cat "$hwmon/name" 2>/dev/null || printf hwmon)"
          for input in "$hwmon"/power*_input "$hwmon"/power*_average; do
            [ -r "$input" ] || continue
            case "$input" in
              *_input) label_path="${input%_input}_label" ;;
              *_average) label_path="${input%_average}_label" ;;
            esac
            sensor_label="$(cat "$label_path" 2>/dev/null || true)"
            normalized_label="$(printf '%s' "$sensor_label" | tr '[:upper:]' '[:lower:]')"
            case "$normalized_label" in
              *psys*|*system*power*|*system*input*|*total*power*|*total*input*) ;;
              *) continue ;;
            esac
            power_value="$(cat "$input" 2>/dev/null || true)"
            case "$power_value" in ''|*[!0-9]*) continue ;; esac
            power_source_safe="$(printf '%s %s' "$sensor_name" "$sensor_label" | tr '|\r\n' '   ')"
            printf 'CQ_SYSPOWER|%s|%s\n' "$power_source_safe" "$power_value"
            system_power_found=1
          done
        done

        if [ "$system_power_found" -eq 0 ]; then
          for supply in /sys/class/power_supply/*; do
            [ -d "$supply" ] || continue
            supply_type="$(cat "$supply/type" 2>/dev/null || true)"
            supply_status="$(cat "$supply/status" 2>/dev/null || true)"
            [ "$supply_type" = "Battery" ] || continue
            [ "$supply_status" = "Discharging" ] || continue
            power_value="$(cat "$supply/power_now" 2>/dev/null || true)"
            if [ -z "$power_value" ]; then
              voltage_value="$(cat "$supply/voltage_now" 2>/dev/null || true)"
              current_value="$(cat "$supply/current_now" 2>/dev/null || true)"
              power_value="$(awk -v voltage="$voltage_value" -v current="$current_value" \
                'BEGIN { if (voltage >= 0 && current >= 0) printf "%.0f", voltage*current/1000000 }')"
            fi
            case "$power_value" in ''|*[!0-9]*) continue ;; esac
            printf 'CQ_SYSPOWER|battery discharge|%s\n' "$power_value"
            break
          done
        fi

        has_nvidia=0
        if command -v nvidia-smi >/dev/null 2>&1; then
          has_nvidia=1
          nvidia-smi \
            --query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw \
            --format=csv,noheader,nounits 2>/dev/null |
          while IFS=',' read -r gpu_index gpu_name gpu_util gpu_memory_used gpu_memory_total gpu_temp gpu_power; do
            gpu_name_safe="$(printf '%s' "$gpu_name" | tr '|\r\n' '   ')"
            printf 'CQ_NVIDIA_GPU|%s|%s|%s|%s|%s|%s|%s|\n' \
              "$gpu_index" "$gpu_name_safe" "$gpu_util" "$gpu_memory_used" "$gpu_memory_total" "$gpu_temp" "$gpu_power"
          done
        fi

        for card in /sys/class/drm/card[0-9]*; do
          [ -d "$card/device" ] || continue
          card_index="${card##*card}"
          vendor="$(cat "$card/device/vendor" 2>/dev/null || true)"
          [ "$has_nvidia" -eq 1 ] && [ "$vendor" = "0x10de" ] && continue
          case "$vendor" in
            0x1002) gpu_name="AMD GPU $card_index" ;;
            0x8086) gpu_name="Intel GPU $card_index" ;;
            0x10de) gpu_name="NVIDIA GPU $card_index" ;;
            *) gpu_name="GPU $card_index" ;;
          esac
          gpu_util="$(cat "$card/device/gpu_busy_percent" 2>/dev/null || true)"
          gpu_memory_used="$(cat "$card/device/mem_info_vram_used" 2>/dev/null || true)"
          gpu_memory_total="$(cat "$card/device/mem_info_vram_total" 2>/dev/null || true)"
          gpu_temp=""
          gpu_power=""
          for gpu_hwmon in "$card"/device/hwmon/hwmon*; do
            [ -d "$gpu_hwmon" ] || continue
            gpu_temp="$(cat "$gpu_hwmon/temp1_input" 2>/dev/null || true)"
            gpu_power="$(cat "$gpu_hwmon/power1_average" 2>/dev/null || cat "$gpu_hwmon/power1_input" 2>/dev/null || true)"
            break
          done
          if [ -n "$gpu_util$gpu_memory_used$gpu_memory_total$gpu_temp$gpu_power" ]; then
            printf 'CQ_DRM_GPU|%s|%s|%s|%s|%s|%s|%s|\n' \
              "$card_index" "$gpu_name" "$gpu_util" "$gpu_memory_used" "$gpu_memory_total" "$gpu_temp" "$gpu_power"
          fi
        done
        """;

    public async Task<ResourceTelemetryCommandResult> ExecuteAsync(
        TargetMachine machine,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo;
        try
        {
            startInfo = machine.Kind == MachineKind.Local
                ? BuildLocalStartInfo()
                : BuildSshStartInfo(machine);
        }
        catch (InvalidOperationException ex)
        {
            return new ResourceTelemetryCommandResult(1, "", ex.Message);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ResourceTelemetryCommandResult(1, "", "Could not start resource monitoring.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Could not start resource monitoring for machine {MachineId}", machine.Id);
            return new ResourceTelemetryCommandResult(1, "", "Could not start resource monitoring.");
        }

        using var timeout = new CancellationTokenSource(ExecutionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, linked.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ResourceTelemetryCommandResult(
                process.ExitCode,
                stdout.Text,
                stderr.Text,
                stdout.Truncated || stderr.Truncated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await ObserveCancellationAsync(stdoutTask, stderrTask);
            return new ResourceTelemetryCommandResult(
                124,
                "",
                "Resource monitoring timed out after " + ExecutionTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await ObserveCancellationAsync(stdoutTask, stderrTask);
            throw;
        }
    }

    internal static ProcessStartInfo BuildSshStartInfo(TargetMachine machine)
    {
        if (string.IsNullOrWhiteSpace(machine.Host) || !IsSafeSshHost(machine.Host))
        {
            throw new InvalidOperationException("The SSH host is missing or contains unsupported characters.");
        }

        if (!string.IsNullOrWhiteSpace(machine.UserName) && !IsSafeSshUser(machine.UserName))
        {
            throw new InvalidOperationException("The SSH user name contains unsupported characters.");
        }

        if (machine.Port is < 1 or > 65_535)
        {
            throw new InvalidOperationException("The SSH port must be between 1 and 65535.");
        }

        var destination = string.IsNullOrWhiteSpace(machine.UserName)
            ? machine.Host
            : machine.UserName + "@" + machine.Host;
        var startInfo = CreateStartInfo("ssh");
        AddArguments(
            startInfo,
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "ConnectTimeout=3",
            "-o", "ConnectionAttempts=1",
            "-o", "ServerAliveInterval=2",
            "-o", "ServerAliveCountMax=1",
            "-p", machine.Port.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(machine.SshKeyPath))
        {
            var keyPath = ResolveSshKeyPath(machine.SshKeyPath);
            if (!File.Exists(keyPath))
            {
                throw new InvalidOperationException("The configured SSH key is not accessible to the API.");
            }

            AddArguments(startInfo, "-i", keyPath);
        }

        startInfo.ArgumentList.Add(destination);
        startInfo.ArgumentList.Add("LC_ALL=C /bin/sh -c " + QuotePosix(CollectorScript));
        return startInfo;
    }

    private static ProcessStartInfo BuildLocalStartInfo()
    {
        var startInfo = CreateStartInfo("/bin/sh");
        AddArguments(startInfo, "-c", CollectorScript);
        startInfo.Environment["LC_ALL"] = "C";
        return startInfo;
    }

    private static ProcessStartInfo CreateStartInfo(string fileName) =>
        new()
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[2_048];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                break;
            }

            var remaining = MaximumOutputCharacters - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(count, remaining));
            }

            if (count > remaining)
            {
                truncated = true;
            }
        }

        return (output.ToString(), truncated);
    }

    private static async Task ObserveCancellationAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Expected after the timeout/caller token cancels the stream readers.
        }
        catch (IOException)
        {
            // Expected when killing the child closes redirected streams mid-read.
        }
    }

    private static bool IsSafeSshHost(string host) =>
        host.Length <= 253
        && host[0] != '-'
        && host.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or ':' or '[' or ']' or '%');

    private static bool IsSafeSshUser(string userName) =>
        userName.Length <= 64
        && userName[0] != '-'
        && userName.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-');

    private static string QuotePosix(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string ResolveSshKeyPath(string configuredPath)
    {
        var trimmed = configuredPath.Trim();
        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName) || File.Exists(trimmed))
        {
            return trimmed;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var homeCandidate = Path.Combine(home, ".ssh", fileName);
            if (File.Exists(homeCandidate))
            {
                return homeCandidate;
            }
        }

        var mountedCandidate = Path.Combine("/home/app/.ssh", fileName);
        return File.Exists(mountedCandidate) ? mountedCandidate : trimmed;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort: the linked timeout/caller cancellation still bounds the request.
        }
    }
}
