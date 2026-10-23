using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnect.App.Services;
using RemoteConnect.App.Sessions;
using RemoteConnect.Core.Security;

namespace RemoteConnect.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SessionManager _sessions;

    public MainViewModel(SettingsService settings, HostService host, SessionManager sessions, AuthRateLimiter rateLimiter, Dispatcher dispatcher)
    {
        _settings = settings;
        _sessions = sessions;
        Host = new HostViewModel(settings, host, sessions, rateLimiter, dispatcher);
        Connect = new ConnectViewModel(settings, sessions, dispatcher);
        _displayName = settings.Current.DisplayName;

        sessions.StatusChanged += (text, kind) => dispatcher.InvokeAsync(() =>
        {
            SessionStatus = text;
            SessionStatusKind = kind;
        });
        sessions.CanCancelChanged += can => dispatcher.InvokeAsync(() => CanCancelSession = can);
    }

    public HostViewModel Host { get; }

    public ConnectViewModel Connect { get; }

    public SessionManager Sessions => _sessions;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _sessionStatus = "Нет активной сессии";

    [ObservableProperty]
    private StatusKind _sessionStatusKind = StatusKind.Info;

    [ObservableProperty]
    private bool _canCancelSession;

    partial void OnDisplayNameChanged(string value)
    {
        var name = value.Trim();
        if (name.Length is > 0 and <= 64)
        {
            _settings.Current.DisplayName = name;
            _settings.Save();
        }
    }

    [RelayCommand]
    private void CancelSession() => _sessions.CancelCurrent();
}
