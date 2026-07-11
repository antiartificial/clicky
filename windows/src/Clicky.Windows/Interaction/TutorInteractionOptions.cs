namespace Clicky.Windows.Interaction;

public sealed class TutorInteractionOptions
{
    public const string DefaultModel = "claude-sonnet-4-6";

    public string Model { get; init; } = DefaultModel;
}
