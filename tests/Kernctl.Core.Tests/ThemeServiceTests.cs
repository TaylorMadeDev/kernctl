using Kernctl.App.Services;
using Kernctl.Core.Themes;

namespace Kernctl.Core.Tests;

public sealed class ThemeServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kernctl-theme-service-tests-{Guid.NewGuid():N}");
    private readonly RecordingThemeResourceSink sink = new();

    [Fact]
    public async Task PreviewCommitAndCancelHaveExplicitSemantics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = await CreateServiceAsync(cancellationToken);
        var graphite = BuiltInThemes.Get("graphite");

        service.BeginPreview();
        service.ApplyPreview(graphite);
        Assert.True(service.HasPreview);
        Assert.Equal(graphite, service.ActiveTheme);

        service.CancelPreview();
        Assert.Equal(BuiltInThemes.Default, service.ActiveTheme);

        service.ApplyPreview(graphite);
        await service.CommitAsync(graphite, cancellationToken);
        Assert.False(service.HasPreview);
        Assert.Equal(graphite, service.CommittedTheme);
        Assert.Equal(graphite, sink.LastTheme);
    }

    [Fact]
    public async Task CustomThemesUseUniqueNamesAndCanBeRenamedAndDeleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = await CreateServiceAsync(cancellationToken);
        var first = service.CreateCustomTheme("Custom", BuiltInThemes.Default);
        await service.CommitAsync(first, cancellationToken);
        var second = service.CreateCustomTheme("Custom", BuiltInThemes.Default);

        Assert.Equal("Custom 2", second.Name);
        var renamed = await service.RenameCustomThemeAsync(first, "Renamed", cancellationToken);
        Assert.Equal("Renamed", renamed.Name);
        await service.DeleteCustomThemeAsync(renamed, cancellationToken);
        Assert.DoesNotContain(service.AvailableThemes, theme => theme.Id == renamed.Id);
    }

    [Fact]
    public async Task BuiltInThemesCannotBeRenamedOrDeleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = await CreateServiceAsync(cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RenameCustomThemeAsync(BuiltInThemes.Default, "Changed", cancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteCustomThemeAsync(BuiltInThemes.Default, cancellationToken));
    }

    [Fact]
    public async Task DuplicateNamesAreRejectedDuringRename()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = await CreateServiceAsync(cancellationToken);
        var first = service.CreateCustomTheme("First", BuiltInThemes.Default);
        await service.CommitAsync(first, cancellationToken);
        var second = service.CreateCustomTheme("Second", BuiltInThemes.Default);
        await service.CommitAsync(second, cancellationToken);

        await Assert.ThrowsAsync<ThemeDataException>(
            () => service.RenameCustomThemeAsync(second, first.Name, cancellationToken));
    }

    [Fact]
    public async Task DuplicateNamesAreRejectedDuringCommit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = await CreateServiceAsync(cancellationToken);
        var first = service.CreateCustomTheme("First", BuiltInThemes.Default);
        await service.CommitAsync(first, cancellationToken);
        var second = service.CreateCustomTheme("Second", BuiltInThemes.Default) with
        {
            Name = first.Name,
        };

        await Assert.ThrowsAsync<ThemeDataException>(
            () => service.CommitAsync(second, cancellationToken));
    }

    [Fact]
    public async Task DeletingAnActivePreviewRestoresTheCommittedTheme()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = await CreateServiceAsync(cancellationToken);
        var custom = service.CreateCustomTheme("Preview", BuiltInThemes.Get("ember"));
        await service.CommitAsync(custom, cancellationToken);
        var disposablePreview = service.CreateCustomTheme("Disposable", BuiltInThemes.Get("oled"));
        await new ThemeStore(root).SaveThemeAsync(disposablePreview, cancellationToken);
        service.ApplyPreview(disposablePreview);

        await service.DeleteCustomThemeAsync(disposablePreview, cancellationToken);

        Assert.Equal(custom, service.ActiveTheme);
        Assert.False(service.HasPreview);
        Assert.Equal(custom, sink.LastTheme);
    }

    [Fact]
    public async Task CommittedCustomThemeRestoresInANewServiceInstance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstService = await CreateServiceAsync(cancellationToken);
        var custom = firstService.CreateCustomTheme("Restart Test", BuiltInThemes.Get("graphite"));
        await firstService.CommitAsync(custom, cancellationToken);

        var restoredSink = new RecordingThemeResourceSink();
        var restoredService = new ThemeService(new ThemeStore(root), restoredSink);
        await restoredService.InitializeAsync(cancellationToken);

        Assert.Equal(custom, restoredService.CommittedTheme);
        Assert.Equal(custom, restoredService.ActiveTheme);
        Assert.Equal(custom, restoredSink.LastTheme);
    }

    private async Task<ThemeService> CreateServiceAsync(CancellationToken cancellationToken)
    {
        var service = new ThemeService(new ThemeStore(root), sink);
        await service.InitializeAsync(cancellationToken);
        return service;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class RecordingThemeResourceSink : IThemeResourceSink
    {
        public ThemeDefinition? LastTheme { get; private set; }

        public void Apply(ThemeDefinition theme) => LastTheme = theme;
    }
}
