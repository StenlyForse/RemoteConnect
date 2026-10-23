using System.Runtime.InteropServices;
using RemoteConnect.Core.Imaging;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Windows.Capture;
using RemoteConnect.Windows.Input;

namespace RemoteConnect.Windows.Tests;

/// <summary>Реальный ввод через SendInput: двигает курсор и печатает в тестовое окно.</summary>
public class InputInjectorTests
{
    [Fact]
    public void Mouse_MovesCursorToMappedPixel()
    {
        var monitor = DisplayMonitors.GetAll()[0];
        var injector = new InputInjector();
        var (nx, ny) = CoordinateMapper.ToNormalized(monitor.Width / 3.0, monitor.Height / 4.0, monitor.Width, monitor.Height);

        injector.Mouse(new MouseInput(MouseAction.Move, nx, ny, MouseButton.None, 0), monitor);
        Thread.Sleep(50);

        Assert.True(GetCursorPos(out var point));
        Assert.InRange(point.X, monitor.Left + (monitor.Width / 3) - 2, monitor.Left + (monitor.Width / 3) + 2);
        Assert.InRange(point.Y, monitor.Top + (monitor.Height / 4) - 2, monitor.Top + (monitor.Height / 4) + 2);
    }

    [Fact]
    public void Keyboard_TypesIntoFocusedTextBox_AndReleaseAllLiftsShift()
    {
        string? typed = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new System.Windows.Forms.Form { TopMost = true, Width = 300, Height = 120, StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen };
                var box = new System.Windows.Forms.TextBox { Dock = System.Windows.Forms.DockStyle.Fill };
                form.Controls.Add(box);
                form.Show();
                SetForegroundWindow(form.Handle);
                form.Activate();
                box.Focus();
                Pump(300);

                var injector = new InputInjector();
                foreach (var vk in new ushort[] { 0x34, 0x32 }) // 4, 2 — одинаково в любой раскладке
                {
                    injector.Key(new KeyInput(vk, 0, true, false));
                    injector.Key(new KeyInput(vk, 0, false, false));
                }

                // Shift зажат «удалённо» и не отпущен — ReleaseAll должен его поднять.
                injector.Key(new KeyInput(0xA0, 0x2A, true, false));
                injector.Key(new KeyInput(0x31, 0, true, false));
                injector.Key(new KeyInput(0x31, 0, false, false));
                injector.ReleaseAll();
                injector.Key(new KeyInput(0x31, 0, true, false));
                injector.Key(new KeyInput(0x31, 0, false, false));
                Pump(300);
                typed = box.Text;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));

        Assert.Null(error);
        Assert.Equal("42!1", typed);
    }

    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
