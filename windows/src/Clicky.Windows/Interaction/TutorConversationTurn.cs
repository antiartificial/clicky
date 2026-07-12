using Clicky.Windows.Pointing;

namespace Clicky.Windows.Interaction;

public sealed record TutorConversationTurn(
    string UserText,
    string AssistantText,
    DesktopPoint? MappedDesktopPoint = null,
    string? TargetLabel = null)
{
    public bool HasInteractionTarget => MappedDesktopPoint is not null;
}
