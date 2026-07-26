using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kernctl.Broker.Client;
using Kernctl.App.Services;
using Kernctl.App.ViewModels;
using Kernctl.App.ViewModels.Actions;
using Kernctl.App.ViewModels.Pages;
using Kernctl.App.Views;
using Kernctl.Core.Actions;
using Kernctl.Core.Services;
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
        services.AddSingleton<ISystemMetricsService, DevelopmentSystemMetricsService>();
        services.AddSingleton(BrokerClientOptions.Default);
        services.AddSingleton<IBrokerExecutableResolver, BrokerExecutableResolver>();
        services.AddSingleton<ICurrentProcessIdentityProvider, CurrentProcessIdentityProvider>();
        services.AddSingleton<IUacBrokerLauncher, WindowsUacBrokerLauncher>();
        services.AddSingleton<IBrokerClient, BrokerClient>();
        services.AddSingleton<IActionPrivilegeBroker, ActionPrivilegeBroker>();
        services.AddSingleton<IActionRegistry>(_ => new ActionRegistry([]));
        services.AddSingleton(_ => new ActionJournalOptions(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kernctl",
            "transactions")));
        services.AddSingleton<IActionJournalStore, FileActionJournalStore>();
        services.AddSingleton<IActionHistoryService, ActionHistoryService>();
        services.AddSingleton<IActionTransactionEngine, ActionTransactionEngine>();
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
