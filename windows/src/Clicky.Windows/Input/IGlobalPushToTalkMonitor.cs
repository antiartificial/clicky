namespace Clicky.Windows.Input;

public interface IGlobalPushToTalkMonitor : IDisposable
{
    PushToTalkKey SelectedKey { get; set; }

    event EventHandler<PushToTalkTransitionEventArgs>? Transitioned;
}

public sealed class PushToTalkTransitionEventArgs(PushToTalkTransition transition) : EventArgs
{
    public PushToTalkTransition Transition { get; } = transition;
}
