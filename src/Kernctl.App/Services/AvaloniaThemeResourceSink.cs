using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Kernctl.Core.Themes;

namespace Kernctl.App.Services;

public sealed class AvaloniaThemeResourceSink : IThemeResourceSink
{
    public void Apply(ThemeDefinition theme)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Apply(theme));
            return;
        }

        var resources = Application.Current?.Resources
            ?? throw new InvalidOperationException("Avalonia application resources are unavailable.");
        var colors = theme.Colors;

        SetColor(resources, "Color.Window", colors.WindowBackground);
        SetColor(resources, "Color.Sidebar", colors.SidebarBackground);
        SetColor(resources, "Color.Surface.Primary", colors.SurfacePrimary);
        SetColor(resources, "Color.Surface.Secondary", colors.SurfaceSecondary);
        SetColor(resources, "Color.Surface.Elevated", colors.SurfaceElevated);
        SetColor(resources, "Color.Border.Subtle", colors.BorderSubtle);
        SetColor(resources, "Color.Border.Strong", colors.BorderStrong);
        SetColor(resources, "Color.Text.Primary", colors.TextPrimary);
        SetColor(resources, "Color.Text.Secondary", colors.TextSecondary);
        SetColor(resources, "Color.Text.Muted", colors.TextMuted);
        SetColor(resources, "Color.Accent", colors.AccentPrimary);
        SetColor(resources, "Color.Accent.Hover", colors.AccentHover);
        SetColor(resources, "Color.Accent.Pressed", colors.AccentPressed);
        SetColor(resources, "Color.Success", colors.Success);
        SetColor(resources, "Color.Warning", colors.Warning);
        SetColor(resources, "Color.Danger", colors.Danger);

        SetBrush(resources, "Brush.Window", colors.WindowBackground);
        SetBrush(resources, "Brush.Sidebar", colors.SidebarBackground);
        SetBrush(resources, "Brush.Surface.Primary", colors.SurfacePrimary);
        SetBrush(resources, "Brush.Surface.Secondary", colors.SurfaceSecondary);
        SetBrush(resources, "Brush.Surface.Elevated", colors.SurfaceElevated);
        SetBrush(resources, "Brush.Surface.Hover", colors.SurfaceSecondary);
        SetBrush(resources, "Brush.Surface.Pressed", colors.BorderSubtle);
        SetBrush(resources, "Brush.Border.Subtle", colors.BorderSubtle);
        SetBrush(resources, "Brush.Border.Strong", colors.BorderStrong);
        SetBrush(resources, "Brush.Text.Primary", colors.TextPrimary);
        SetBrush(resources, "Brush.Text.Secondary", colors.TextSecondary);
        SetBrush(resources, "Brush.Text.Muted", colors.TextMuted);
        SetBrush(resources, "Brush.Accent", colors.AccentPrimary);
        SetBrush(resources, "Brush.Accent.Hover", colors.AccentHover);
        SetBrush(resources, "Brush.Accent.Pressed", colors.AccentPressed);
        SetBrush(resources, "Brush.Accent.Subtle", WithAlpha(colors.SelectionBackground, 0x66));
        SetBrush(resources, "Brush.Focus", colors.FocusRing);
        SetBrush(resources, "Brush.Selection", colors.SelectionBackground);
        SetBrush(resources, "Brush.Success", colors.Success);
        SetBrush(resources, "Brush.Success.Subtle", WithAlpha(colors.Success, 0x22));
        SetBrush(resources, "Brush.Warning", colors.Warning);
        SetBrush(resources, "Brush.Warning.Subtle", WithAlpha(colors.Warning, 0x22));
        SetBrush(resources, "Brush.Danger", colors.Danger);
        SetBrush(resources, "Brush.Danger.Subtle", WithAlpha(colors.Danger, 0x22));
        SetBrush(resources, "Brush.Overlay", WithAlpha(colors.WindowBackground, 0xCC));

        SetBrush(resources, "ThemeAccentBrush", colors.AccentPrimary);
        SetBrush(resources, "ThemeAccentBrush2", colors.AccentHover);
        SetBrush(resources, "ThemeAccentBrush3", colors.AccentPressed);
        SetBrush(resources, "ThemeAccentBrush4", WithAlpha(colors.AccentPrimary, 0x55));
        SetBrush(resources, "HighlightBrush", WithAlpha(colors.AccentPrimary, 0x55));
        SetBrush(resources, "HighlightBrush2", WithAlpha(colors.AccentPrimary, 0x88));
        SetBrush(resources, "HighlightForegroundBrush", colors.TextPrimary);

        resources["Font.Family"] = new FontFamily(theme.Typography.FontFamily);
        resources["Font.Scale"] = theme.Typography.Scale;
        resources["Font.Size.Display"] = 32 * theme.Typography.Scale;
        resources["Font.Size.Heading"] = 18 * theme.Typography.Scale;
        resources["Font.Size.Body"] = 14 * theme.Typography.Scale;
        resources["Font.Size.Label"] = 14 * theme.Typography.Scale;
        resources["Font.Size.Caption"] = 12 * theme.Typography.Scale;
        resources["Font.Size.Window"] = 15 * theme.Typography.Scale;
        resources["Font.Size.Brand"] = 17 * theme.Typography.Scale;
        resources["Font.Size.Navigation"] = 14 * theme.Typography.Scale;
        resources["Font.Size.ToolHeading"] = 15 * theme.Typography.Scale;
        resources["Font.Size.ToolBody"] = 12.5 * theme.Typography.Scale;
        resources["Font.Size.Profile"] = 17 * theme.Typography.Scale;
        resources["Font.Size.DialogHeading"] = 22 * theme.Typography.Scale;
        resources["Line.Height.Display"] = 38 * theme.Typography.Scale;
        resources["Line.Height.Body"] = 20 * theme.Typography.Scale;

        var radii = GetRadii(theme.CornerStyle);
        resources["Radius.Small"] = new CornerRadius(radii.Small);
        resources["Radius.Medium"] = new CornerRadius(radii.Medium);
        resources["Radius.Large"] = new CornerRadius(radii.Large);
        resources["Radius.Pill"] = new CornerRadius(999);

        resources["Control.Height"] = theme.Spacing.ControlHeight;
        resources["Card.Padding"] = new Thickness(theme.Spacing.CardPadding);
        resources["Dialog.Padding"] = new Thickness(theme.Spacing.CardPadding + 11);
        resources["NavigationItem.Height"] = theme.Spacing.NavigationItemHeight;
        resources["Page.Spacing"] = theme.Spacing.PageSpacing;
        resources["Grid.Gap"] = theme.Spacing.GridGap;
        resources["Animation.Duration"] = GetAnimationDuration(theme.Motion);
    }

    private static void SetColor(IResourceDictionary resources, string key, string value) =>
        resources[key] = Color.Parse(value);

    private static void SetBrush(IResourceDictionary resources, string key, string value) =>
        resources[key] = new SolidColorBrush(Color.Parse(value));

    private static string WithAlpha(string value, byte alpha)
    {
        if (!ThemeColor.TryParse(value, out var color))
        {
            throw new ThemeDataException($"Invalid theme colour '{value}'.");
        }

        return new ThemeColor(alpha, color.R, color.G, color.B).ToHex();
    }

    private static (double Small, double Medium, double Large) GetRadii(ThemeCornerStyle style) =>
        style switch
        {
            ThemeCornerStyle.Sharp => (0, 0, 0),
            ThemeCornerStyle.Rounded => (10, 16, 22),
            _ => (6, 10, 14),
        };

    private static TimeSpan GetAnimationDuration(ThemeMotion motion)
    {
        if (!motion.EnableAnimations)
        {
            return TimeSpan.Zero;
        }

        return motion.Intensity == ThemeMotionIntensity.Reduced
            ? TimeSpan.FromMilliseconds(60)
            : TimeSpan.FromMilliseconds(140);
    }
}
