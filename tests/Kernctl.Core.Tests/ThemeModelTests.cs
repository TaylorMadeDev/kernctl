using Kernctl.Core.Themes;

namespace Kernctl.Core.Tests;

public sealed class ThemeModelTests
{
    [Fact]
    public void BuiltInThemesAreValidAndImmutable()
    {
        Assert.Equal(4, BuiltInThemes.All.Count);
        Assert.All(BuiltInThemes.All, theme =>
        {
            Assert.True(theme.IsBuiltIn);
            Assert.Empty(ThemeValidation.Validate(theme));
        });
    }

    [Theory]
    [InlineData("#8B7CFF", 255, 139, 124, 255)]
    [InlineData("#808B7CFF", 128, 139, 124, 255)]
    public void ColorParserSupportsRgbAndArgb(
        string value,
        byte alpha,
        byte red,
        byte green,
        byte blue)
    {
        Assert.True(ThemeColor.TryParse(value, out var color));
        Assert.Equal(new ThemeColor(alpha, red, green, blue), color);
        Assert.Equal(value, color.ToHex());
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("123456")]
    [InlineData("#GG0000")]
    [InlineData("")]
    public void ColorParserRejectsMalformedValues(string value) =>
        Assert.False(ThemeColor.TryParse(value, out _));

    [Fact]
    public void ContrastCalculationUsesWcagRatio()
    {
        Assert.Equal(21, ThemeContrast.CalculateRatio("#FFFFFF", "#000000"), 4);
        Assert.NotEmpty(ThemeContrast.Evaluate(
            BuiltInThemes.Default.Colors with
            {
                TextPrimary = "#111111",
                WindowBackground = "#101010",
            }));
    }

    [Fact]
    public void JsonRoundTripsAndToleratesUnknownProperties()
    {
        var json = ThemeJson.Serialize(BuiltInThemes.Default);
        var withUnknownProperty = json.Replace(
            "\"name\": \"kernctl Dark\",",
            "\"name\": \"kernctl Dark\", \"futureProperty\": true,",
            StringComparison.Ordinal);

        var deserialized = ThemeJson.Deserialize(withUnknownProperty);

        Assert.Equal(BuiltInThemes.Default, deserialized);
    }

    [Fact]
    public void JsonRejectsMalformedAndUnsupportedSchemas()
    {
        Assert.Throws<ThemeDataException>(() => ThemeJson.Deserialize("{bad json"));
        var unsupported = BuiltInThemes.Default with { SchemaVersion = 99 };
        Assert.Throws<ThemeDataException>(() => ThemeJson.Serialize(unsupported));
    }

    [Theory]
    [InlineData("My Graphite Theme", "my-graphite-theme")]
    [InlineData("../../evil", "evil")]
    [InlineData("***", "theme")]
    public void FileNamesAreSanitized(string input, string expected) =>
        Assert.Equal(expected, ThemeValidation.SanitizeFileName(input));

    [Theory]
    [InlineData("../theme.json")]
    [InlineData("..\\theme.json")]
    [InlineData("theme.exe")]
    [InlineData("..json")]
    public void PathTraversalAndUnsafeExtensionsAreRejected(string fileName) =>
        Assert.False(ThemeValidation.IsSafeThemeFileName(fileName));
}
