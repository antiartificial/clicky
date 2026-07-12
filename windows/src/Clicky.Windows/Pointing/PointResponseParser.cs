using System.Globalization;
using System.Text.RegularExpressions;

namespace Clicky.Windows.Pointing;

public static partial class PointResponseParser
{
    public static PointResponse Parse(string responseText)
    {
        ArgumentNullException.ThrowIfNull(responseText);

        var match = PointDirectiveRegex().Match(responseText);
        if (!match.Success)
        {
            return new PointResponse(responseText, Target: null, HasPointDirective: false);
        }

        var spokenText = responseText[..match.Index].TrimEnd();
        if (match.Groups["none"].Success)
        {
            return new PointResponse(spokenText, Target: null, HasPointDirective: true);
        }

        var x = double.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture);
        var y = double.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        var elementLabel = match.Groups["label"].Success
            ? match.Groups["label"].Value.Trim()
            : null;
        int? screenNumber = match.Groups["screen"].Success
            ? int.Parse(match.Groups["screen"].Value, CultureInfo.InvariantCulture)
            : null;

        if (screenNumber is < 1)
        {
            return new PointResponse(spokenText, Target: null, HasPointDirective: true);
        }

        var target = new PointTarget(new ImagePixelPoint(x, y), elementLabel, screenNumber);
        return new PointResponse(spokenText, target, HasPointDirective: true);
    }

    [GeneratedRegex(
        @"\[POINT:(?:(?<none>none)|(?<x>\d+)\s*,\s*(?<y>\d+)(?::(?<label>[^\]:\s][^\]:]*?))?(?::screen(?<screen>\d+))?)\]\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PointDirectiveRegex();
}
