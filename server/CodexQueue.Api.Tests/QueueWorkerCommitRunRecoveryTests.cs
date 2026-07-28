using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;

namespace CodexQueue.Api.Tests;

public sealed class QueueWorkerCommitRunRecoveryTests
{
    [Fact]
    public void FindOrRestoreRunForExecution_RecreatesMissingSeparateCommitRun()
    {
        var request = new CodexRequest
        {
            Id = Guid.NewGuid(),
            GenerateCommit = true,
            SeparateCommitSession = true,
            ExecutionRunner = ExecutionRunner.CodexCli,
            CommitExecutionRunner = ExecutionRunner.OpenHandsCli,
            CommitProviderProfileId = Guid.NewGuid(),
            CommitModel = "qwen3:30b",
            CommitModelEffort = "high",
            CommitModelSpeed = "65536",
        };
        request.Runs.Add(new CodexRun
        {
            RequestId = request.Id,
            Kind = RunKind.Request,
            Status = QueueStatus.Succeeded,
            Model = "gpt-5.3-codex-spark",
        });

        var restored = QueueWorker.FindOrRestoreRunForExecution(request, RunKind.Commit);

        Assert.NotNull(restored);
        Assert.Equal(RunKind.Commit, restored.Kind);
        Assert.Equal(QueueStatus.Queued, restored.Status);
        Assert.Equal(ExecutionRunner.OpenHandsCli, restored.ExecutionRunner);
        Assert.Equal(request.CommitProviderProfileId, restored.ProviderProfileId);
        Assert.Equal("qwen3:30b", restored.Model);
        Assert.Equal("high", restored.ModelEffort);
        Assert.Equal("65536", restored.ModelSpeed);
        Assert.Equal(2, request.Runs.Count);
    }

    [Fact]
    public void FindOrRestoreRunForExecution_DoesNotCreateCommitBeforeRequestSucceeds()
    {
        var request = new CodexRequest
        {
            Id = Guid.NewGuid(),
            GenerateCommit = true,
            SeparateCommitSession = true,
        };
        request.Runs.Add(new CodexRun
        {
            RequestId = request.Id,
            Kind = RunKind.Request,
            Status = QueueStatus.Running,
            Model = "gpt-5.3-codex-spark",
        });

        var restored = QueueWorker.FindOrRestoreRunForExecution(request, RunKind.Commit);

        Assert.Null(restored);
        Assert.Single(request.Runs);
    }

    [Fact]
    public void HasUnclaimedRunningStage_DoesNotBlockQueuedCommitHandoff()
    {
        var request = new CodexRequest
        {
            Status = QueueStatus.Running,
        };
        request.Runs.Add(new CodexRun
        {
            Kind = RunKind.Request,
            Status = QueueStatus.Succeeded,
        });
        request.Runs.Add(new CodexRun
        {
            Kind = RunKind.Commit,
            Status = QueueStatus.Queued,
        });

        Assert.False(QueueWorker.HasUnclaimedRunningStage(request));
    }

    [Theory]
    [InlineData(QueueStatus.Running)]
    [InlineData(QueueStatus.CancelRequested)]
    public void HasUnclaimedRunningStage_BlocksAnActuallyActivePersistedStage(QueueStatus runStatus)
    {
        var request = new CodexRequest
        {
            Status = QueueStatus.Running,
        };
        request.Runs.Add(new CodexRun
        {
            Kind = RunKind.Request,
            Status = runStatus,
        });

        Assert.True(QueueWorker.HasUnclaimedRunningStage(request));
    }
}
