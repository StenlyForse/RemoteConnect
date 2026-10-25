using System.IO;
using System.Buffers;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteConnect.App.Sessions;
using RemoteConnect.Core.Chat;
using RemoteConnect.Core.Imaging;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Session;
using RemoteConnect.Windows.Encoding;
using RemoteConnect.Windows.Input;
using Serilog;
using MouseAction = RemoteConnect.Core.Protocol.MouseAction;
using ProtocolMouseButton = RemoteConnect.Core.Protocol.MouseButton;
using WpfMouseButton = System.Windows.Input.MouseButton;

namespace RemoteConnect.App.Views;

/// <summary>
/// Окно управляющей стороны: отображение экрана (FR-SCR-3, FR-SCR-4), выбор монитора (FR-SCR-5)
/// передача мыши/клавиатуры (FR-INP-1..4) и чат (spec-chat).
/// </summary>
public partial class ViewerWindow : Window
{
    private readonly Connection _connection;
    private readonly DispatcherTimer _statsTimer;
    private readonly ClipboardSync _clipboard;
    private readonly ChatSession _chat;
    private WriteableBitmap? _bitmap;
    private LowLevelKeyboardHook? _hook;
    private IntPtr _hwnd;
    private bool _closedByConnection;
    private volatile bool _remoteInputEnabled = true;
    private bool _suppressMonitorChange;
    private long _frames;
    private long _bytes;
    private volatile int _quality;
    private (WindowStyle Style, WindowState State, ResizeMode Resize)? _savedWindow;

    public ViewerWindow(Connection connection, string myName, string peerName, string fingerprint)
    {
        InitializeComponent();
        WindowPlacement.FitToWorkArea(this, maximizeIfTooBig: true);
        _connection = connection;
        PeerName.Text = peerName;
        FingerprintRun.Text = fingerprint;
        Title = $"{peerName} — RemoteConnect";

        _clipboard = new ClipboardSync(connection, Dispatcher);
        _clipboard.RemoteEnabledChanged += enabled =>
            ClipboardToggle.Content = enabled ? "Буфер обмена" : "Буфер обмена (выкл. на удалённом ПК)";
        _clipboard.StatusChanged += status => FileStatusRun.Text = status.Length > 0 ? "   " + status : string.Empty;

        // У управляющего панель сама не раскрывается — только красная точка (FR-CHT-19, FR-CHT-23).
        _chat = new ChatSession(m => _connection.Send(m), myName, peerName, autoOpenOnFirstIncoming: false, TimeProvider.System);
        _chat.UnreadChanged += count => ChatUnreadDot.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Chat.Attach(_chat, peerName);
        Chat.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            // Клавиша, нажатая до перехода в чат, иначе осталась бы «зажатой» на удалённом ПК.
            if ((bool)e.NewValue)
            {
                _connection.Send(new ReleaseAll());
            }
        };

        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStats(), Dispatcher);

        Loaded += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            _statsTimer.Start();
            _clipboard.Start();
            _ = Task.Run(ReceiveLoopAsync);
            _ = WatchConnectionAsync();
        };
        Activated += (_, _) => InstallHook();
        Deactivated += (_, _) =>
        {
            RemoveHook();
            _connection.Send(new ReleaseAll());
        };
    }

    private bool CanSendInput => _bitmap is not null && _remoteInputEnabled && _connection.IsOpen;

    protected override void OnClosing(CancelEventArgs e)
    {
        RemoveHook();
        _statsTimer.Stop();
        _clipboard.Dispose();
        if (!_closedByConnection)
        {
            _ = _connection.CloseAsync(DisconnectReason.UserClosed);
        }

        base.OnClosing(e);
    }

    private async Task WatchConnectionAsync()
    {
        await _connection.Completion;
        _closedByConnection = true;
        Close();
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var message = await _connection.ReceiveAsync();
                switch (message)
                {
                    case ScreenInfo info:
                        await Dispatcher.InvokeAsync(() => ApplyScreenInfo(info));
                        break;
                    case FrameTiles frame:
                        var decoded = DecodeTiles(frame);
                        await Dispatcher.InvokeAsync(() => ApplyTiles(decoded), DispatcherPriority.Render);
                        _connection.Send(new FrameAck(frame.FrameNumber));
                        _quality = frame.Quality;

                        // Большой кадр приходит двумя частями — считаем кадр один раз.
                        if (frame.EndOfFrame)
                        {
                            Interlocked.Increment(ref _frames);
                        }

                        Interlocked.Add(ref _bytes, frame.Tiles.Sum(t => (long)t.Data.Length));
                        break;
                    case InputEnabled input:
                        _remoteInputEnabled = input.Enabled;
                        await Dispatcher.InvokeAsync(() => InputDisabledRun.Text = input.Enabled ? string.Empty : "   ввод запрещён удалённой стороной");
                        break;
                    case ChatMessage chat:
                        Log.Information("Чат: получено сообщение, {Length} символов", chat.Text.Length);
                        await Dispatcher.InvokeAsync(() => _chat.TryHandle(chat));
                        break;
                    default:
                        _clipboard.TryHandle(message);
                        break;
                }
            }
        }
        catch (ConnectionClosedException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning(ex, "Ошибка в окне просмотра");
            await _connection.CloseAsync(DisconnectReason.ProtocolError);
        }
    }

    private static List<(EncodedTile Tile, byte[] Pixels)> DecodeTiles(FrameTiles frame) =>
        frame.Tiles
            .AsParallel()
            .AsOrdered()
            .Select(tile =>
            {
                var pixels = ArrayPool<byte>.Shared.Rent(tile.Width * tile.Height * 4);
                try
                {
                    JpegTileCodec.Decode(tile.Data, pixels, tile.Width, tile.Height);
                    return (tile, pixels);
                }
                catch (InvalidDataException ex)
                {
                    Log.Debug("Пропущена повреждённая плитка: {Message}", ex.Message);
                    ArrayPool<byte>.Shared.Return(pixels);
                    return (tile, Array.Empty<byte>());
                }
            })
            .ToList();

    private void ApplyScreenInfo(ScreenInfo info)
    {
        if (_bitmap is null || _bitmap.PixelWidth != info.Width || _bitmap.PixelHeight != info.Height)
        {
            _bitmap = new WriteableBitmap(info.Width, info.Height, 96, 96, PixelFormats.Bgr32, null);
            Screen.Source = _bitmap;
        }

        Placeholder.Visibility = Visibility.Collapsed;

        _suppressMonitorChange = true;
        Monitors.Items.Clear();
        for (var i = 0; i < info.Monitors.Count; i++)
        {
            var m = info.Monitors[i];
            Monitors.Items.Add($"Монитор {i + 1}: {m.Width}×{m.Height}{(m.IsPrimary ? " (основной)" : string.Empty)}");
        }

        Monitors.SelectedIndex = info.ActiveMonitor;
        Monitors.Visibility = info.Monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _suppressMonitorChange = false;
    }

    private void ApplyTiles(List<(EncodedTile Tile, byte[] Pixels)> tiles)
    {
        try
        {
            if (_bitmap is not { } bitmap)
            {
                return;
            }

            foreach (var (tile, pixels) in tiles)
            {
                if (pixels.Length == 0 || tile.X < 0 || tile.Y < 0 || tile.X + tile.Width > bitmap.PixelWidth || tile.Y + tile.Height > bitmap.PixelHeight)
                {
                    continue;
                }

                bitmap.WritePixels(new Int32Rect(tile.X, tile.Y, tile.Width, tile.Height), pixels, tile.Width * 4, 0);
            }
        }
        finally
        {
            foreach (var (_, pixels) in tiles)
            {
                if (pixels.Length > 0)
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                }
            }
        }
    }

    private void UpdateStats()
    {
        var frames = Interlocked.Exchange(ref _frames, 0);
        var kb = Interlocked.Exchange(ref _bytes, 0) / 1024;
        Stats.Text = _quality > 0 ? $"{frames} к/с  {kb} КБ/с  качество {_quality}" : $"{frames} к/с  {kb} КБ/с";
    }

    // ---------- мышь ----------

    private void Screen_MouseMove(object sender, MouseEventArgs e) => SendMouse(MouseAction.Move, e, ProtocolMouseButton.None, 0);

    private void Screen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Screen.Focus();
        Screen.CaptureMouse();
        SendMouse(MouseAction.Down, e, Map(e.ChangedButton), 0);
        e.Handled = true;
    }

    private void Screen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        SendMouse(MouseAction.Up, e, Map(e.ChangedButton), 0);
        if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released)
        {
            Screen.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    private void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        SendMouse(MouseAction.Wheel, e, ProtocolMouseButton.None, (short)Math.Clamp(e.Delta, short.MinValue, short.MaxValue));
        e.Handled = true;
    }

    private void SendMouse(MouseAction action, MouseEventArgs e, ProtocolMouseButton button, short wheel)
    {
        if (!CanSendInput || Screen.ActualWidth <= 0)
        {
            return;
        }

        var position = e.GetPosition(Screen);
        var (x, y) = CoordinateMapper.ToNormalized(position.X, position.Y, Screen.ActualWidth, Screen.ActualHeight);
        _connection.Send(new MouseInput(action, x, y, button, wheel));
    }

    private static ProtocolMouseButton Map(WpfMouseButton button) => button switch
    {
        WpfMouseButton.Left => ProtocolMouseButton.Left,
        WpfMouseButton.Right => ProtocolMouseButton.Right,
        WpfMouseButton.Middle => ProtocolMouseButton.Middle,
        WpfMouseButton.XButton1 => ProtocolMouseButton.X1,
        WpfMouseButton.XButton2 => ProtocolMouseButton.X2,
        _ => ProtocolMouseButton.None,
    };

    // ---------- клавиатура ----------

    private void InstallHook()
    {
        if (_hook is not null)
        {
            return;
        }

        try
        {
            _hook = new LowLevelKeyboardHook(OnKey);
        }
        catch (Win32Exception ex)
        {
            Log.Warning(ex, "Не удалось установить перехват клавиатуры");
        }
    }

    private void RemoveHook()
    {
        _hook?.Dispose();
        _hook = null;
    }

    /// <summary>
    /// Пока окно просмотра активно, все клавиши (включая Win и Alt+Tab) уходят на удалённую сторону —
    /// кроме набора в поле чата (FR-CHT-10). Перехват вызывается на UI-потоке, поэтому фокус можно проверять прямо здесь.
    /// </summary>
    private bool OnKey(KeyboardHookEvent e)
    {
        if (Chat.IsKeyboardFocusWithin || !CanSendInput || LowLevelKeyboardHook.ForegroundWindow != _hwnd)
        {
            return false;
        }

        _connection.Send(new KeyInput(e.VirtualKey, e.ScanCode, e.IsDown, e.IsExtended));
        return true;
    }

    // ---------- панель ----------

    private void Monitors_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressMonitorChange && Monitors.SelectedIndex >= 0)
        {
            _connection.Send(new SelectMonitor(Monitors.SelectedIndex));
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_savedWindow is { } saved)
        {
            WindowStyle = saved.Style;
            ResizeMode = saved.Resize;
            WindowState = saved.State;
            _savedWindow = null;
            FullScreenButton.Content = "Полный экран";
        }
        else
        {
            _savedWindow = (WindowStyle, WindowState, ResizeMode);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            FullScreenButton.Content = "Выйти из полного экрана";
        }
    }

    private void QualityMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Первое срабатывание — при загрузке разметки (SelectedIndex="0"), соединения ещё нет.
        if (_connection is null || QualityMode.SelectedIndex < 0)
        {
            return;
        }

        _connection.Send(new StreamMode((StreamModeKind)QualityMode.SelectedIndex));
    }

    private void Clipboard_Changed(object sender, RoutedEventArgs e) => _clipboard?.SetLocalEnabled(ClipboardToggle.IsChecked == true);

    private void Disconnect_Click(object sender, RoutedEventArgs e) => Close();

    private void Chat_Click(object sender, RoutedEventArgs e)
    {
        var open = Chat.Visibility != Visibility.Visible;
        Chat.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _chat.SetPanelOpen(open);
        if (open)
        {
            Chat.FocusInput();
        }
        else
        {
            Screen.Focus();
        }
    }
}
