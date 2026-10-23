using System.Windows.Threading;
using RemoteConnect.App.Views;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Session;
using RemoteConnect.Windows.Input;
using Serilog;

namespace RemoteConnect.App.Sessions;

/// <summary>
/// Управляемая сторона после согласия: плашка сессии (FR-SEC-6), трансляция экрана и выполнение ввода.
/// Ввод выполняется только здесь, то есть только после «Разрешить».
/// </summary>
public sealed class ControlledSession(Connection connection, string peerName, int jpegQuality)
{
    private readonly InputInjector _injector = new();
    private readonly ScreenStreamer _streamer = new(connection, jpegQuality);
    private readonly ClipboardSync _clipboard = new(connection, Dispatcher.CurrentDispatcher);
    private volatile bool _inputEnabled = true;

    /// <summary>Вызывается на UI-потоке; завершается, когда соединение закрыто.</summary>
    public async Task<DisconnectInfo> RunAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(connection.ClosedToken);
        var bar = new SessionBarWindow(peerName);
        bar.DisconnectRequested += () => _ = connection.CloseAsync(DisconnectReason.UserClosed);
        bar.ViewOnlyChanged += viewOnly =>
        {
            _inputEnabled = !viewOnly;
            if (viewOnly)
            {
                _injector.ReleaseAll();
            }

            connection.Send(new InputEnabled(!viewOnly));
        };
        bar.ClipboardChanged += enabled => _clipboard.SetLocalEnabled(enabled);
        _clipboard.RemoteEnabledChanged += bar.SetRemoteClipboardEnabled;
        bar.Show();
        _clipboard.Start();

        var streamTask = Task.Run(() => _streamer.RunAsync(cts.Token));
        var inputTask = Task.Run(() => InputLoopAsync(cts.Token));

        var info = await connection.Completion;
        await cts.CancelAsync();
        _clipboard.Dispose();
        _injector.ReleaseAll();
        bar.CloseForced();

        try
        {
            await Task.WhenAll(streamTask, inputTask);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionClosedException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ошибка трансляции");
        }

        return info;
    }

    private async Task InputLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Message message;
            try
            {
                message = await connection.ReceiveAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ConnectionClosedException)
            {
                return;
            }

            switch (message)
            {
                case FrameAck:
                    _streamer.OnFrameAck();
                    break;
                case MouseInput mouse when _inputEnabled && _streamer.CurrentMonitor is { } monitor:
                    _injector.Mouse(mouse, monitor);
                    break;
                case KeyInput key when _inputEnabled:
                    _injector.Key(key);
                    break;
                case ReleaseAll:
                    _injector.ReleaseAll();
                    break;
                case SelectMonitor select:
                    _streamer.RequestMonitor(select.Index);
                    break;
                default:
                    _clipboard.TryHandle(message);
                    break;
            }
        }
    }
}
