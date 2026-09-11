namespace GRBKP.Core.Operations;

public sealed class PackageStateMachine(PackageState initialState = PackageState.Created)
{
    public PackageState Current { get; private set; } = initialState;

    public void TransitionTo(PackageState next)
    {
        if (!AllowedTransitions.TryGetValue(Current, out var allowed) || !allowed.Contains(next))
        {
            throw new InvalidStateTransitionException(Current.ToString(), next.ToString());
        }

        Current = next;
    }

    private static readonly IReadOnlyDictionary<PackageState, HashSet<PackageState>>
        AllowedTransitions = new Dictionary<PackageState, HashSet<PackageState>>
        {
            [PackageState.Created] = [PackageState.Inspecting, PackageState.Failed],
            [PackageState.Inspecting] =
                [PackageState.Copying, PackageState.Paused, PackageState.Incomplete, PackageState.Failed],
            [PackageState.Copying] =
                [PackageState.Verifying, PackageState.Paused, PackageState.Incomplete, PackageState.Failed],
            [PackageState.Verifying] =
                [PackageState.Complete, PackageState.Incomplete, PackageState.Failed],
            [PackageState.Paused] =
                [PackageState.Inspecting, PackageState.Copying, PackageState.Verifying, PackageState.Failed],
            [PackageState.Incomplete] =
                [PackageState.Inspecting, PackageState.Copying, PackageState.Verifying, PackageState.Failed],
            [PackageState.Complete] = [],
            [PackageState.Failed] = []
        };
}

public sealed class RestoreStateMachine(RestoreState initialState = RestoreState.Preflight)
{
    public RestoreState Current { get; private set; } = initialState;

    public void TransitionTo(RestoreState next)
    {
        if (next == RestoreState.Paused && !IsTerminal(Current))
        {
            Current = next;
            return;
        }

        if (Current == RestoreState.Paused)
        {
            if (IsTerminal(next) || next == RestoreState.Preflight)
            {
                throw new InvalidStateTransitionException(Current.ToString(), next.ToString());
            }

            Current = next;
            return;
        }

        if (!AllowedTransitions.TryGetValue(Current, out var allowed) || !allowed.Contains(next))
        {
            throw new InvalidStateTransitionException(Current.ToString(), next.ToString());
        }

        Current = next;
    }

    public static bool IsTerminal(RestoreState state) =>
        state is RestoreState.Complete or
            RestoreState.PhysicalValidationPending or
            RestoreState.Failed;

    private static readonly IReadOnlyDictionary<RestoreState, HashSet<RestoreState>>
        AllowedTransitions = new Dictionary<RestoreState, HashSet<RestoreState>>
        {
            [RestoreState.Preflight] = [RestoreState.Confirmed, RestoreState.Failed],
            [RestoreState.Confirmed] = [RestoreState.ForceClosing, RestoreState.Failed],
            [RestoreState.ForceClosing] = [RestoreState.RestoringUserData, RestoreState.Failed],
            [RestoreState.RestoringUserData] = [RestoreState.RestoringAppModules, RestoreState.Failed],
            [RestoreState.RestoringAppModules] = [RestoreState.RestoringFileZilla, RestoreState.Failed],
            [RestoreState.RestoringFileZilla] = [RestoreState.RestoringPrinters, RestoreState.Failed],
            [RestoreState.RestoringPrinters] = [RestoreState.ApplyingMachineSettings, RestoreState.Failed],
            [RestoreState.ApplyingMachineSettings] = [RestoreState.CoreMigrationApplied, RestoreState.Failed],
            [RestoreState.CoreMigrationApplied] = [RestoreState.AwaitingCleanupChoice, RestoreState.Failed],
            [RestoreState.AwaitingCleanupChoice] =
                [RestoreState.ApplyingCorporateCleanup, RestoreState.CleanupSkipped, RestoreState.Failed],
            [RestoreState.ApplyingCorporateCleanup] = [RestoreState.RebootPending, RestoreState.Failed],
            [RestoreState.CleanupSkipped] = [RestoreState.RebootPending, RestoreState.Failed],
            [RestoreState.RebootPending] = [RestoreState.PostBootValidation, RestoreState.Failed],
            [RestoreState.PostBootValidation] =
                [RestoreState.Complete, RestoreState.PhysicalValidationPending, RestoreState.Failed],
            [RestoreState.Complete] = [],
            [RestoreState.PhysicalValidationPending] = [],
            [RestoreState.Failed] = [],
            [RestoreState.Paused] = []
        };
}

public sealed class InvalidStateTransitionException(string current, string requested)
    : InvalidOperationException($"Transição de estado inválida: {current} → {requested}.")
{
    public string CurrentState { get; } = current;

    public string RequestedState { get; } = requested;
}
