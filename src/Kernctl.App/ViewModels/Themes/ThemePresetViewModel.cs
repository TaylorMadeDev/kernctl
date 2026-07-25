using Kernctl.Core.Themes;

namespace Kernctl.App.ViewModels.Themes;

public sealed record ThemePresetViewModel(ThemeDefinition Theme)
{
    public string Name => Theme.Name;

    public string Accent => Theme.Colors.AccentPrimary;

    public string Background => Theme.Colors.WindowBackground;

    public string Surface => Theme.Colors.SurfacePrimary;

    public bool IsBuiltIn => Theme.IsBuiltIn;
}
