namespace Clicky.Windows.Session;

public sealed class CompanionSessionCoordinator
{
    private long nextInteractionId;
    private CompanionInteractionId? currentInteractionId;

    public CompanionSessionState State { get; private set; } = CompanionSessionState.Idle;

    public CompanionInteractionId? CurrentInteractionId => currentInteractionId;

    public event EventHandler<CompanionSessionStateChangedEventArgs>? StateChanged;

    public CompanionInteractionId BeginListening()
    {
        EnsureTransitionAllowed(
            CompanionSessionState.Listening,
            CompanionSessionState.Idle,
            CompanionSessionState.Responding);

        var interactionId = new CompanionInteractionId(++nextInteractionId);
        currentInteractionId = interactionId;
        TransitionTo(CompanionSessionState.Listening, CompanionSessionState.Idle, CompanionSessionState.Responding);
        return interactionId;
    }

    public void BeginProcessing()
    {
        TransitionTo(CompanionSessionState.Processing, CompanionSessionState.Listening);
    }

    public bool BeginProcessing(CompanionInteractionId interactionId)
    {
        return TransitionCurrentInteractionTo(
            interactionId,
            CompanionSessionState.Processing,
            CompanionSessionState.Listening);
    }

    public void BeginResponding()
    {
        TransitionTo(CompanionSessionState.Responding, CompanionSessionState.Processing);
    }

    public bool BeginResponding(CompanionInteractionId interactionId)
    {
        return TransitionCurrentInteractionTo(
            interactionId,
            CompanionSessionState.Responding,
            CompanionSessionState.Processing);
    }

    public void CompleteResponse()
    {
        CompleteCurrentResponse();
    }

    public bool CompleteResponse(CompanionInteractionId interactionId)
    {
        if (currentInteractionId != interactionId)
        {
            return false;
        }

        CompleteCurrentResponse();
        return true;
    }

    public void ResetToIdle()
    {
        if (State == CompanionSessionState.Idle)
        {
            return;
        }

        var previousState = State;
        State = CompanionSessionState.Idle;
        currentInteractionId = null;
        StateChanged?.Invoke(this, new CompanionSessionStateChangedEventArgs(previousState, State));
    }

    private bool TransitionCurrentInteractionTo(
        CompanionInteractionId interactionId,
        CompanionSessionState nextState,
        params CompanionSessionState[] allowedCurrentStates)
    {
        if (currentInteractionId != interactionId)
        {
            return false;
        }

        TransitionTo(nextState, allowedCurrentStates);
        return true;
    }

    private void TransitionTo(
        CompanionSessionState nextState,
        params CompanionSessionState[] allowedCurrentStates)
    {
        EnsureTransitionAllowed(nextState, allowedCurrentStates);

        var previousState = State;
        State = nextState;
        StateChanged?.Invoke(this, new CompanionSessionStateChangedEventArgs(previousState, nextState));
    }

    private void CompleteCurrentResponse()
    {
        EnsureTransitionAllowed(CompanionSessionState.Idle, CompanionSessionState.Responding);

        var previousState = State;
        State = CompanionSessionState.Idle;
        currentInteractionId = null;
        StateChanged?.Invoke(this, new CompanionSessionStateChangedEventArgs(previousState, State));
    }

    private void EnsureTransitionAllowed(
        CompanionSessionState nextState,
        params CompanionSessionState[] allowedCurrentStates)
    {
        if (!allowedCurrentStates.Contains(State))
        {
            throw new InvalidOperationException($"Cannot transition companion session from {State} to {nextState}.");
        }
    }
}

public readonly record struct CompanionInteractionId(long Value);

public sealed class CompanionSessionStateChangedEventArgs(
    CompanionSessionState previousState,
    CompanionSessionState currentState) : EventArgs
{
    public CompanionSessionState PreviousState { get; } = previousState;

    public CompanionSessionState CurrentState { get; } = currentState;
}
