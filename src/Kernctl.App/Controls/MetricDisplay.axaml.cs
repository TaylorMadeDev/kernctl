using Avalonia;
using Avalonia.Controls;

namespace Kernctl.App.Controls;

public sealed partial class MetricDisplay : UserControl
{
    public static readonly StyledProperty<string> IconProperty =
        AvaloniaProperty.Register<MetricDisplay, string>(nameof(Icon), string.Empty);

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<MetricDisplay, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<MetricDisplay, string>(nameof(Value), string.Empty);

    public MetricDisplay() => InitializeComponent();

    public string Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
