using System.Diagnostics;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Kernctl.App.Controls;

/// <summary>Renders simple local SVG path resources as scalable Avalonia geometry.</summary>
public sealed class SvgIcon : Viewbox
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<SvgIcon, string?>(nameof(Source));

    public static readonly StyledProperty<IBrush?> IconBrushProperty =
        AvaloniaProperty.Register<SvgIcon, IBrush?>(nameof(IconBrush));

    private readonly Avalonia.Controls.Shapes.Path path = new();

    public SvgIcon()
    {
        Stretch = Stretch.Uniform;
        Child = path;
        AffectsIcon(SourceProperty, IconBrushProperty);
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IBrush? IconBrush
    {
        get => GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    private static void AffectsIcon(params AvaloniaProperty[] properties)
    {
        foreach (var property in properties)
        {
            property.Changed.AddClassHandler<SvgIcon>((icon, _) => icon.UpdateIcon());
        }
    }

    private void UpdateIcon()
    {
        path.Fill = IconBrush;

        if (string.IsNullOrWhiteSpace(Source))
        {
            path.Data = CreateFallback();
            return;
        }

        try
        {
            using var stream = AssetLoader.Open(new Uri(Source, UriKind.Absolute));
            var document = XDocument.Load(stream);
            var pathData = document
                .Descendants()
                .Where(element => element.Name.LocalName == "path")
                .Select(element => element.Attribute("d")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value));

            var combined = string.Join(" ", pathData);
            path.Data = string.IsNullOrWhiteSpace(combined)
                ? CreateFallback()
                : Geometry.Parse(combined);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or UriFormatException
            or System.Xml.XmlException
            or FormatException)
        {
            Trace.TraceWarning("Unable to load SVG icon '{0}': {1}", Source, exception.Message);
            path.Data = CreateFallback();
        }
    }

    private static Geometry CreateFallback() =>
        Geometry.Parse("M2,2 L14,2 L14,14 L2,14 Z M4,4 L12,12 M12,4 L4,12");
}
