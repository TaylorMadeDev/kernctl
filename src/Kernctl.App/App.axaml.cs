using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kernctl.App.Services;
using Kernctl.App.ViewModels;
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
        services.AddSingleton<ITransactionalActionEngine, TransactionalActionEngine>();
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
