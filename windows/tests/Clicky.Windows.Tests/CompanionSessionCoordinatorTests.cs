using Clicky.Windows.Session;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class CompanionSessionCoordinatorTests
{
    [TestMethod]
    public void SessionFlow_TransitionsThroughExpectedStates()
    {
        var coordinator = new CompanionSessionCoordinator();
        var transitions = new List<CompanionSessionStateChangedEventArgs>();
        coordinator.StateChanged += (_, eventArgs) => transitions.Add(eventArgs);

        coordinator.BeginListening();
        coordinator.BeginProcessing();
        coordinator.BeginResponding();
        coordinator.CompleteResponse();

        Assert.AreEqual(CompanionSessionState.Idle, coordinator.State);
        CollectionAssert.AreEqual(
            new[]
            {
                CompanionSessionState.Listening,
                CompanionSessionState.Processing,
                CompanionSessionState.Responding,
                CompanionSessionState.Idle
            },
            transitions.Select(transition => transition.CurrentState).ToArray());
    }

    [TestMethod]
    public void BeginListening_WhileResponding_InterruptsCurrentResponse()
    {
        var coordinator = CreateRespondingCoordinator();

        var interactionId = coordinator.BeginListening();

        Assert.AreEqual(CompanionSessionState.Listening, coordinator.State);
        Assert.AreEqual(interactionId, coordinator.CurrentInteractionId);
    }

    [TestMethod]
    public void TokenAwareTransitions_CurrentInteractionCompletesExpectedFlow()
    {
        var coordinator = new CompanionSessionCoordinator();

        var interactionId = coordinator.BeginListening();

        Assert.IsTrue(coordinator.BeginProcessing(interactionId));
        Assert.IsTrue(coordinator.BeginResponding(interactionId));
        Assert.IsTrue(coordinator.CompleteResponse(interactionId));
        Assert.AreEqual(CompanionSessionState.Idle, coordinator.State);
        Assert.IsNull(coordinator.CurrentInteractionId);
    }

    [TestMethod]
    public void TokenAwareTransition_StaleCompletionCannotAdvanceNewInteraction()
    {
        var coordinator = CreateRespondingCoordinator();
        var staleInteractionId = coordinator.CurrentInteractionId!.Value;
        var currentInteractionId = coordinator.BeginListening();

        var transitioned = coordinator.CompleteResponse(staleInteractionId);

        Assert.IsFalse(transitioned);
        Assert.AreEqual(CompanionSessionState.Listening, coordinator.State);
        Assert.AreEqual(currentInteractionId, coordinator.CurrentInteractionId);
    }

    [TestMethod]
    public void TokenAwareTransition_ResetInvalidatesActiveInteraction()
    {
        var coordinator = new CompanionSessionCoordinator();
        var interactionId = coordinator.BeginListening();

        coordinator.ResetToIdle();

        Assert.IsFalse(coordinator.BeginProcessing(interactionId));
        Assert.AreEqual(CompanionSessionState.Idle, coordinator.State);
        Assert.IsNull(coordinator.CurrentInteractionId);
    }

    [TestMethod]
    public void BeginResponding_FromIdle_ThrowsAndPreservesState()
    {
        var coordinator = new CompanionSessionCoordinator();

        Assert.ThrowsExactly<InvalidOperationException>(coordinator.BeginResponding);
        Assert.AreEqual(CompanionSessionState.Idle, coordinator.State);
    }

    [TestMethod]
    public void BeginListening_FromListening_ThrowsAndPreservesInteractionIdentity()
    {
        var coordinator = new CompanionSessionCoordinator();
        var interactionId = coordinator.BeginListening();

        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.BeginListening());
        Assert.AreEqual(CompanionSessionState.Listening, coordinator.State);
        Assert.AreEqual(interactionId, coordinator.CurrentInteractionId);
    }

    [TestMethod]
    public void ResetToIdle_FromActiveState_RaisesSingleTransition()
    {
        var coordinator = new CompanionSessionCoordinator();
        var transitionCount = 0;
        coordinator.StateChanged += (_, _) => transitionCount++;
        coordinator.BeginListening();

        coordinator.ResetToIdle();
        coordinator.ResetToIdle();

        Assert.AreEqual(CompanionSessionState.Idle, coordinator.State);
        Assert.AreEqual(2, transitionCount);
    }

    private static CompanionSessionCoordinator CreateRespondingCoordinator()
    {
        var coordinator = new CompanionSessionCoordinator();
        coordinator.BeginListening();
        coordinator.BeginProcessing();
        coordinator.BeginResponding();
        return coordinator;
    }
}
