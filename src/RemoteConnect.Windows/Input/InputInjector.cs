using System.Runtime.InteropServices;
using RemoteConnect.Core.Imaging;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Windows.Capture;
using RemoteConnect.Windows.Interop;

namespace RemoteConnect.Windows.Input;

/// <summary>
/// Выполняет удалённый ввод через SendInput (FR-INP-1, FR-INP-2) и помнит зажатые клавиши и кнопки,
/// чтобы отпустить их при потере фокуса или обрыве связи (FR-INP-3, NFR-7).
/// </summary>
public sealed class InputInjector
{
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    private readonly Lock _lock = new();
    private readonly HashSet<(ushort Vk, ushort Scan, bool Extended)> _pressedKeys = [];
    private readonly HashSet<MouseButton> _pressedButtons = [];
    private (int X, int Y) _lastPosition;

    public void Mouse(MouseInput input, DisplayMonitor monitor)
    {
        var (x, y) = CoordinateMapper.ToPixel(input.X, input.Y, monitor.Left, monitor.Top, monitor.Width, monitor.Height);
        lock (_lock)
        {
            _lastPosition = (x, y);
            var flags = NativeMethods.MOUSEEVENTF_MOVE;
            uint data = 0;

            switch (input.Action)
            {
                case MouseAction.Down:
                    flags |= ButtonFlag(input.Button, down: true, out data);
                    _pressedButtons.Add(input.Button);
                    break;
                case MouseAction.Up:
                    flags |= ButtonFlag(input.Button, down: false, out data);
                    _pressedButtons.Remove(input.Button);
                    break;
                case MouseAction.Wheel:
                    flags |= NativeMethods.MOUSEEVENTF_WHEEL;
                    data = unchecked((uint)input.WheelDelta);
                    break;
                case MouseAction.HorizontalWheel:
                    flags |= NativeMethods.MOUSEEVENTF_HWHEEL;
                    data = unchecked((uint)input.WheelDelta);
                    break;
            }

            SendMouse(x, y, flags, data);
        }
    }

    public void Key(KeyInput input)
    {
        lock (_lock)
        {
            var key = (input.VirtualKey, input.ScanCode, input.IsExtended);
            if (input.IsDown)
            {
                _pressedKeys.Add(key);
            }
            else
            {
                _pressedKeys.Remove(key);
            }

            SendKey(input.VirtualKey, input.ScanCode, input.IsDown, input.IsExtended);
        }
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            foreach (var (vk, scan, extended) in _pressedKeys)
            {
                SendKey(vk, scan, isDown: false, extended);
            }

            foreach (var button in _pressedButtons)
            {
                var flags = NativeMethods.MOUSEEVENTF_MOVE | ButtonFlag(button, down: false, out var data);
                SendMouse(_lastPosition.X, _lastPosition.Y, flags, data);
            }

            _pressedKeys.Clear();
            _pressedButtons.Clear();
        }
    }

    private static uint ButtonFlag(MouseButton button, bool down, out uint data)
    {
        data = 0;
        switch (button)
        {
            case MouseButton.Left:
                return down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP;
            case MouseButton.Right:
                return down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP;
            case MouseButton.Middle:
                return down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP;
            case MouseButton.X1:
                data = NativeMethods.XBUTTON1;
                return down ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP;
            case MouseButton.X2:
                data = NativeMethods.XBUTTON2;
                return down ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP;
            default:
                return 0;
        }
    }

    private static void SendMouse(int x, int y, uint flags, uint data)
    {
        var vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vw = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
        var vh = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)),
                    dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1)),
                    mouseData = data,
                    dwFlags = flags | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
        NativeMethods.SendInput(1, [input], InputSize);
    }

    private static void SendKey(ushort vk, ushort scan, bool isDown, bool extended)
    {
        var flags = (isDown ? 0 : NativeMethods.KEYEVENTF_KEYUP) | (extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0);
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags },
            },
        };
        NativeMethods.SendInput(1, [input], InputSize);
    }
}
