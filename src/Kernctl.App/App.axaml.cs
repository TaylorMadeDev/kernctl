using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kernctl.Broker.Client;
using Kernctl.App.Services;
using Kernctl.App.ViewModels;
using Kernctl.App.ViewModels.Actions;
using Kernctl.App.ViewModels.Pages;
using Kernctl.App.ViewModels.Profiles;
using Kernctl.App.Views;
using Kernctl.Core.Actions;
using Kernctl.Core.Services;
using Kernctl.Core.Profiles;
using Kernctl.Core.Themes;
using Kernctl.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Kernctl.App;

public sealed partial class App : Application
{
    private ServiceProvider? serviceProvider;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            serviceProvider = services.BuildServiceProvider();
            serviceProvider
                .GetRequiredService<IThemeService>()
                .InitializeAsync()
                .GetAwaiter()
                .GetResult();
            serviceProvider
                .GetRequiredService<IProfileCatalogService>()
                .InitializeAsync()
                .GetAwaiter()
                .GetResult();
            serviceProvider
                .GetRequiredService<ProfileManagerViewModel>()
                .InitializeAsync()
                .GetAwaiter()
                .GetResult();

            desktop.MainWindow = new MainWindow
            {
                DataContext = serviceProvider.GetRequiredService<MainWindowViewModel>(),
            };

            desktop.Exit += (_, _) => serviceProvider.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<IPowerSchemeService, WindowsPowerSchemeService>();
        services.AddSingleton<IKernctlFeatureState, KernctlFeatureState>();
        services.AddSingleton<ISystemMetricsService, DevelopmentSystemMetricsService>();
        services.AddSingleton(BrokerClientOptions.Default);
        services.AddSingleton<IBrokerExecutableResolver, BrokerExecutableResolver>();
        services.AddSingleton<ICurrentProcessIdentityProvider, CurrentProcessIdentityProvider>();
        services.AddSingleton<IUacBrokerLauncher, WindowsUacBrokerLauncher>();
        services.AddSingleton<IBrokerClient, BrokerClient>();
        services.AddSingleton<IActionPrivilegeBroker, ActionPrivilegeBroker>();
        services.AddSingleton<IActionRegistry>(provider => new ActionRegistry(
        [
            new PowerSchemeSystemAction(
                provider.GetRequiredService<IPowerSchemeService>(),
                KnownPowerScheme.PowerSaver),
            new PowerSchemeSystemAction(
                provider.GetRequiredService<IPowerSchemeService>(),
                KnownPowerScheme.Balanced),
            new PowerSchemeSystemAction(
                provider.GetRequiredService<IPowerSchemeService>(),
                KnownPowerScheme.HighPerformance),
            new KernctlFeatureSystemAction(
                provider.GetRequiredService<IKernctlFeatureState>(),
                MonitoringFeature.Fps,
                desiredValue: false),
            new KernctlFeatureSystemAction(
                provider.GetRequiredService<IKernctlFeatureState>(),
                MonitoringFeature.Fps,
                desiredValue: true),
            new KernctlFeatureSystemAction(
                provider.GetRequiredService<IKernctlFeatureState>(),
                KernctlPreference.PerformanceMode,
                desiredValue: false),
            new KernctlFeatureSystemAction(
                provider.GetRequiredService<IKernctlFeatureState>(),
                KernctlPreference.PerformanceMode,
                desiredValue: true),
        ]));
        services.AddSingleton(_ => new ActionJournalOptions(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kernctl",
            "transactions")));
        services.AddSingleton<IActionJournalStore, FileActionJournalStore>();
        services.AddSingleton<IActionHistoryService, ActionHistoryService>();
        services.AddSingleton<IActionTransactionEngine, ActionTransactionEngine>();
        var applicationDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kernctl");
        services.AddSingleton<IProfileStore>(_ => new ProfileStore(applicationDataRoot));
        services.AddSingleton<IProfileHistoryStore>(_ => new ProfileHistoryStore(applicationDataRoot));
        services.AddSingleton<IProfileCatalogService, ProfileCatalogService>();
        services.AddSingleton<IProfileEngine, ProfileEngine>();
        services.AddSingleton<IAutomaticProfileSwitcher, AutomaticProfileSwitcher>();
        services.AddSingleton<IProfileFileDialogService, ProfileFileDialogService>();
        services.AddSingleton<ProfileManagerViewModel>();
        services.AddSingleton<ActionReviewDialogViewModel>();
        services.AddSingleton<ActionProgressViewModel>();
        services.AddSingleton<ActionRecoveryViewModel>();
        services.AddSingleton(_ => new ThemeStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kernctl")));
        services.AddSingleton<IThemeResourceSink, AvaloniaThemeResourceSink>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IThemeFileDialogService, ThemeFileDialogService>();
        services.AddSingleton<GamingPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();
        services.AddSingleton<MainWindowViewModel>();
    }
}
