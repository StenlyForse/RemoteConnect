using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace RemoteConnect.App.Views;

/// <summary>
/// Плашка поверх всех окон на управляемой стороне (FR-SEC-6, FR-SEC-8).
/// Закрыть её, не завершив сессию, нельзя: закрытие = отключение.
/// </summary>
public partial class SessionBarWindow : Window
{
    private bool _forceClose;

    public SessionBarWindow(string peerName)
    {
        InitializeComponent();
        PeerName.Text = peerName;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + ((area.Width - ActualWidth) / 2);
            Top = area.Top;
        };
    }

    public event Action? DisconnectRequested;

    public event Action<bool>? ViewOnlyChanged;

    public void CloseForced()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            DisconnectRequested?.Invoke();
        }

        base.OnClosing(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Topmost = false;
        Topmost = true;
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => DisconnectRequested?.Invoke();

    private void ViewOnly_Changed(object sender, RoutedEventArgs e) => ViewOnlyChanged?.Invoke(ViewOnly.IsChecked == true);

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
