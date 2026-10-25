using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnect.App.Services;
using RemoteConnect.App.Sessions;
using RemoteConnect.Core.Network;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Security;
using RemoteConnect.Core.Session;
using Serilog;

namespace RemoteConnect.App.ViewModels;

/// <summary>Вкладка «Подключиться» (сценарии С2, С3; FR-NET-9, FR-SEC-4, FR-SEC-7, FR-UI-3).</summary>
public sealed partial class ConnectViewModel : ObservableObject
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly SettingsService _settings;
    private readonly SessionManager _sessions;
    private CancellationTokenSource? _cts;

    public ConnectViewModel(SettingsService settings, SessionManager sessions, Dispatcher dispatcher)
    {
        _settings = settings;
        _sessions = sessions;
        foreach (var address in settings.Current.History)
        {
            History.Add(address);
        }

        _address = History.FirstOrDefault() ?? string.Empty;
        _isController = settings.Current.ConnectRole == SessionRole.Controller;
    }

    public ObservableCollection<string> History { get; } = [];

    /// <summary>Пароль задаётся из PasswordBox в code-behind и не хранится.</summary>
    public string Password { get; set; } = string.Empty;

    public event Action<string>? PasswordFilled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _address;

    [ObservableProperty]
    private bool _isController;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private StatusKind _statusKind;

    public bool IsIdle => !IsConnecting;

    [RelayCommand]
    private void Paste()
    {
        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        if (ConnectionInfoParser.TryParse(text, out var target))
        {
            Address = target.ToString();
            if (target.Password is { } password)
            {
                Password = password;
                PasswordFilled?.Invoke(password);
            }

            SetStatus(target.Password is null ? "Адрес вставлен. Введите пароль." : "Адрес и пароль вставлены.", StatusKind.Info);
        }
        else
        {
            SetStatus("В буфере обмена нет адреса подключения.", StatusKind.Warning);
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool CanConnect() => !IsConnecting && !string.IsNullOrWhiteSpace(Address);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (!ConnectionInfoParser.TryParse(Address, out var target))
        {
            SetStatus("Неверный адрес. Пример: 203.0.113.5:47800", StatusKind.Error);
            return;
        }

        var password = (target.Password ?? Password).Trim();
        if (password.Length == 0)
        {
            SetStatus("Введите пароль, который показан на компьютере друга.", StatusKind.Error);
            return;
        }

        if (!_sessions.Slot.TryClaim())
        {
            SetStatus("Уже идёт другая сессия. Сначала завершите её.", StatusKind.Warning);
            return;
        }

        var role = IsController ? SessionRole.Controller : SessionRole.Controlled;
        _settings.Current.ConnectRole = role;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsConnecting = true;
        Connection? connection = null;
        var handedOff = false;
        try
        {
            SetStatus($"Подключение к {target}…", StatusKind.Info);
            var tcp = await SecureTransport.ConnectTcpAsync(target.Host, target.Port, ConnectTimeout, ct);
            var (ssl, certHash) = await SecureTransport.AuthenticateAsClientAsync(tcp.GetStream(), ct);
            connection = new Connection(ssl, tcp.Client.RemoteEndPoint);

            var fingerprint = Fingerprint.Format(certHash);
            if (!ConfirmKnownHost(target, certHash, fingerprint))
            {
                SetStatus("Подключение отменено: код хоста изменился.", StatusKind.Warning);
                return;
            }

            AddHistory(target.ToString());
            connection.Start();

            SetStatus("Проверка пароля…", StatusKind.Info);
            var (result, peerVersion) = await Handshake.RunClientAsync(connection, _settings.Current.DisplayName, role, password, certHash, AppInfo.Version, ct);
            Log.Information("Подключение к {Target}: {Status}, версия собеседника {PeerVersion}", target, result.Status, peerVersion ?? "< 0.4");

            switch (result.Status)
            {
                case AuthStatus.Ok:
                    handedOff = true;
                    IsConnecting = false;
                    SetStatus(null, StatusKind.Info);
                    await _sessions.RunOnUiAsync(connection, role, result.HostName, fingerprint);
                    break;
                case AuthStatus.WrongPassword:
                    SetStatus($"Неверный пароль, осталось попыток: {result.AttemptsLeft}", StatusKind.Error);
                    break;
                case AuthStatus.Blocked:
                    SetStatus($"Слишком много попыток, повторите через {Math.Max(1, (int)Math.Ceiling(result.BlockedSeconds / 60.0))} мин", StatusKind.Error);
                    break;
                case AuthStatus.Busy:
                    SetStatus("Удалённый компьютер занят другой сессией.", StatusKind.Warning);
                    break;
                case AuthStatus.VersionMismatch:
                    SetStatus(AppInfo.DescribeVersionMismatch(peerVersion), StatusKind.Error);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Подключение отменено.", StatusKind.Info);
        }
        catch (TimeoutException)
        {
            SetStatus("Компьютер не отвечает. Проверьте адрес и что у друга включено ожидание.", StatusKind.Error);
        }
        catch (SocketException ex)
        {
            SetStatus(ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "Подключение отклонено: на этом адресе не включено ожидание.",
                SocketError.HostNotFound or SocketError.NoData => "Адрес не найден.",
                _ => $"Не удалось подключиться: {ex.Message}",
            }, StatusKind.Error);
        }
        catch (AuthenticationException ex)
        {
            Log.Warning(ex, "Ошибка TLS");
            SetStatus("Не удалось установить защищённое соединение. Это точно RemoteConnect?", StatusKind.Error);
        }
        catch (Exception ex) when (ex is ConnectionClosedException or System.IO.IOException or ProtocolException)
        {
            Log.Warning(ex, "Соединение прервано при подключении");
            SetStatus("Соединение прервано при подключении.", StatusKind.Error);
        }
        finally
        {
            IsConnecting = false;
            _cts.Dispose();
            _cts = null;
            if (!handedOff)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }

                _sessions.Slot.Release();
            }
        }
    }

    /// <summary>TOFU: запоминаем код хоста при первом подключении, предупреждаем при смене (В3).</summary>
    private bool ConfirmKnownHost(ConnectionTarget target, byte[] certHash, string fingerprint)
    {
        var key = target.ToString();
        var hex = Convert.ToHexString(certHash);
        if (_settings.Current.KnownHosts.TryGetValue(key, out var known) && !string.Equals(known, hex, StringComparison.OrdinalIgnoreCase))
        {
            var answer = MessageBox.Show(
                $"Код хоста {key} изменился.\n\nНовый код: {fingerprint}\n\n" +
                "Так бывает, если друг переустановил приложение или Windows. Но это может быть и подмена.\n" +
                "Сверьте код с другом (он показан у него на вкладке «Хост»). Продолжить подключение?",
                "RemoteConnect — внимание",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return false;
            }
        }

        _settings.Current.KnownHosts[key] = hex;
        _settings.Save();
        return true;
    }

    private void AddHistory(string address)
    {
        _settings.AddHistory(address);
        _settings.Save();
        History.Remove(address);
        History.Insert(0, address);
        while (History.Count > 10)
        {
            History.RemoveAt(History.Count - 1);
        }

        Address = address;
    }

    private void SetStatus(string? text, StatusKind kind)
    {
        StatusText = text;
        StatusKind = kind;
    }
}
