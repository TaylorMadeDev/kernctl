using Kernctl.Core.Themes;

namespace Kernctl.Core.Tests;

public sealed class ThemeStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kernctl-theme-store-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CustomThemeAndActiveSelectionPersistAtomically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new ThemeStore(root);
        var custom = BuiltInThemes.Default.CreateEditableCopy("custom-test", "Test Theme");

        await store.SaveThemeAsync(custom, cancellationToken);
        await store.SaveActiveThemeAsync(custom.Id, cancellationToken);
        var snapshot = await store.LoadAsync(cancellationToken);

        Assert.Equal(custom, Assert.Single(snapshot.CustomThemes));
        Assert.Equal(custom.Id, snapshot.ActiveThemeId);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MalformedThemeIsReportedAndInvalidActiveThemeFallsBack()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var themesDirectory = Path.Combine(root, "themes");
        Directory.CreateDirectory(themesDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(themesDirectory, "broken.json"),
            "{bad",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "settings.json"),
            """{"schemaVersion":1,"activeThemeId":"missing"}""",
            cancellationToken);

        var snapshot = await new ThemeStore(root).LoadAsync(cancellationToken);

        Assert.Empty(snapshot.CustomThemes);
        Assert.Equal(BuiltInThemes.DefaultThemeId, snapshot.ActiveThemeId);
        Assert.NotEmpty(snapshot.Errors);
    }

    [Fact]
    public async Task ImportRejectsOversizedFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "oversized.json");
        await File.WriteAllBytesAsync(
            path,
            new byte[ThemeJson.MaximumImportBytes + 1],
            cancellationToken);

        await Assert.ThrowsAsync<ThemeDataException>(
            () => ThemeStore.ImportAsync(path, cancellationToken));
    }

    [Fact]
    public async Task ExportedThemeCanBeImportedAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "portable-theme.json");
        var custom = BuiltInThemes.Get("ember")
            .CreateEditableCopy("custom-export", "Portable Ember");

        await ThemeStore.ExportAsync(custom, path, cancellationToken);
        var imported = await ThemeStore.ImportAsync(path, cancellationToken);

        Assert.Equal(custom, imported);
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        Assert.Contains("\"schemaVersion\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SchemaVersion\"", json, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
