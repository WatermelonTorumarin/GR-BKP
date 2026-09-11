using GRBKP.Core.Adapters;
using GRBKP.Core.Operations;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class PostBootResumeTests
{
    [Fact]
    public async Task T35_RebootResume_UsesDurableCheckpoint_IsIdempotent_AndRemovesResumeTaskAtTerminalState()
    {
        using var fixture = new TemporaryOperationDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 11, 17, 0, 0, TimeSpan.Zero));
        var reboot = new RebootAdapterFake();
        var operationId = Guid.Parse("30303030-3030-3030-3030-303030303030");
        var checkpoint = new OperationCheckpoint(
            operationId,
            Guid.Parse("40404040-4040-4040-4040-404040404040"),
            OperationKind.Restore,
            RestoreState.CleanupSkipped.ToString(),
            null,
            42,
            "data/Users/Ana/Documents/final.txt",
            new DurableCounters(42, 4_200, 0, 1),
            UpdatedAtUtc: clock.UtcNow);
        var firstComposition = new PostBootResumeModule(fileSystem, reboot, clock);

        var scheduled = await firstComposition.ScheduleAndRequestRebootAsync(
            fixture.Root,
            checkpoint);

        Assert.Equal(RestoreState.RebootPending.ToString(), scheduled.State);
        Assert.True(scheduled.ResumeTaskPending);
        Assert.Contains(operationId, reboot.ScheduledOperations);
        Assert.True(reboot.RebootRequested);

        var continuationExecutions = 0;
        var secondComposition = new PostBootResumeModule(fileSystem, reboot, clock);
        var completed = await secondComposition.ResumeAfterBootAsync(
            fixture.Root,
            (durableCheckpoint, _) =>
            {
                continuationExecutions++;
                Assert.Equal(42, durableCheckpoint.LastConfirmedSequence);
                return Task.FromResult(new PostBootContinuationResult(
                    RestoreState.Complete,
                    durableCheckpoint.Counters));
            });

        Assert.Equal(RestoreState.Complete.ToString(), completed.State);
        Assert.False(completed.ResumeTaskPending);
        Assert.Empty(reboot.ScheduledOperations);
        Assert.Equal(1, continuationExecutions);

        var thirdComposition = new PostBootResumeModule(fileSystem, reboot, clock);
        var repeated = await thirdComposition.ResumeAfterBootAsync(
            fixture.Root,
            (_, _) =>
            {
                continuationExecutions++;
                throw new InvalidOperationException("Estado terminal não pode executar novamente.");
            });

        Assert.Equal(RestoreState.Complete.ToString(), repeated.State);
        Assert.Equal(1, continuationExecutions);
        Assert.Empty(reboot.ScheduledOperations);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RebootAdapterFake : IRebootAdapter
    {
        public HashSet<Guid> ScheduledOperations { get; } = [];

        public bool RebootRequested { get; private set; }

        public Task ScheduleResumeAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            ScheduledOperations.Add(operationId);
            return Task.CompletedTask;
        }

        public Task RequestRebootAsync(CancellationToken cancellationToken = default)
        {
            RebootRequested = true;
            return Task.CompletedTask;
        }

        public Task RemoveResumeTaskAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            ScheduledOperations.Remove(operationId);
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryOperationDirectory : IDisposable
    {
        public TemporaryOperationDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "grbkp-phase1-operation", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
