using System.IO;
using System.Windows;
using System.Windows.Threading;
using RemoteConnect.App.Services;
using RemoteConnect.App.Sessions;
using RemoteConnect.App.ViewModels;
using RemoteConnect.App.Views;
using RemoteConnect.Core.Security;
using RemoteConnect.Windows.Security;
using Serilog;

namespace RemoteConnect.App;

public partial class App : Application
{
    private HostService? _host;
    private SessionManager? _sessions;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(AppPaths.Logs);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(AppPaths.Logs, "remoteconnect-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        Log.Information("Запуск RemoteConnect {Version}", typeof(App).Assembly.GetName().Version);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warning(args.Exception, "Необработанное исключение в задаче");
            args.SetObserved();
        };

        var settings = new SettingsService();
        var certificate = HostCertificate.LoadOrCreate(AppPaths.Certificate);
        var rateLimiter = new AuthRateLimiter(TimeProvider.System);
        _sessions = new SessionManager(settings, Dispatcher);
        _host = new HostService(settings, certificate, rateLimiter, _sessions);

        var window = new MainWindow(new MainViewModel(settings, _host, _sessions, rateLimiter, Dispatcher));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Закрываем порт на роутере и сообщаем удалённой стороне о выходе.
            var shutdown = Task.Run(async () =>
            {
                if (_sessions is not null)
                {
                    await _sessions.CloseAllAsync();
                }

                if (_host is not null)
                {
                    await _host.DisposeAsync();
                }
            });
            shutdown.Wait(TimeSpan.FromSeconds(4));
        }
        catch (AggregateException ex)
        {
            Log.Warning(ex, "Ошибка при завершении");
        }

        Log.Information("Выход");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Необработанное исключение");
        MessageBox.Show($"Произошла ошибка:\n{e.Exception.Message}\n\nПодробности в журнале:\n{AppPaths.Logs}", "RemoteConnect", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
