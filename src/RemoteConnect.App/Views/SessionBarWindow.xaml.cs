using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RemoteConnect.Core.Chat;

namespace RemoteConnect.App.Views;

/// <summary>
/// Плашка поверх всех окон на управляемой стороне (FR-SEC-6, FR-SEC-8).
/// Закрыть её, не завершив сессию, нельзя: закрытие = отключение.
/// </summary>
public partial class SessionBarWindow : Window
{
    private static readonly Color BarColor = Color.FromRgb(0xB9, 0x1C, 0x1C);
    private static readonly Color FlashColor = Color.FromRgb(0xF8, 0x71, 0x71);

    private bool _forceClose;
    private ChatSession? _chat;

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

    /// <summary>
    /// Подключает чат. Автораскрытие по первому сообщению (FR-CHT-21) только меняет видимость панели:
    /// окно плашки не активируется, поэтому ввод управляющего не попадает в поле чата (FR-CHT-22).
    /// </summary>
    public void AttachChat(ChatSession chat, string peerName)
    {
        _chat = chat;
        Chat.Attach(chat, peerName);
        chat.OpenRequested += () => ChatContainer.Visibility = Visibility.Visible;
        chat.UnreadChanged += count =>
        {
            ChatUnreadDot.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (count > 0)
            {
                Flash();
            }
        };
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

    private void Chat_Click(object sender, RoutedEventArgs e)
    {
        var open = ChatContainer.Visibility != Visibility.Visible;
        ChatContainer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _chat?.SetPanelOpen(open);
        if (open)
        {
            // Пользователь сам нажал кнопку — окно плашки уже активно, фокус можно отдать полю ввода.
            Chat.FocusInput();
        }
    }

    /// <summary>Одно короткое мигание плашки при новом сообщении в свёрнутую панель (FR-CHT-24).</summary>
    private void Flash()
    {
        var animation = new ColorAnimation(BarColor, FlashColor, TimeSpan.FromMilliseconds(300)) { AutoReverse = true };
        BarBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void ViewOnly_Changed(object sender, RoutedEventArgs e) => ViewOnlyChanged?.Invoke(ViewOnly.IsChecked == true);

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
