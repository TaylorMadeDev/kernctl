using Avalonia.Controls;
using Kernctl.App.ViewModels.Themes;

namespace Kernctl.App.Controls;

public sealed partial class ThemePreview : UserControl
{
    public ThemePreview()
    {
        InitializeComponent();
        DataContext = new ThemePreviewViewModel();
    }
}
