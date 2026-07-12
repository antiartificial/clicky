namespace Clicky.Windows.Input;

public enum PushToTalkKey
{
    F13 = 0x7C,
    F14 = 0x7D,
    F15 = 0x7E,
    F16 = 0x7F,
    F17 = 0x80,
    F18 = 0x81,
    F19 = 0x82,
    F20 = 0x83,
    F21 = 0x84,
    F22 = 0x85,
    F23 = 0x86,
    F24 = 0x87,
}

public enum PushToTalkTransitionKind
{
    Pressed,
    Released,
}

public readonly record struct PushToTalkTransition(
    PushToTalkKey Key,
    PushToTalkTransitionKind Kind);

internal static class PushToTalkKeyValidator
{
    public static void Validate(PushToTalkKey key, string parameterName)
    {
        if (key < PushToTalkKey.F13 || key > PushToTalkKey.F24)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                key,
                "Push-to-talk keys must be between F13 and F24.");
        }
    }
}
