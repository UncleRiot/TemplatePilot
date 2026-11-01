using System.IO;
using Microsoft.Extensions.DependencyInjection;
using TemplatePilot.Infrastructure;
using TemplatePilot.Services;
using TemplatePilot.ViewModels;
using TemplatePilot.Views;
using WpfApplication = System.Windows.Application;
using System.Windows;

namespace TemplatePilot;

public partial class App : WpfApplication
{
    private ServiceProvider? _services;
    private SingleInstanceCoordinator? _singleInstance;
    private CancellationTokenSource? _shutdownCts;
    private bool _showingUnhandledErrorDialog;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _shutdownCts = new CancellationTokenSource();
        _singleInstance = new SingleInstanceCoordinator();

        if (!_singleInstance.TryAcquire())
        {
            Shutdown();
            return;
        }

        _services = ConfigureServices();
        var logging = _services.GetRequiredService<LoggingService>();

        RegisterExceptionHandlers(logging);

        try
        {
            var settings = _services.GetRequiredService<SettingsService>();
            await settings.LoadAsync(_shutdownCts.Token).ConfigureAwait(true);

            var paths = _services.GetRequiredService<AppPaths>();
            paths.SetTemplatesPath(settings.Current.TemplatesPath);

            var localization = _services.GetRequiredService<LocalizationService>();
            localization.SetLanguage(settings.Current.Language);

            _services.GetRequiredService<CrashRecoveryService>().DeleteStaleRecoveryFiles();

            var mainWindow = _services.GetRequiredService<MainWindow>();
            // Semantic Anchor: MainWindowBeforeThemeRegistration
            // Invariant: assign Application.MainWindow before applying the theme;
            // WPF-UI resolves that property when it configures the native window backdrop.
            MainWindow = mainWindow;
            _services.GetRequiredService<AppThemeService>().RegisterMainWindow(
                mainWindow,
                settings.Current.Theme,
                settings.Current.GlassTransparency);

            _services.GetRequiredService<TrayService>().Initialize(
                mainWindow,
                () => _services.GetRequiredService<ApplicationLifetimeService>().RequestShutdown());

            var startup = _services.GetRequiredService<StartupService>();
            startup.SetEnabled(settings.Current.StartWithWindows);

            var hotkey = _services.GetRequiredService<GlobalHotkeyService>();
            hotkey.HotkeyPressed += OnHotkeyPressed;

            try
            {
                hotkey.Register(settings.Current.Shortcut);
            }
            catch (Exception exception)
            {
                logging.Error("Global hotkey registration failed.", exception);
                _services.GetRequiredService<MessageBoxService>().Error(localization["HotkeyConflict"]);
            }

            if (!e.Args.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                mainWindow.Show();
            }

            logging.Info("Application started.");
        }
        catch (Exception exception)
        {
            logging.Error("Application startup failed.", exception);
            try
            {
                ThemedDialogWindow.ShowMessage(ThemedDialogWindow.FindOwner(), AppConstants.AppName,
                    exception.Message, null, "OK", symbol: "!");
            }
            catch (Exception dialogException)
            {
                logging.Error("Startup error dialog failed.", dialogException);
            }
            finally
            {
                Shutdown();
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownCts?.Cancel();

        if (_services is not null)
        {
            _services.Dispose();
            _services = null;
        }

        _shutdownCts?.Dispose();
        _shutdownCts = null;

        try
        {
            var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConstants.AppName, "Temp");
            if (Directory.Exists(temp))
            {
                foreach (var file in Directory.EnumerateFiles(temp))
                {
                    AtomicFileWriter.TryDelete(file);
                }
            }
        }
        catch
        {
        }

        _singleInstance?.Dispose();
        _singleInstance = null;

        base.OnExit(e);
    }

    private ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<AppPaths>();
        services.AddSingleton<UiDispatcher>();
        services.AddSingleton<LoggingService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<AppThemeService>();
        services.AddSingleton<ApplicationLifetimeService>();
        services.AddSingleton<WindowPlacementService>();
        services.AddSingleton<UpdateService>();

        services.AddSingleton<FileLockService>();
        services.AddSingleton<StartupService>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<MessageBoxService>();
        services.AddSingleton<CategoryDialogService>();
        services.AddSingleton<FileDialogService>();

        services.AddSingleton<ClipboardStaQueue>();
        services.AddSingleton<ClipboardRetryService>();
        services.AddSingleton<ClipboardFingerprintService>();
        services.AddSingleton<ClipboardCaptureService>();
        services.AddSingleton<ClipboardPasteService>();

        services.AddSingleton<KeyboardInputService>();
        services.AddSingleton<ForegroundWindowService>();
        services.AddSingleton<FocusValidationService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<PlaceholderService>();

        services.AddSingleton<TemplateStorageService>();
        services.AddSingleton<CategoryCatalogService>();
        services.AddSingleton<TemplateCacheService>();
        services.AddSingleton<TemplateResolver>();
        services.AddSingleton<TemplateEditorService>();
        services.AddSingleton<TemplateImportExportService>();
        services.AddSingleton<CrashRecoveryService>();
        services.AddSingleton<SearchPopupService>();
        services.AddSingleton<TemplateEditorDialogService>();
        services.AddSingleton<TemplateInsertionCoordinator>();

        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider(validateScopes: true);
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (_services is null || _shutdownCts is null || _shutdownCts.IsCancellationRequested)
        {
            return;
        }

        var coordinator = _services.GetRequiredService<TemplateInsertionCoordinator>();
        _ = coordinator.HandleHotkeyAsync(_shutdownCts.Token);
    }

    private void RegisterExceptionHandlers(LoggingService logging)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            logging.Error("Unhandled UI exception.", args.Exception);
            args.Handled = true;
            // Semantic Anchor: UnhandledDialogRecursionGuard
            // Invariant: a failure inside a themed error dialog is logged, not shown
            // through another dialog (which would recursively fail on the UI thread).
            if (_showingUnhandledErrorDialog)
            {
                return;
            }

            try
            {
                _showingUnhandledErrorDialog = true;
                ThemedDialogWindow.ShowMessage(ThemedDialogWindow.FindOwner(), AppConstants.AppName,
                    args.Exception.Message, null, "OK", symbol: "!");
            }
            catch (Exception dialogException)
            {
                logging.Error("Unhandled error dialog failed.", dialogException);
            }
            finally
            {
                _showingUnhandledErrorDialog = false;
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                logging.Error("Unhandled AppDomain exception.", exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logging.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }
}