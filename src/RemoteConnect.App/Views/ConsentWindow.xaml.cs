using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using RemoteConnect.Core.Protocol;

namespace RemoteConnect.App.Views;

/// <summary>
/// Подтверждение на управляемой стороне (FR-SEC-5). Без «Разрешить» сессия не начинается;
/// через <c>timeout</c> запрос отклоняется автоматически. Фокус по умолчанию — на «Отклонить».
/// </summary>
public partial class ConsentWindow : Window
{
    private readonly TaskCompletionSource<ConsentDecision> _result = new();
    private readonly DispatcherTimer _timer;
    private readonly DateTime _deadline;
    private bool _closing;

    private ConsentWindow(string peerName, string fingerprint, TimeSpan timeout)
    {
        InitializeComponent();
        PeerName.Text = peerName;
        FingerprintRun.Text = fingerprint;
        _deadline = DateTime.UtcNow + timeout;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        Loaded += (_, _) =>
        {
            if (_result.Task.IsCompleted)
            {
                Close();
                return;
            }

            DenyButton.Focus();
            Activate();
        };
    }

    public static Task<ConsentDecision> AskAsync(string peerName, string fingerprint, TimeSpan timeout, CancellationToken ct)
    {
        var window = new ConsentWindow(peerName, fingerprint, timeout);
        var registration = ct.Register(() => window.Dispatcher.InvokeAsync(() => window.Complete(ConsentDecision.Denied)));
        window._result.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        window.Tick();
        window._timer.Start();
        window.Show();
        SystemSounds.Exclamation.Play();
        return window._result.Task;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Закрытие крестиком = отказ. Окно уже закрывается — повторный Close() бросил бы InvalidOperationException.
        _closing = true;
        Complete(ConsentDecision.Denied);
        base.OnClosing(e);
    }

    private void Tick()
    {
        var left = _deadline - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            Complete(ConsentDecision.TimedOut);
            return;
        }

        Countdown.Text = $"Автоматический отказ через {(int)Math.Ceiling(left.TotalSeconds)} с";
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => Complete(ConsentDecision.Allowed);

    private void Deny_Click(object sender, RoutedEventArgs e) => Complete(ConsentDecision.Denied);

    private void Complete(ConsentDecision decision)
    {
        if (!_result.TrySetResult(decision))
        {
            return;
        }

        _timer.Stop();
        if (IsLoaded && !_closing)
        {
            Close();
        }
    }
}
