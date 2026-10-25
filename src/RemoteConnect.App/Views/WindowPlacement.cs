using System.Windows;

namespace RemoteConnect.App.Views;

/// <summary>
/// Подгонка начального размера окна под рабочую область экрана: на ноутбуке 1366×768 окно высотой 800
/// уходило низом под панель задач, а рамкой — за верхний край.
/// </summary>
public static class WindowPlacement
{
    private const double Margin = 24;

    /// <summary>
    /// Вызывается до показа окна. Если заданный в XAML размер не помещается, окно уменьшается до 90% рабочей области;
    /// при <paramref name="maximizeIfTooBig"/> — ещё и открывается развёрнутым (Windows ставит его над панелью задач).
    /// </summary>
    public static void FitToWorkArea(Window window, bool maximizeIfTooBig)
    {
        var area = SystemParameters.WorkArea;
        if (window.Width <= area.Width - Margin && window.Height <= area.Height - Margin)
        {
            return;
        }

        window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, area.Width * 0.9));
        window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, area.Height * 0.9));
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = area.Left + ((area.Width - window.Width) / 2);
        window.Top = area.Top + ((area.Height - window.Height) / 2);
        if (maximizeIfTooBig)
        {
            window.WindowState = WindowState.Maximized;
        }
    }
}
