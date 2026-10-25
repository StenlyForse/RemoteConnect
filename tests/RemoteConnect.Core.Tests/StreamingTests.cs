using Microsoft.Extensions.Time.Testing;
using RemoteConnect.Core.Imaging;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Streaming;

namespace RemoteConnect.Core.Tests;

public class StreamRateControllerTests
{
    private readonly FakeTimeProvider _time = new();

    private (StreamRateController Controller, ChannelSimulator Channel) Create(StreamModeKind mode = StreamModeKind.Auto)
    {
        var controller = new StreamRateController(_time, mode);
        return (controller, new ChannelSimulator(_time, controller));
    }

    private static List<(TimeSpan SentAt, TimeSpan Rtt, int Quality)> After(ChannelSimulator channel, TimeSpan from) =>
        channel.Frames.Where(f => f.SentAt >= from).ToList();

    private static double FpsOf(List<(TimeSpan SentAt, TimeSpan Rtt, int Quality)> frames, TimeSpan window) => frames.Count / window.TotalSeconds;

    [Fact]
    public void SlowChannel_2Mbps_KeepsDelayLow()
    {
        var (controller, channel) = Create();
        channel.Mbps = 2;
        channel.OneWayDelay = TimeSpan.FromMilliseconds(20);
        channel.Run(TimeSpan.FromSeconds(30));

        // Установившийся режим — последние 10 с (NFR-ADP-2).
        var steady = After(channel, TimeSpan.FromSeconds(20));
        var worst = steady.Max(f => f.Rtt) - channel.Ping;
        Assert.True(worst <= TimeSpan.FromMilliseconds(500), $"задержка сверх пинга {worst.TotalMilliseconds} мс");
        Assert.True(FpsOf(steady, TimeSpan.FromSeconds(10)) >= 5, $"частота {FpsOf(steady, TimeSpan.FromSeconds(10))}");
        Assert.True(controller.Quality < 60, $"качество {controller.Quality} не снизилось");
    }

    [Fact]
    public void FastChannel_100Mbps_RaisesQualityAndFps()
    {
        var (controller, channel) = Create();
        channel.Mbps = 100;
        channel.OneWayDelay = TimeSpan.FromMilliseconds(1);
        channel.Run(TimeSpan.FromSeconds(30));

        Assert.True(controller.Quality >= 80, $"качество {controller.Quality}");
        Assert.True(controller.Fps >= 29, $"частота {controller.Fps}");
        var steady = After(channel, TimeSpan.FromSeconds(25));
        Assert.True(FpsOf(steady, TimeSpan.FromSeconds(5)) >= 25, $"фактическая частота {FpsOf(steady, TimeSpan.FromSeconds(5))}");
    }

    [Fact]
    public void HighPing_GoodBandwidth_IsNotMistakenForCongestion()
    {
        var (controller, channel) = Create();
        channel.Mbps = 50;
        channel.OneWayDelay = TimeSpan.FromMilliseconds(150);
        channel.Run(TimeSpan.FromSeconds(40));

        // Пинг 300 мс — это база, а не очередь: качество растёт, как на быстром канале.
        Assert.True(controller.Quality >= 80, $"качество {controller.Quality}");
    }

    [Fact]
    public void ChannelDrops_RecoversWithin3Seconds()
    {
        var (controller, channel) = Create();
        channel.Mbps = 100;
        channel.OneWayDelay = TimeSpan.FromMilliseconds(10);
        channel.Run(TimeSpan.FromSeconds(20));
        Assert.True(controller.Quality >= 80);

        channel.Mbps = 2;
        channel.Run(TimeSpan.FromSeconds(10));

        // Через 3 с после падения канала задержка снова в норме (FR-ADP-4).
        var afterDrop = channel.Frames.Where(f => f.SentAt >= TimeSpan.FromSeconds(23)).ToList();
        var worst = afterDrop.Max(f => f.Rtt) - channel.Ping;
        Assert.True(worst <= TimeSpan.FromMilliseconds(500), $"через 3 с после падения задержка {worst.TotalMilliseconds} мс");
    }

    [Fact]
    public void ChannelImproves_QualityRisesGraduallyWithoutOscillation()
    {
        var (controller, channel) = Create();
        channel.Mbps = 2;
        channel.Run(TimeSpan.FromSeconds(20));
        var low = controller.Quality;

        channel.Mbps = 100;
        var qualities = new List<int>();
        for (var i = 0; i < 20; i++)
        {
            channel.Run(TimeSpan.FromSeconds(1));
            qualities.Add(controller.Quality);
        }

        // Не быстрее шага +5 в секунду и без снижений на хорошем канале (FR-ADP-4).
        var steps = qualities.Prepend(low).Zip(qualities, (a, b) => b - a).ToList();
        Assert.All(steps, s => Assert.InRange(s, 0, 10));
        Assert.True(qualities[^1] > low);
    }

    [Fact]
    public void SlowEncoding_LimitsFps()
    {
        var (controller, channel) = Create();
        channel.Mbps = 100;
        channel.OneWayDelay = TimeSpan.FromMilliseconds(1);
        channel.EncodeCost = TimeSpan.FromMilliseconds(40);
        channel.Run(TimeSpan.FromSeconds(30));

        // Кадр кодируется 40 мс → интервал не меньше 50 мс → не больше 20 к/с (NFR-ADP-1).
        Assert.True(controller.FrameInterval >= TimeSpan.FromMilliseconds(49), $"интервал {controller.FrameInterval.TotalMilliseconds} мс");
        Assert.True(FpsOf(After(channel, TimeSpan.FromSeconds(25)), TimeSpan.FromSeconds(5)) <= 21);
    }

    [Theory]
    [InlineData(StreamModeKind.Auto, 30, 85)]
    [InlineData(StreamModeKind.BestQuality, 75, 90)]
    [InlineData(StreamModeKind.Faster, 20, 55)]
    public void Modes_RespectQualityBounds(StreamModeKind mode, int min, int max)
    {
        var (slow, slowChannel) = Create(mode);
        slowChannel.Mbps = 0.5;
        slowChannel.Run(TimeSpan.FromSeconds(30));
        Assert.Equal(min, slow.Quality);
        Assert.True(slowChannel.Frames.All(f => f.Quality >= min));

        var fastTime = new FakeTimeProvider();
        var fast = new StreamRateController(fastTime, mode);
        var fastChannel = new ChannelSimulator(fastTime, fast) { Mbps = 1000, OneWayDelay = TimeSpan.FromMilliseconds(1) };
        fastChannel.Run(TimeSpan.FromSeconds(60));
        Assert.Equal(max, fast.Quality);
    }

    [Fact]
    public void VerySlowChannel_FallsBackToOneFrameInFlight_ThenLowerFps_NotBelowMinimum()
    {
        var (controller, channel) = Create();
        channel.Mbps = 0.3;
        channel.Run(TimeSpan.FromSeconds(60));

        Assert.Equal(30, controller.Quality);
        Assert.Equal(1, controller.MaxFramesInFlight);
        Assert.True(controller.Fps >= 5 - 0.001, $"частота {controller.Fps}");
    }

    [Fact]
    public void StaticScreen_FreezesParameters()
    {
        var (controller, channel) = Create();
        channel.Mbps = 100;
        channel.Run(TimeSpan.FromSeconds(3));
        var (quality, fps) = (controller.Quality, controller.Fps);

        channel.ChangedFraction = 0;
        channel.Run(TimeSpan.FromSeconds(30));

        // Нет кадров — нет измерений: параметры не «уплывают» вверх на пустом канале.
        Assert.Equal((quality, fps), (controller.Quality, controller.Fps));
    }

    [Fact]
    public void SetMode_AppliesNewBoundsImmediately()
    {
        var controller = new StreamRateController(_time);
        controller.SetMode(StreamModeKind.BestQuality);
        Assert.Equal((StreamModeKind.BestQuality, 85), (controller.Mode, controller.Quality));
        controller.SetMode(StreamModeKind.Faster);
        Assert.Equal(40, controller.Quality);
    }

    [Fact]
    public void Slots_FollowAcks_AndForgetPending()
    {
        var controller = new StreamRateController(_time);
        controller.OnFrameSent(1, 1000);
        controller.OnFrameSent(2, 1000);
        Assert.False(controller.HasSlot);

        controller.OnFrameAcked(1);
        Assert.True(controller.HasSlot);

        controller.OnFrameSent(3, 1000);
        controller.ForgetPending();
        Assert.True(controller.HasSlot);
        controller.OnFrameAcked(42);
    }
}

public class RefineTrackerTests
{
    private readonly FakeTimeProvider _time = new();

    private RefineTracker Create(int width = 256, int height = 128)
    {
        var tracker = new RefineTracker(_time);
        tracker.Reset(width, height);
        return tracker;
    }

    [Fact]
    public void LowQualityTiles_RefinedOnceAfterStable()
    {
        var tracker = Create();
        tracker.OnSent([new PixelRect(0, 0, 256, 128)], 40);

        Assert.Empty(tracker.TakeRefinement(80, 1));
        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Empty(tracker.TakeRefinement(80, 1));
        _time.Advance(TimeSpan.FromMilliseconds(1));

        var refine = tracker.TakeRefinement(80, 1);
        Assert.Equal([new PixelRect(0, 0, 256, 64), new PixelRect(0, 64, 256, 64)], refine);

        tracker.OnSent(refine, 80);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(tracker.TakeRefinement(80, 1));
    }

    [Fact]
    public void GoodTiles_AndNeverSentTiles_AreNotRefined()
    {
        var tracker = Create();
        tracker.OnSent([new PixelRect(0, 0, 64, 64)], 85);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(tracker.TakeRefinement(80, 1));
    }

    [Fact]
    public void ChangedTile_BecomesCandidateAgain_AfterStable()
    {
        var tracker = Create();
        tracker.OnSent([new PixelRect(0, 0, 256, 128)], 80);
        _time.Advance(TimeSpan.FromSeconds(2));
        tracker.OnSent([new PixelRect(64, 64, 64, 64)], 30);
        Assert.Empty(tracker.TakeRefinement(80, 1));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([new PixelRect(64, 64, 64, 64)], tracker.TakeRefinement(80, 1));
    }

    [Fact]
    public void Refinement_IsLimitedByAreaBudget()
    {
        var tracker = Create(1920, 1088);
        tracker.OnSent([new PixelRect(0, 0, 1920, 1088)], 30);
        _time.Advance(TimeSpan.FromSeconds(2));

        var portion = tracker.TakeRefinement(80, 0.25);
        var area = portion.Sum(r => (long)r.Width * r.Height);
        Assert.InRange(area, 1, 1920L * 1088 / 4);
        Assert.Equal(4, portion.Count);
    }

    [Fact]
    public void EdgeTiles_AreClippedToFrame()
    {
        var tracker = Create(100, 70);
        tracker.OnSent([new PixelRect(0, 0, 100, 70)], 30);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal([new PixelRect(0, 0, 100, 64), new PixelRect(0, 64, 100, 6)], tracker.TakeRefinement(80, 1));
    }
}
