using System.Globalization;
using System.Text.RegularExpressions;

namespace LNMirror.Core.Scrcpy;

public readonly record struct ScrcpyFrameSample(double RenderedFps, int SkippedFrames)
{
    private static readonly Regex FrameRateLine = new(
        @"^\s*(?:INFO:\s*)?(?:(?<fps>[0-9]+(?:\.[0-9]+)?)\s*fps|(?:fps|frame\s*rate)\s*[:=]?\s*(?<fps>[0-9]+(?:\.[0-9]+)?))(?:\s+\(\+(?<skipped>[0-9]+)\s+frames?\s+skipped\))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryParse(string line, out ScrcpyFrameSample sample)
    {
        sample = default;
        var match = FrameRateLine.Match(line);
        if (!match.Success
            || !double.TryParse(match.Groups["fps"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var fps)
            || !double.IsFinite(fps))
        {
            return false;
        }

        var skippedGroup = match.Groups["skipped"];
        int skipped = 0;
        if (skippedGroup.Success && !int.TryParse(skippedGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out skipped))
        {
            return false;
        }

        sample = new ScrcpyFrameSample(fps, skipped);
        return true;
    }
}
