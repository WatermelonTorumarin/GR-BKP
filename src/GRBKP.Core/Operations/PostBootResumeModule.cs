using GRBKP.Core.Adapters;

namespace GRBKP.Core.Operations;

public sealed record PostBootContinuationResult(
    RestoreState State,
    DurableCounters Counters);

public sealed class PostBootResumeModule(
    IFileSystemAdapter fileSystem,
    IRebootAdapter rebootAdapter,
    IClock clock)
{
    private readonly OperationCheckpointStore checkpointStore = new(fileSystem, clock);

    public async Task<OperationCheckpoint> ScheduleAndRequestRebootAsync(
        string operationRoot,
        OperationCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        if (checkpoint.Kind != OperationKind.Restore)
        {
            throw new ArgumentException("Somente restaurações podem agendar retomada pós-boot.",
                nameof(checkpoint));
        }

        var current = Enum.Parse<RestoreState>(checkpoint.State, ignoreCase: true);
        var machine = new RestoreStateMachine(current);
        machine.TransitionTo(RestoreState.RebootPending);
        var scheduled = checkpoint with
        {
            State = RestoreState.RebootPending.ToString(),
            ResumeState = RestoreState.PostBootValidation.ToString(),
            ResumeTaskPending = true
        };
        await checkpointStore.SaveAsync(operationRoot, scheduled, cancellationToken)
            .ConfigureAwait(false);
        await rebootAdapter.ScheduleResumeAsync(checkpoint.OperationId, cancellationToken)
            .ConfigureAwait(false);
        await rebootAdapter.RequestRebootAsync(cancellationToken).ConfigureAwait(false);
        return scheduled;
    }

    public async Task<OperationCheckpoint> ResumeAfterBootAsync(
        string operationRoot,
        Func<OperationCheckpoint, CancellationToken, Task<PostBootContinuationResult>> continuation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var checkpoint = await checkpointStore.ReadAsync(operationRoot, cancellationToken)
            .ConfigureAwait(false);
        var state = Enum.Parse<RestoreState>(checkpoint.State, ignoreCase: true);
        if (RestoreStateMachine.IsTerminal(state))
        {
            await rebootAdapter.RemoveResumeTaskAsync(
                checkpoint.OperationId,
                cancellationToken).ConfigureAwait(false);
            return checkpoint;
        }

        var machine = new RestoreStateMachine(state);
        machine.TransitionTo(RestoreState.PostBootValidation);
        var validating = checkpoint with
        {
            State = RestoreState.PostBootValidation.ToString(),
            ResumeState = null,
            ResumeTaskPending = true
        };
        await checkpointStore.SaveAsync(operationRoot, validating, cancellationToken)
            .ConfigureAwait(false);

        var outcome = await continuation(validating, cancellationToken).ConfigureAwait(false);
        if (outcome.State is not (RestoreState.Complete or
            RestoreState.PhysicalValidationPending or
            RestoreState.Failed))
        {
            throw new InvalidOperationException("A continuação pós-boot deve retornar estado terminal.");
        }

        machine.TransitionTo(outcome.State);
        var completed = validating with
        {
            State = outcome.State.ToString(),
            Counters = outcome.Counters,
            ResumeTaskPending = false
        };
        await checkpointStore.SaveAsync(operationRoot, completed, cancellationToken)
            .ConfigureAwait(false);
        await rebootAdapter.RemoveResumeTaskAsync(
            checkpoint.OperationId,
            cancellationToken).ConfigureAwait(false);
        return completed;
    }
}
