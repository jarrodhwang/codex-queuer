using CodexQueue.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CodexQueue.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TargetMachine> Machines => Set<TargetMachine>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<AiProviderProfile> AiProviderProfiles => Set<AiProviderProfile>();
    public DbSet<QueueTab> QueueTabs => Set<QueueTab>();
    public DbSet<CodexRequest> Requests => Set<CodexRequest>();
    public DbSet<CodexRun> Runs => Set<CodexRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TargetMachine>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.Host).HasMaxLength(256);
            entity.Property(x => x.UserName).HasMaxLength(128);
            entity.Property(x => x.SshKeyPath).HasMaxLength(1024);
            entity.Property(x => x.WorkingRoot).HasMaxLength(1024);
            entity.Property(x => x.Platform).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Path).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.CodexSessionId).HasMaxLength(80);
            entity.Property(x => x.DefaultModel).HasMaxLength(256);
            entity.Property(x => x.DefaultModelEffort).HasMaxLength(32);
            entity.Property(x => x.DefaultModelSpeed).HasMaxLength(32);
            entity.Property(x => x.DefaultCommitModel).HasMaxLength(256);
            entity.Property(x => x.DefaultCommitModelEffort).HasMaxLength(32);
            entity.Property(x => x.DefaultCommitModelSpeed).HasMaxLength(32);
            entity.Property(x => x.DefaultGenerateCommit);
            entity.Property(x => x.DefaultSeparateCommitSession);
            entity.Property(x => x.DefaultPermissionMode).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.DefaultExecutionRunner).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.DefaultLocalModel).HasMaxLength(256);
            entity.Property(x => x.DefaultLocalModelEffort).HasMaxLength(32);
            entity.Property(x => x.DefaultLocalModelSpeed).HasMaxLength(32);
            entity.Property(x => x.SeparateQueuesByTab);
            entity.HasOne(x => x.Machine)
                .WithMany(x => x.Projects)
                .HasForeignKey(x => x.MachineId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.DefaultLocalProviderProfile)
                .WithMany(x => x.DefaultProjects)
                .HasForeignKey(x => x.DefaultLocalProviderProfileId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => new { x.MachineId, x.Name }).IsUnique();
            entity.HasIndex(x => x.DefaultLocalProviderProfileId);
        });

        modelBuilder.Entity<AiProviderProfile>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(160).UseCollation("NOCASE").IsRequired();
            entity.Property(x => x.Source).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.BaseUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.ModelDiscoveryMode).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.ApiKeyEnvironmentVariable).HasMaxLength(160);
            entity.Property(x => x.DefaultModel).HasMaxLength(256);
            entity.Property(x => x.LastHealthStatus).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.LastHealthError).HasMaxLength(2048);
            entity.HasOne(x => x.ServerMachine)
                .WithMany()
                .HasForeignKey(x => x.ServerMachineId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => new { x.Source, x.BaseUrl });
            entity.HasIndex(x => x.ServerMachineId);
        });

        modelBuilder.Entity<CodexRequest>(entity =>
        {
            entity.Property(x => x.ExecutionRunner).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.QueueWaitReason).HasMaxLength(512);
            entity.Property(x => x.ExecutionProjectPath).HasMaxLength(2048);
            entity.Property(x => x.Prompt).IsRequired();
            entity.Property(x => x.AttachmentsJson);
            entity.Property(x => x.Model).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ModelEffort).HasMaxLength(32);
            entity.Property(x => x.ModelSpeed).HasMaxLength(32);
            entity.Property(x => x.QueueOrder);
            entity.Property(x => x.SeparateCommitSession);
            entity.Property(x => x.PermissionMode).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.CommitModel).HasMaxLength(256);
            entity.Property(x => x.CommitModelEffort).HasMaxLength(32);
            entity.Property(x => x.CommitModelSpeed).HasMaxLength(32);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.RetryAfter);
            entity.Property(x => x.RetryReason).HasMaxLength(512);
            entity.Property(x => x.AvailableModel).HasMaxLength(256);
            entity.Property(x => x.ArchivedAt);
            entity.Property(x => x.DeletedAt);
            entity.HasOne(x => x.Project)
                .WithMany(x => x.Requests)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.QueueTab)
                .WithMany(x => x.Requests)
                .HasForeignKey(x => x.QueueTabId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.Machine)
                .WithMany()
                .HasForeignKey(x => x.MachineId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ProviderProfile)
                .WithMany(x => x.Requests)
                .HasForeignKey(x => x.ProviderProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            entity.HasIndex(x => new { x.ProjectId, x.QueueOrder });
            entity.HasIndex(x => x.ProviderProfileId);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.DeletedAt);
        });

        modelBuilder.Entity<QueueTab>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(80).UseCollation("NOCASE").IsRequired();
            entity.Property(x => x.CodexSessionId).HasMaxLength(80);
            entity.Property(x => x.OpenHandsConversationId).HasMaxLength(80);
            entity.Property(x => x.DeletedAt);
            entity.HasOne(x => x.Project)
                .WithMany(x => x.QueueTabs)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProjectId, x.Name })
                .IsUnique()
                .HasDatabaseName("IX_QueueTabs_ProjectId_ActiveName")
                .HasFilter("\"DeletedAt\" IS NULL");
        });

        modelBuilder.Entity<CodexRun>(entity =>
        {
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.ExecutionRunner).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ProviderProfileName).HasMaxLength(160);
            entity.Property(x => x.ProviderSource).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Model).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ModelEffort).HasMaxLength(32);
            entity.Property(x => x.ModelSpeed).HasMaxLength(32);
            entity.Property(x => x.CodexSessionId).HasMaxLength(80);
            entity.Property(x => x.OpenHandsConversationId).HasMaxLength(80);
            entity.Property(x => x.CommandPreview).HasMaxLength(2048);
            entity.Property(x => x.RetryAfter);
            entity.Property(x => x.RetryReason).HasMaxLength(512);
            entity.Property(x => x.AvailableModel).HasMaxLength(256);
            entity.HasOne(x => x.Request)
                .WithMany(x => x.Runs)
                .HasForeignKey(x => x.RequestId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.RequestId, x.Kind });
            entity.HasIndex(x => x.ProviderProfileId);
            entity.HasIndex(x => x.Status);
        });
    }
}
