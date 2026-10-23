using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnect.App.Services;
using RemoteConnect.App.Sessions;
using RemoteConnect.Core.Network;
using RemoteConnect.Core.Security;
using RemoteConnect.Windows.Network;

namespace RemoteConnect.App.ViewModels;

public sealed partial class BlockedAddressItem(IPAddress address, string remaining) : ObservableObject
{
    public IPAddress Address { get; } = address;

    public string Remaining { get; } = remaining;
}

/// <summary>Вкладка «Хост» (сценарии С1, С5; FR-NET-5..8, FR-NET-11, FR-SEC-9, FR-SEC-10).</summary>
public sealed partial class HostViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly HostService _host;
    private readonly SessionManager _sessions;
    private readonly AuthRateLimiter _rateLimiter;
    private readonly Dispatcher _dispatcher;

    public HostViewModel(SettingsService settings, HostService host, SessionManager sessions, AuthRateLimiter rateLimiter, Dispatcher dispatcher)
    {
        _settings = settings;
        _host = host;
        _sessions = sessions;
        _rateLimiter = rateLimiter;
        _dispatcher = dispatcher;

        var config = settings.Current;
        _port = config.Port.ToString();
        _autoStopMinutes = config.AutoStopMinutes.ToString();
        _isOneTimePassword = config.PasswordMode == PasswordMode.OneTime;
        _hasPermanentPassword = config.PermanentPasswordKey is not null;
        Fingerprint = host.CertificateFingerprint;

        host.Changed += snapshot => dispatcher.InvokeAsync(() => Apply(snapshot));
        host.AutoStopped += () => dispatcher.InvokeAsync(() => Notice = "Ожидание остановлено автоматически: никто не подключился. Порт на роутере закрыт.");
        rateLimiter.Changed += (_, _) => dispatcher.InvokeAsync(RefreshBlocked);

        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        timer.Start();

        RefreshLocalAddresses();
        Apply(host.Snapshot);
    }

    public string Fingerprint { get; }

    public ObservableCollection<string> LocalAddresses { get; } = [];

    public ObservableCollection<BlockedAddressItem> BlockedAddresses { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(StartStopText))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    [ObservableProperty]
    private string _port;

    [ObservableProperty]
    private string _autoStopMinutes;

    [ObservableProperty]
    private bool _isOneTimePassword;

    [ObservableProperty]
    private bool _hasPermanentPassword;

    [ObservableProperty]
    private string _newPermanentPassword = string.Empty;

    [ObservableProperty]
    private string _statusText = "Ожидание выключено";

    [ObservableProperty]
    private StatusKind _statusKind = StatusKind.Info;

    [ObservableProperty]
    private string? _networkText;

    [ObservableProperty]
    private StatusKind _networkKind = StatusKind.Info;

    [ObservableProperty]
    private string? _connectionAddress;

    [ObservableProperty]
    private string? _oneTimePassword;

    [ObservableProperty]
    private string? _autoStopText;

    [ObservableProperty]
    private string? _notice;

    public bool CanEdit => !IsRunning && !IsBusy;

    public string StartStopText => IsRunning ? "Остановить ожидание" : "Начать ожидание";

    public bool HasBlocked => BlockedAddresses.Count > 0;

    [RelayCommand]
    private async Task StartStopAsync()
    {
        Notice = null;
        IsBusy = true;
        try
        {
            if (IsRunning)
            {
                await _host.StopAsync();
                return;
            }

            if (!SaveOptions())
            {
                return;
            }

            StatusText = "Запуск…";
            await _host.StartAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Copy()
    {
        if (ConnectionAddress is null)
        {
            return;
        }

        var text = $"{ConnectionInfoParser.Format(ConnectionAddressHost(), _host.Snapshot.Port, OneTimePassword)}\r\nКод хоста: {Fingerprint}";
        try
        {
            Clipboard.SetText(text);
            Notice = OneTimePassword is null
                ? "Адрес скопирован. Пароль сообщите другу отдельно."
                : "Адрес и пароль скопированы — отправьте их другу.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Notice = "Буфер обмена занят другой программой, попробуйте ещё раз.";
        }
    }

    [RelayCommand]
    private async Task SavePermanentPasswordAsync()
    {
        var password = NewPermanentPassword;
        if (password.Length < 6)
        {
            Notice = "Пароль должен быть не короче 6 символов.";
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Run(() => _settings.SetPermanentPassword(password));
            HasPermanentPassword = true;
            NewPermanentPassword = string.Empty;
            PermanentPasswordSaved?.Invoke();
            Notice = "Постоянный пароль сохранён.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public event Action? PermanentPasswordSaved;

    [RelayCommand]
    private void Unblock(BlockedAddressItem item)
    {
        _rateLimiter.Unblock(item.Address);
        RefreshBlocked();
    }

    [RelayCommand]
    private void AddFirewallRule()
    {
        if (!int.TryParse(Port, out var port))
        {
            return;
        }

        var path = Environment.ProcessPath ?? string.Empty;
        Notice = NetworkInfo.TryAddFirewallRule(port, path, out var error)
            ? $"Правило брандмауэра для порта {port} добавлено."
            : $"Не удалось добавить правило: {error}";
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (_sessions.IsActive &&
            MessageBox.Show("Текущая сессия будет завершена. Продолжить?", "RemoteConnect", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Notice = "Перезапуск отменён.";
        }
    }

    private bool SaveOptions()
    {
        if (!int.TryParse(Port, out var port) || port is < 1024 or > 65535)
        {
            Notice = "Порт должен быть числом от 1024 до 65535.";
            return false;
        }

        if (!int.TryParse(AutoStopMinutes, out var minutes) || minutes is < 0 or > 24 * 60)
        {
            Notice = "Авто-остановка — число минут от 0 до 1440 (0 — не останавливать).";
            return false;
        }

        if (!IsOneTimePassword && !HasPermanentPassword)
        {
            Notice = "Задайте постоянный пароль или выберите одноразовый.";
            return false;
        }

        var config = _settings.Current;
        config.Port = port;
        config.AutoStopMinutes = minutes;
        config.PasswordMode = IsOneTimePassword ? PasswordMode.OneTime : PasswordMode.Permanent;
        _settings.Save();
        return true;
    }

    private void Apply(HostSnapshot s)
    {
        IsRunning = s.Running;
        OneTimePassword = s.OneTimePassword;

        if (!s.Running)
        {
            StatusText = s.Error ?? "Ожидание выключено";
            StatusKind = s.Error is null ? StatusKind.Info : StatusKind.Error;
            NetworkText = null;
            ConnectionAddress = null;
            AutoStopText = null;
            return;
        }

        var address = s.ConnectionAddress;
        ConnectionAddress = address is null ? null : new ConnectionTarget(address.ToString(), s.Port, null).ToString();

        if (s.VpnInterface is { } vpn && s.Upnp != ProbeState.InProgress)
        {
            StatusText = $"Ожидание на порту {s.Port}, но включён VPN — друг не сможет подключиться";
            StatusKind = StatusKind.Warning;
            NetworkText =
                $"Интернет-трафик этого компьютера идёт через VPN «{vpn}». Входящее подключение друга дойдёт через роутер, " +
                "но ответ уйдёт в VPN-туннель, и соединение не установится. Отключите VPN на время сессии и перезапустите ожидание. " +
                "Либо пусть хостом будет друг: вы подключитесь к нему и выберете роль." +
                (s.Upnp == ProbeState.Ok ? " Порт на роутере при этом уже открыт." : string.Empty);
            NetworkKind = StatusKind.Warning;
            return;
        }

        (StatusText, StatusKind) = (s.Upnp, s.Nat, s.Reachability) switch
        {
            (ProbeState.InProgress, _, _) => ($"Ожидание подключений на порту {s.Port}. Настраиваю роутер…", StatusKind.Info),
            (_, NatVerdict.Cgnat, _) => ($"Ожидание на порту {s.Port}, но из интернета подключиться не получится", StatusKind.Warning),
            (_, _, ProbeState.Ok) => ($"Готово: ожидание подключений на порту {s.Port}", StatusKind.Success),
            (ProbeState.Failed, _, ProbeState.InProgress) => ($"Ожидание подключений на порту {s.Port}. Проверяю доступность…", StatusKind.Info),
            (ProbeState.Ok, _, _) => ($"Ожидание подключений на порту {s.Port}", StatusKind.Success),
            _ => ($"Ожидание на порту {s.Port}, но роутер настроить не удалось", StatusKind.Warning),
        };

        (NetworkText, NetworkKind) = (s.Upnp, s.Nat, s.Reachability) switch
        {
            (ProbeState.InProgress, _, _) => ("Открываю порт на роутере через UPnP и определяю внешний IP…", StatusKind.Info),
            (_, NatVerdict.Cgnat, _) =>
                ($"У вас «серый» IP от провайдера (CGNAT){RouterSuffix(s)}. Подключения из интернета до этого компьютера не дойдут. " +
                 "Варианты: пусть хостом будет друг (вы подключитесь к нему и выберете роль), закажите у провайдера белый IP или используйте Tailscale/ZeroTier.",
                    StatusKind.Warning),
            (ProbeState.Ok, _, ProbeState.Ok) => ("Порт открыт на роутере через UPnP и доступен из интернета.", StatusKind.Success),
            (_, _, ProbeState.Ok) => ("Порт доступен из интернета (проброшен на роутере вручную).", StatusKind.Success),
            (ProbeState.Ok, _, ProbeState.InProgress) => ("Порт открыт на роутере через UPnP. Проверяю доступность…", StatusKind.Info),
            (_, _, ProbeState.InProgress) => ("UPnP не сработал. Проверяю, проброшен ли порт вручную…", StatusKind.Info),
            (ProbeState.Ok, _, ProbeState.Failed) =>
                ("Порт открыт через UPnP. Проверить доступность изнутри сети не удалось — роутер может не поддерживать такую проверку. Просто попросите друга подключиться.",
                    StatusKind.Info),
            (ProbeState.Ok, _, _) => ("Порт открыт на роутере через UPnP.", StatusKind.Success),
            _ => ($"{s.UpnpError} Пробросьте порт вручную в настройках роутера (раздел «Проброс портов» / «Port Forwarding»): " +
                  $"TCP, внешний и внутренний порт {s.Port}, локальный адрес {s.LanAddress?.ToString() ?? "этого компьютера"}. " +
                  "Если порт уже проброшен вручную, а проверка не прошла (не все роутеры её позволяют), просто попросите друга подключиться.",
                    StatusKind.Warning),
        };
    }

    private static string RouterSuffix(HostSnapshot s) =>
        s.RouterAddress is null ? string.Empty : $": роутер получил адрес {s.RouterAddress}";

    private string ConnectionAddressHost()
    {
        return _host.Snapshot.ConnectionAddress?.ToString() ?? string.Empty;
    }

    private void Tick()
    {
        var remaining = _host.AutoStopRemaining;
        AutoStopText = IsRunning && remaining is { } r
            ? $"Авто-остановка через {(int)r.TotalMinutes:00}:{r.Seconds:00}, если никто не подключится"
            : null;

        if (BlockedAddresses.Count > 0)
        {
            RefreshBlocked();
        }
    }

    private void RefreshBlocked()
    {
        BlockedAddresses.Clear();
        foreach (var blocked in _rateLimiter.GetBlocked())
        {
            var r = blocked.Remaining;
            BlockedAddresses.Add(new BlockedAddressItem(blocked.Address, $"ещё {(int)r.TotalMinutes}:{r.Seconds:00}"));
        }

        OnPropertyChanged(nameof(HasBlocked));
    }

    [RelayCommand]
    private void RefreshLocalAddresses()
    {
        LocalAddresses.Clear();
        foreach (var (name, address) in NetworkInfo.GetLocalAddresses())
        {
            LocalAddresses.Add($"{address}   ({name})");
        }
    }
}
