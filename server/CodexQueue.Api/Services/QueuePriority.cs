using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public static class QueuePriority
{
    public static int DisplayRank(QueueStatus status) =>
        status switch
        {
            QueueStatus.Succeeded => 0,
            QueueStatus.Running or QueueStatus.CancelRequested => 1,
            QueueStatus.Queued => 2,
            QueueStatus.UsageLimited => 3,
            QueueStatus.Failed or QueueStatus.Cancelled => 4,
            _ => 5
        };

    public static int CompareForDisplay(CodexRequest left, CodexRequest right)
    {
        var rankComparison = DisplayRank(left.Status).CompareTo(DisplayRank(right.Status));
        if (rankComparison != 0)
        {
            return rankComparison;
        }

        if (left.Status == QueueStatus.Succeeded)
        {
            return CompareDescending(left.FinishedAt ?? left.CreatedAt, right.FinishedAt ?? right.CreatedAt)
                ?? CompareDescending(left.CreatedAt, right.CreatedAt)
                ?? left.Id.CompareTo(right.Id);
        }

        if (left.Status is QueueStatus.Failed or QueueStatus.Cancelled or QueueStatus.UsageLimited)
        {
            return CompareDescending(left.FinishedAt ?? left.CreatedAt, right.FinishedAt ?? right.CreatedAt)
                ?? CompareDescending(left.CreatedAt, right.CreatedAt)
                ?? left.Id.CompareTo(right.Id);
        }

        return left.QueueOrder.CompareTo(right.QueueOrder) != 0
            ? left.QueueOrder.CompareTo(right.QueueOrder)
            : left.CreatedAt.CompareTo(right.CreatedAt) != 0
                ? left.CreatedAt.CompareTo(right.CreatedAt)
                : left.Id.CompareTo(right.Id);
    }

    public static void ReorderQueuedAfterActive(IEnumerable<CodexRequest> projectRequests, IReadOnlyList<Guid> queuedRequestIds)
    {
        var requests = projectRequests
            .Where(IsVisiblePriorityRequest)
            .ToArray();
        var queuedById = requests
            .Where(x => x.Status == QueueStatus.Queued)
            .ToDictionary(x => x.Id);
        var requestedSet = new HashSet<Guid>();
        var requestedQueued = new List<CodexRequest>();
        foreach (var requestId in queuedRequestIds)
        {
            if (queuedById.TryGetValue(requestId, out var request) && requestedSet.Add(requestId))
            {
                requestedQueued.Add(request);
            }
        }

        var active = requests
            .Where(x => x.Status is QueueStatus.Running or QueueStatus.CancelRequested)
            .OrderBy(x => x.QueueOrder)
            .ThenBy(x => x.StartedAt ?? x.CreatedAt)
            .ThenBy(x => x.Id);
        var remainingQueued = requests
            .Where(x => x.Status == QueueStatus.Queued && !requestedSet.Contains(x.Id))
            .OrderBy(x => x.QueueOrder)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id);

        var order = 1;
        foreach (var request in active.Concat(requestedQueued).Concat(remainingQueued))
        {
            request.QueueOrder = order++;
        }
    }

    public static void MoveQueuedRequestAfterActive(IEnumerable<CodexRequest> projectRequests, CodexRequest movedRequest)
    {
        ReorderQueuedAfterActive(projectRequests, new[] { movedRequest.Id });
    }

    private static bool IsVisiblePriorityRequest(CodexRequest request) =>
        request.DeletedAt is null &&
        request.ArchivedAt is null &&
        request.Status is QueueStatus.Queued or QueueStatus.Running or QueueStatus.CancelRequested;

    private static int? CompareDescending(DateTimeOffset left, DateTimeOffset right)
    {
        var comparison = right.CompareTo(left);
        return comparison == 0 ? null : comparison;
    }
}
