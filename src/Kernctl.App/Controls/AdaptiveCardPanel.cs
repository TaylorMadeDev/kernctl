using Avalonia;
using Avalonia.Controls;

namespace Kernctl.App.Controls;

/// <summary>
/// Lays out cards in up to three equal columns and drops columns when the available
/// width would make a card narrower than its usable minimum.
/// </summary>
public sealed class AdaptiveCardPanel : Panel
{
    public static readonly StyledProperty<double> HorizontalSpacingProperty =
        AvaloniaProperty.Register<AdaptiveCardPanel, double>(nameof(HorizontalSpacing), 14);

    public static readonly StyledProperty<double> VerticalSpacingProperty =
        AvaloniaProperty.Register<AdaptiveCardPanel, double>(nameof(VerticalSpacing), 14);

    public static readonly StyledProperty<double> MinimumItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveCardPanel, double>(nameof(MinimumItemWidth), 260);

    public static readonly StyledProperty<int> MaximumColumnsProperty =
        AvaloniaProperty.Register<AdaptiveCardPanel, int>(nameof(MaximumColumns), 3);

    static AdaptiveCardPanel()
    {
        AffectsMeasure<AdaptiveCardPanel>(
            HorizontalSpacingProperty,
            VerticalSpacingProperty,
            MinimumItemWidthProperty,
            MaximumColumnsProperty);
    }

    public double HorizontalSpacing
    {
        get => GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public double MinimumItemWidth
    {
        get => GetValue(MinimumItemWidthProperty);
        set => SetValue(MinimumItemWidthProperty, value);
    }

    public int MaximumColumns
    {
        get => GetValue(MaximumColumnsProperty);
        set => SetValue(MaximumColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
        {
            return default;
        }

        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width)
            : GetNaturalWidth();
        var columns = CalculateColumnCount(width);
        var itemWidth = CalculateItemWidth(width, columns);
        var rowHeights = new double[(Children.Count + columns - 1) / columns];

        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            var row = index / columns;
            rowHeights[row] = Math.Max(rowHeights[row], child.DesiredSize.Height);
        }

        var height = rowHeights.Sum()
            + (Math.Max(0, rowHeights.Length - 1) * VerticalSpacing);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Max(0, finalSize.Width);
        var columns = CalculateColumnCount(width);
        var itemWidth = CalculateItemWidth(width, columns);
        var rowHeights = new double[(Children.Count + columns - 1) / columns];

        for (var index = 0; index < Children.Count; index++)
        {
            var row = index / columns;
            rowHeights[row] = Math.Max(rowHeights[row], Children[index].DesiredSize.Height);
        }

        var y = 0d;
        for (var row = 0; row < rowHeights.Length; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = (row * columns) + column;
                if (index >= Children.Count)
                {
                    break;
                }

                var x = column * (itemWidth + HorizontalSpacing);
                Children[index].Arrange(new Rect(x, y, itemWidth, rowHeights[row]));
            }

            y += rowHeights[row] + VerticalSpacing;
        }

        return finalSize;
    }

    private int CalculateColumnCount(double width)
    {
        var maximum = Math.Max(1, MaximumColumns);
        var minimumWidth = Math.Max(1, MinimumItemWidth);
        var spacing = Math.Max(0, HorizontalSpacing);
        var columns = (int)Math.Floor((width + spacing) / (minimumWidth + spacing));
        return Math.Clamp(columns, 1, maximum);
    }

    private double CalculateItemWidth(double width, int columns)
    {
        var totalSpacing = Math.Max(0, columns - 1) * Math.Max(0, HorizontalSpacing);
        return Math.Max(0, (width - totalSpacing) / columns);
    }

    private double GetNaturalWidth()
    {
        var columns = Math.Max(1, MaximumColumns);
        return (Math.Max(1, MinimumItemWidth) * columns)
            + (Math.Max(0, columns - 1) * Math.Max(0, HorizontalSpacing));
    }
}
