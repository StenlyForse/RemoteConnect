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

    public event Action<bool>? ClipboardChanged;

    /// <summary>Показывает, что собеседник выключил синхронизацию буфера (FR-CLP-4).</summary>
    public void SetRemoteClipboardEnabled(bool enabled)
    {
        ClipboardToggle.Content = enabled ? "Буфер обмена" : "Буфер обмена (выкл. у собеседника)";
    }

    /// <summary>Передача файлов или уведомление о них (FR-FCB-19, 20); пустая строка скрывает.</summary>
    public void SetFileStatus(string status)
    {
        FileStatus.Text = status;
        FileStatus.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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

    private void Clipboard_Changed(object sender, RoutedEventArgs e) => ClipboardChanged?.Invoke(ClipboardToggle.IsChecked == true);

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
