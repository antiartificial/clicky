namespace Clicky.Windows.Input;

internal sealed class PushToTalkTransitionTracker
{
    private PushToTalkKey selectedKey;
    private bool isSelectedKeyPressed;

    public PushToTalkTransitionTracker(PushToTalkKey selectedKey = PushToTalkKey.F13)
    {
        PushToTalkKeyValidator.Validate(selectedKey, nameof(selectedKey));
        this.selectedKey = selectedKey;
    }

    public PushToTalkKey SelectedKey => selectedKey;

    public PushToTalkTransition? ProcessKeyEvent(int virtualKeyCode, bool isKeyDown)
    {
        if (virtualKeyCode != (int)selectedKey || isSelectedKeyPressed == isKeyDown)
        {
            return null;
        }

        isSelectedKeyPressed = isKeyDown;
        return new PushToTalkTransition(
            selectedKey,
            isKeyDown
                ? PushToTalkTransitionKind.Pressed
                : PushToTalkTransitionKind.Released);
    }

    public PushToTalkTransition? ChangeSelectedKey(PushToTalkKey newSelectedKey)
    {
        PushToTalkKeyValidator.Validate(newSelectedKey, nameof(newSelectedKey));
        if (newSelectedKey == selectedKey)
        {
            return null;
        }

        PushToTalkTransition? releaseTransition = isSelectedKeyPressed
            ? new PushToTalkTransition(selectedKey, PushToTalkTransitionKind.Released)
            : null;

        selectedKey = newSelectedKey;
        isSelectedKeyPressed = false;
        return releaseTransition;
    }

    public PushToTalkTransition? ReleaseIfPressed()
    {
        if (!isSelectedKeyPressed)
        {
            return null;
        }

        isSelectedKeyPressed = false;
        return new PushToTalkTransition(selectedKey, PushToTalkTransitionKind.Released);
    }
}
