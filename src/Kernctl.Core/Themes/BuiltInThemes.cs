namespace Kernctl.Core.Themes;

public static class BuiltInThemes
{
    public const string DefaultThemeId = "kernctl-dark";

    public static IReadOnlyList<ThemeDefinition> All { get; } =
    [
        Create(
            DefaultThemeId,
            "kernctl Dark",
            new ThemeColors
            {
                WindowBackground = "#090B0E",
                SidebarBackground = "#0C0F13",
                SurfacePrimary = "#11151A",
                SurfaceSecondary = "#151A20",
                SurfaceElevated = "#181E25",
                BorderSubtle = "#202630",
                BorderStrong = "#343C48",
                TextPrimary = "#F3F4F6",
                TextSecondary = "#A0A7B2",
                TextMuted = "#727B88",
                AccentPrimary = "#8B7CFF",
                AccentHover = "#9B8FFF",
                AccentPressed = "#6F60E8",
                Success = "#68C78A",
                Warning = "#E5B567",
                Danger = "#F06464",
                FocusRing = "#B8AFFF",
                SelectionBackground = "#2D294F",
            }),
        Create(
            "oled",
            "OLED",
            new ThemeColors
            {
                WindowBackground = "#000000",
                SidebarBackground = "#030303",
                SurfacePrimary = "#090A0C",
                SurfaceSecondary = "#0E1013",
                SurfaceElevated = "#14171B",
                BorderSubtle = "#262A31",
                BorderStrong = "#3A404A",
                TextPrimary = "#F7F7F8",
                TextSecondary = "#A9ADB5",
                TextMuted = "#747984",
                AccentPrimary = "#8B7CFF",
                AccentHover = "#A094FF",
                AccentPressed = "#6E5FE6",
                Success = "#6ECB91",
                Warning = "#E5B567",
                Danger = "#F06A6A",
                FocusRing = "#B8AFFF",
                SelectionBackground = "#292444",
            }),
        Create(
            "graphite",
            "Graphite",
            new ThemeColors
            {
                WindowBackground = "#111315",
                SidebarBackground = "#151719",
                SurfacePrimary = "#1A1D20",
                SurfaceSecondary = "#202429",
                SurfaceElevated = "#272C31",
                BorderSubtle = "#30363D",
                BorderStrong = "#48515C",
                TextPrimary = "#EEF2F6",
                TextSecondary = "#AAB2BC",
                TextMuted = "#78838F",
                AccentPrimary = "#6F91B8",
                AccentHover = "#82A3C7",
                AccentPressed = "#587A9F",
                Success = "#75B58A",
                Warning = "#C5A467",
                Danger = "#D36B6B",
                FocusRing = "#9CB9D8",
                SelectionBackground = "#263645",
            }),
        Create(
            "ember",
            "Ember",
            new ThemeColors
            {
                WindowBackground = "#100B08",
                SidebarBackground = "#15100C",
                SurfacePrimary = "#1B1410",
                SurfaceSecondary = "#231A14",
                SurfaceElevated = "#2B211A",
                BorderSubtle = "#382B22",
                BorderStrong = "#554134",
                TextPrimary = "#F5F0EB",
                TextSecondary = "#B8AAA0",
                TextMuted = "#83766D",
                AccentPrimary = "#D98A4E",
                AccentHover = "#E39A61",
                AccentPressed = "#B96E38",
                Success = "#78B887",
                Warning = "#E5B567",
                Danger = "#E06B62",
                FocusRing = "#F0B17F",
                SelectionBackground = "#4B2E1E",
            }),
    ];

    public static ThemeDefinition Default => Get(DefaultThemeId);

    public static ThemeDefinition Get(string id) =>
        All.Single(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase));

    private static ThemeDefinition Create(string id, string name, ThemeColors colors) =>
        new()
        {
            Id = id,
            Name = name,
            IsBuiltIn = true,
            Colors = colors,
        };
}
