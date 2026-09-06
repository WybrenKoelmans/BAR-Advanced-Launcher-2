using System;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Infrastructure;
using BAR_Advanced_Launcher_2.Services;
using BAR_Advanced_Launcher_2.Services.Logging;
using BAR_Advanced_Launcher_2.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BAR_Advanced_Launcher_2;

public partial class App : Application
{
    private IHost? _host;
    private ILogger<App>? _logger;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>The running app's container. Null only before <see cref="OnLaunched"/>.</summary>
    public static IServiceProvider Services =>
        (Current as App)?._host?.Services
        ?? throw new InvalidOperationException("The host has not been built yet.");

    public static T GetService<T>() where T : class => Services.GetRequiredService<T>();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppPaths.EnsureCreated();

        try
        {
            _host = BuildHost();
        }
        catch (Exception ex)
        {
            // Container validation runs inside Build(), so a bad registration throws
            // before there is a logger to report it — and the app would otherwise just
            // vanish with nothing written anywhere. PLAN.md §2.5(3): nothing is swallowed.
            WriteStartupFailure(ex);
            throw;
        }

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Starting {App}.", AppPaths.AppFolderName);

        _window = new MainWindow();

        // Services that need an HWND or a XamlRoot resolve them through this.
        _host.Services.GetRequiredService<WindowContext>().Attach(_window);

        _window.Closed += (_, _) => _host?.Dispose();
        _window.Activate();
    }

    private static IHost BuildHost()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        // Page view models are resolved on first navigation, so a missing registration
        // would otherwise surface as a click that does nothing rather than as an error.
        // Validating at build time turns that into a failure at startup, in the log.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));

        var logSink = new AppLogSink();
        builder.Services.AddSingleton<IAppLogSink>(logSink);
        builder.Logging
            .SetMinimumLevel(LogLevel.Debug)
            .AddProvider(new AppLoggerProvider(logSink))
            .AddProvider(new FileLoggerProvider(AppPaths.LogsFolder));

        // The UI dispatcher is captured here because OnLaunched runs on the UI thread.
        DispatcherQueue uiQueue = DispatcherQueue.GetForCurrentThread();
        builder.Services.AddSingleton<IUiDispatcher>(new UiDispatcher(uiQueue));

        builder.Services.AddSingleton<WindowContext>();
        builder.Services.AddSingleton<NavigationService>();
        builder.Services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());

        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<IBarInstallationLocator, BarInstallationLocator>();
        builder.Services.AddSingleton<IEngineCatalog, EngineCatalog>();
        builder.Services.AddSingleton<IArchiveCacheParser, ArchiveCacheParser>();
        builder.Services.AddSingleton<IArchiveCatalog, ArchiveCatalog>();
        builder.Services.AddSingleton<IRecycleBin, RecycleBin>();
        builder.Services.AddSingleton<IStartScriptStore, StartScriptStore>();
        builder.Services.AddSingleton<IStartScriptSerializer, StartScriptSerializer>();
        builder.Services.AddSingleton<IStartScriptFactory, StartScriptFactory>();
        builder.Services.AddSingleton<IProfileStore, ProfileStore>();
        builder.Services.AddSingleton<IEngineEnvironment, EngineEnvironment>();
        builder.Services.AddSingleton<ILaunchService, LaunchService>();
        builder.Services.AddSingleton<IInstallationContext, InstallationContext>();
        builder.Services.AddSingleton<IShellService, ShellService>();
        builder.Services.AddSingleton<IDialogService, DialogService>();

        // Page view models are singletons: the frame builds a fresh Page on every
        // navigation, and a transient view model would both lose its state and leak a
        // subscription to IInstallationContext.Changed each time.
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<LaunchViewModel>();
        builder.Services.AddSingleton<ScriptsViewModel>();
        builder.Services.AddSingleton<ContentViewModel>();
        builder.Services.AddSingleton<HistoryViewModel>();
        builder.Services.AddSingleton<LogViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();

        return builder.Build();
    }

    /// <summary>
    /// Last-resort reporting for a failure that happens before the logging stack exists.
    /// Writes straight to the log folder and does not throw on the way: whatever went
    /// wrong originally is the interesting failure, not this.
    /// </summary>
    private static void WriteStartupFailure(Exception exception)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.LogsFolder);

            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppPaths.LogsFolder, "startup-failure.log"),
                $"{DateTimeOffset.Now:O} The host could not be built.{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nowhere left to report to.
        }
    }

    // PLAN.md §2.5(3): nothing gets swallowed. Every failure route reaches the log.
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled UI exception: {Message}", e.Message);
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled domain exception.");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }
}
