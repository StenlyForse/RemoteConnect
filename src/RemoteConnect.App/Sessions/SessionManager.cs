using System.Windows;
using System.Windows.Threading;
using RemoteConnect.App.Services;
using RemoteConnect.App.Views;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Session;
using Serilog;

namespace RemoteConnect.App.Sessions;

public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Ведёт одну сессию от авторизации до отключения: согласие на управляемой стороне (FR-SEC-5),
/// затем трансляция/управление. Одновременно — только одна сессия (FR-NET-10).
/// </summary>
public sealed class SessionManager(SettingsService settings, Dispatcher dispatcher)
{
    public static readonly TimeSpan ConsentTimeout = TimeSpan.FromSeconds(60);

    private Connection? _current;

    public event Action<string, StatusKind>? StatusChanged;

    /// <summary>Можно ли отменить текущий этап (ожидание подтверждения удалённой стороной).</summary>
    public event Action<bool>? CanCancelChanged;

    public SessionSlot Slot { get; } = new();

    public bool IsActive => _current is not null;

    /// <summary>Запускает сессию на UI-потоке и ждёт её окончания. Слот должен быть уже занят; освобождается здесь.</summary>
    public Task RunOnUiAsync(Connection connection, SessionRole localRole, string peerName, string fingerprint) =>
        dispatcher.InvokeAsync(() => RunAsync(connection, localRole, peerName, fingerprint)).Task.Unwrap();

    public void CancelCurrent() => _ = _current?.CloseAsync(DisconnectReason.UserClosed);

    public async Task CloseAllAsync()
    {
        if (_current is { } connection)
        {
            await connection.CloseAsync(DisconnectReason.AppExit);
        }
    }

    private async Task RunAsync(Connection connection, SessionRole localRole, string peerName, string fingerprint)
    {
        _current = connection;
        try
        {
            Log.Information("Сессия с {Peer}: локальная роль {Role}", peerName, localRole);
            var (message, kind) = localRole == SessionRole.Controlled
                ? await RunControlledAsync(connection, peerName, fingerprint)
                : await RunControllerAsync(connection, peerName, fingerprint);
            Log.Information("Сессия с {Peer} завершена: {Message}", peerName, message);
            StatusChanged?.Invoke(message, kind);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка сессии");
            StatusChanged?.Invoke($"Ошибка сессии: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            CanCancelChanged?.Invoke(false);
            if (connection.IsOpen)
            {
                await connection.CloseAsync(DisconnectReason.Error);
            }

            _current = null;
            Slot.Release();
            Application.Current.MainWindow?.Activate();
        }
    }

    private async Task<(string, StatusKind)> RunControlledAsync(Connection connection, string peerName, string fingerprint)
    {
        StatusChanged?.Invoke($"«{peerName}» запрашивает управление этим компьютером", StatusKind.Warning);
        var decision = await ConsentWindow.AskAsync(peerName, fingerprint, ConsentTimeout, connection.ClosedToken);
        if (!connection.IsOpen)
        {
            return (Describe(await connection.Completion, peerName), StatusKind.Warning);
        }

        connection.Send(new ConsentResult(decision));
        if (decision != ConsentDecision.Allowed)
        {
            await connection.CloseAsync(DisconnectReason.ConsentDenied);
            return (decision == ConsentDecision.TimedOut ? "Запрос на управление отклонён автоматически (нет ответа 60 с)" : "Вы отклонили запрос на управление", StatusKind.Info);
        }

        StatusChanged?.Invoke($"Вашим компьютером управляет «{peerName}»", StatusKind.Warning);
        var session = new ControlledSession(connection, settings.Current.DisplayName, peerName);
        var info = await session.RunAsync();
        return (Describe(info, peerName), StatusKind.Info);
    }

    private async Task<(string, StatusKind)> RunControllerAsync(Connection connection, string peerName, string fingerprint)
    {
        StatusChanged?.Invoke($"Ожидание подтверждения на стороне «{peerName}»…", StatusKind.Info);
        CanCancelChanged?.Invoke(true);
        ConsentResult consent;
        try
        {
            consent = await connection.ReceiveAsync<ConsentResult>(ConsentTimeout + TimeSpan.FromSeconds(15));
        }
        catch (ConnectionClosedException ex)
        {
            return (Describe(ex.Info, peerName), StatusKind.Warning);
        }
        catch (TimeoutException)
        {
            return ("Удалённая сторона не ответила на запрос", StatusKind.Warning);
        }
        finally
        {
            CanCancelChanged?.Invoke(false);
        }

        if (consent.Decision != ConsentDecision.Allowed)
        {
            return (consent.Decision == ConsentDecision.TimedOut
                ? $"«{peerName}» не ответил на запрос"
                : $"«{peerName}» отклонил запрос на управление", StatusKind.Warning);
        }

        StatusChanged?.Invoke($"Вы управляете компьютером «{peerName}»", StatusKind.Success);
        var viewer = new ViewerWindow(connection, settings.Current.DisplayName, peerName, fingerprint);
        viewer.Show();
        var info = await connection.Completion;
        return (Describe(info, peerName), info.Reason is DisconnectReason.UserClosed or DisconnectReason.AppExit ? StatusKind.Info : StatusKind.Warning);
    }

    private static string Describe(DisconnectInfo info, string peerName) => (info.Reason, info.ByRemote) switch
    {
        (DisconnectReason.UserClosed, false) or (DisconnectReason.AppExit, false) => "Сессия завершена",
        (DisconnectReason.UserClosed, true) => $"«{peerName}» завершил сессию",
        (DisconnectReason.AppExit, true) => $"«{peerName}» закрыл приложение",
        (DisconnectReason.ConsentDenied, _) => $"«{peerName}» отклонил запрос",
        (DisconnectReason.Timeout, _) => "Соединение потеряно (нет ответа 10 с)",
        (DisconnectReason.ProtocolError, _) => "Сессия прервана: ошибка протокола",
        _ => "Соединение потеряно",
    };
}
