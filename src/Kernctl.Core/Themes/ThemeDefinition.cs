namespace Kernctl.Core.Themes;

public enum ThemeCornerStyle
{
    Sharp,
    Subtle,
    Rounded,
}

public enum ThemeDensity
{
    Compact,
    Comfortable,
    Spacious,
}

public enum ThemeMotionIntensity
{
    Reduced,
    Standard,
}

public sealed record ThemeDefinition
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public bool IsBuiltIn { get; init; }

    public string? BaseThemeId { get; init; }

    public required ThemeColors Colors { get; init; }

    public ThemeTypography Typography { get; init; } = new();

    public ThemeSpacing Spacing { get; init; } = ThemeSpacing.Comfortable;

    public ThemeCornerStyle CornerStyle { get; init; } = ThemeCornerStyle.Subtle;

    public ThemeDensity Density { get; init; } = ThemeDensity.Comfortable;

    public ThemeMotion Motion { get; init; } = new();

    public ThemeDefinition CreateEditableCopy(string id, string name) =>
        this with
        {
            Id = id,
            Name = name,
            IsBuiltIn = false,
            BaseThemeId = IsBuiltIn ? Id : BaseThemeId,
            Colors = Colors with { },
            Typography = Typography with { },
            Spacing = Spacing with { },
            Motion = Motion with { },
        };
}

public sealed record ThemeColors
{
    public required string WindowBackground { get; init; }

    public required string SidebarBackground { get; init; }

    public required string SurfacePrimary { get; init; }

    public required string SurfaceSecondary { get; init; }

    public required string SurfaceElevated { get; init; }

    public required string BorderSubtle { get; init; }

    public required string BorderStrong { get; init; }

    public required string TextPrimary { get; init; }

    public required string TextSecondary { get; init; }

    public required string TextMuted { get; init; }

    public required string AccentPrimary { get; init; }

    public required string AccentHover { get; init; }

    public required string AccentPressed { get; init; }

    public required string Success { get; init; }

    public required string Warning { get; init; }

    public required string Danger { get; init; }

    public required string FocusRing { get; init; }

    public required string SelectionBackground { get; init; }
}

public sealed record ThemeTypography
{
    public string FontFamily { get; init; } = "Segoe UI Variable, Segoe UI";

    public double Scale { get; init; } = 1;
}

public sealed record ThemeSpacing
{
    public static ThemeSpacing Compact { get; } = new()
    {
        ControlHeight = 34,
        CardPadding = 14,
        NavigationItemHeight = 46,
        PageSpacing = 18,
        GridGap = 10,
    };

    public static ThemeSpacing Comfortable { get; } = new()
    {
        ControlHeight = 38,
        CardPadding = 17,
        NavigationItemHeight = 52,
        PageSpacing = 22,
        GridGap = 14,
    };

    public static ThemeSpacing Spacious { get; } = new()
    {
        ControlHeight = 44,
        CardPadding = 22,
        NavigationItemHeight = 60,
        PageSpacing = 28,
        GridGap = 18,
    };

    public double ControlHeight { get; init; } = 38;

    public double CardPadding { get; init; } = 17;

    public double NavigationItemHeight { get; init; } = 52;

    public double PageSpacing { get; init; } = 22;

    public double GridGap { get; init; } = 14;

    public static ThemeSpacing ForDensity(ThemeDensity density) => density switch
    {
        ThemeDensity.Compact => Compact with { },
        ThemeDensity.Spacious => Spacious with { },
        _ => Comfortable with { },
    };
}

public sealed record ThemeMotion
{
    public bool EnableAnimations { get; init; } = true;

    public ThemeMotionIntensity Intensity { get; init; } = ThemeMotionIntensity.Standard;

    public bool FollowSystemPreference { get; init; }
}
