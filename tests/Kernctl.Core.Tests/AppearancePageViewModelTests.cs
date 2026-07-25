using Kernctl.App.Services;
using Kernctl.App.ViewModels.Pages;
using Kernctl.Core.Themes;

namespace Kernctl.Core.Tests;

public sealed class AppearancePageViewModelTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kernctl-appearance-vm-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task EditingMarksDirtyAndCancelRestoresCommittedTheme()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = new ThemeService(new ThemeStore(root), new NullThemeResourceSink());
        await service.InitializeAsync(cancellationToken);
        var viewModel = new AppearancePageViewModel(service, new NullThemeFileDialogService());
        viewModel.BeginPreviewSession();

        viewModel.Density = ThemeDensity.Spacious;

        Assert.True(viewModel.IsDirty);
        Assert.True(service.HasPreview);
        viewModel.CancelCommand.Execute(null);
        Assert.False(viewModel.IsDirty);
        Assert.Equal(BuiltInThemes.Default, service.ActiveTheme);
    }

    [Fact]
    public async Task EditingBuiltInCreatesCustomWorkingTheme()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = new ThemeService(new ThemeStore(root), new NullThemeResourceSink());
        await service.InitializeAsync(cancellationToken);
        var viewModel = new AppearancePageViewModel(service, new NullThemeFileDialogService());

        viewModel.FontScalePercent = 110;

        Assert.False(viewModel.WorkingTheme.IsBuiltIn);
        Assert.Equal(1.1, viewModel.WorkingTheme.Typography.Scale, 3);
        Assert.True(viewModel.IsDirty);
    }

    [Theory]
    [InlineData(90, 0.9)]
    [InlineData(100, 1.0)]
    [InlineData(120, 1.2)]
    public async Task SupportedFontScaleEndpointsPreviewWithoutChangingTheRange(
        double percent,
        double expectedScale)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = new ThemeService(new ThemeStore(root), new NullThemeResourceSink());
        await service.InitializeAsync(cancellationToken);
        var viewModel = new AppearancePageViewModel(service, new NullThemeFileDialogService());

        viewModel.FontScalePercent = percent;

        Assert.Equal(expectedScale, viewModel.WorkingTheme.Typography.Scale, 3);
        Assert.Equal(expectedScale, service.ActiveTheme.Typography.Scale, 3);
        Assert.Empty(ThemeValidation.Validate(viewModel.WorkingTheme));
        Assert.Equal(percent != 100, service.HasPreview);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class NullThemeResourceSink : IThemeResourceSink
    {
        public void Apply(ThemeDefinition theme)
        {
        }
    }

    private sealed class NullThemeFileDialogService : IThemeFileDialogService
    {
        public Task<string?> PickImportPathAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickExportPathAsync(string suggestedFileName) =>
            Task.FromResult<string?>(null);
    }
}
