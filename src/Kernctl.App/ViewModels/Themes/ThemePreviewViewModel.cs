using Kernctl.App.Services;

namespace Kernctl.App.ViewModels.Themes;

public sealed class ThemePreviewViewModel
{
    public ToolCardViewModel PreviewTool { get; } = new(
        "Performance Mode",
        "Representative kernctl tool card",
        IconCatalog.Optimize,
        hasToggle: true,
        initialToggleState: true);
}
