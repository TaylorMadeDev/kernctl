namespace Kernctl.Core.Themes;

public static class ThemeContrast
{
    public static double CalculateRatio(string foreground, string background)
    {
        if (!ThemeColor.TryParse(foreground, out var foregroundColor)
            || !ThemeColor.TryParse(background, out var backgroundColor))
        {
            throw new ArgumentException("Contrast colours must use #RRGGBB or #AARRGGBB format.");
        }

        var foregroundLuminance = RelativeLuminance(Composite(foregroundColor, backgroundColor));
        var backgroundLuminance = RelativeLuminance(backgroundColor);
        var lighter = Math.Max(foregroundLuminance, backgroundLuminance);
        var darker = Math.Min(foregroundLuminance, backgroundLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static IReadOnlyList<ContrastIssue> Evaluate(ThemeColors colors)
    {
        var checks = new[]
        {
            Check("Primary text on window background", colors.TextPrimary, colors.WindowBackground, 4.5),
            Check("Primary text on primary surface", colors.TextPrimary, colors.SurfacePrimary, 4.5),
            Check("Secondary text on primary surface", colors.TextSecondary, colors.SurfacePrimary, 4.5),
            Check("Accent on primary surface", colors.AccentPrimary, colors.SurfacePrimary, 3),
            Check("Button text on accent", colors.TextPrimary, colors.AccentPrimary, 4.5),
        };

        return checks.Where(issue => issue is not null).Cast<ContrastIssue>().ToArray();
    }

    private static ContrastIssue? Check(string label, string foreground, string background, double minimum)
    {
        var ratio = CalculateRatio(foreground, background);
        return ratio < minimum ? new ContrastIssue(label, ratio, minimum) : null;
    }

    private static ThemeColor Composite(ThemeColor foreground, ThemeColor background)
    {
        if (foreground.A == 255)
        {
            return foreground;
        }

        var alpha = foreground.A / 255d;
        return new ThemeColor(
            255,
            (byte)Math.Round((foreground.R * alpha) + (background.R * (1 - alpha))),
            (byte)Math.Round((foreground.G * alpha) + (background.G * (1 - alpha))),
            (byte)Math.Round((foreground.B * alpha) + (background.B * (1 - alpha))));
    }

    private static double RelativeLuminance(ThemeColor color) =>
        (0.2126 * Linearize(color.R)) + (0.7152 * Linearize(color.G)) + (0.0722 * Linearize(color.B));

    private static double Linearize(byte component)
    {
        var channel = component / 255d;
        return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }
}

public sealed record ContrastIssue(string Combination, double ActualRatio, double RequiredRatio)
{
    public string Message =>
        $"{Combination} is {ActualRatio:F1}:1; at least {RequiredRatio:F1}:1 is recommended.";
}
