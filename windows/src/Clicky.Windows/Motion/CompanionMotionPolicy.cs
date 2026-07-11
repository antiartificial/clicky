using System.Windows;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Motion;

public static class CompanionMotionPolicy
{
    public static bool IsEnabled(CompanionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.MotionEffectsEnabled && SystemParameters.ClientAreaAnimation;
    }
}
