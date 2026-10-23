using Microsoft.Extensions.Time.Testing;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Session;

namespace RemoteConnect.Core.Tests;

public class ClipboardSyncPolicyTests
{
    private static byte[] H(string text) => ClipboardSyncPolicy.Hash(ClipboardFormat.Text, System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void NewContent_IsSent_SameContentTwice_IsNot()
    {
        var policy = new ClipboardSyncPolicy(new FakeTimeProvider());
        Assert.True(policy.ShouldSend(H("a")));
        Assert.False(policy.ShouldSend(H("a")));
        Assert.True(policy.ShouldSend(H("b")));
    }

    [Fact]
    public void AppliedContent_IsNotEchoedBack()
    {
        var policy = new ClipboardSyncPolicy(new FakeTimeProvider());
        policy.OnApplied(H("from remote"));
        Assert.False(policy.ShouldSend(H("from remote")));
    }

    [Fact]
    public void SlightlyChangedContentRightAfterApply_IsTreatedAsEcho()
    {
        var time = new FakeTimeProvider();
        var policy = new ClipboardSyncPolicy(time);
        policy.OnApplied(H("image as received"));
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(policy.ShouldSend(H("image as windows stored it")));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(policy.ShouldSend(H("user copied something new")));
    }

    [Fact]
    public void SameContentCopiedAgainLater_AfterApply_IsNotResent()
    {
        var time = new FakeTimeProvider();
        var policy = new ClipboardSyncPolicy(time);
        policy.OnApplied(H("x"));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(policy.ShouldSend(H("x")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Disabled_OnEitherSide_NothingSentOrApplied(bool local, bool remote)
    {
        var policy = new ClipboardSyncPolicy(new FakeTimeProvider()) { LocalEnabled = local, RemoteEnabled = remote };
        Assert.False(policy.IsActive);
        Assert.False(policy.ShouldSend(H("secret")));
        Assert.False(policy.ShouldApply(ClipboardFormat.Text, 10));
    }

    [Fact]
    public void Limits()
    {
        Assert.True(ClipboardSyncPolicy.WithinLimits(ClipboardFormat.Text, ClipboardSyncPolicy.MaxTextChars));
        Assert.False(ClipboardSyncPolicy.WithinLimits(ClipboardFormat.Text, ClipboardSyncPolicy.MaxTextChars + 1));
        Assert.True(ClipboardSyncPolicy.WithinLimits(ClipboardFormat.Image, ClipboardSyncPolicy.MaxImageBytes));
        Assert.False(ClipboardSyncPolicy.WithinLimits(ClipboardFormat.Image, ClipboardSyncPolicy.MaxImageBytes + 1));
        Assert.False(new ClipboardSyncPolicy(new FakeTimeProvider()).ShouldApply(ClipboardFormat.Image, ClipboardSyncPolicy.MaxImageBytes + 1));
    }

    [Fact]
    public void Hash_DependsOnFormat()
    {
        Assert.NotEqual(ClipboardSyncPolicy.Hash(ClipboardFormat.Text, [1, 2]), ClipboardSyncPolicy.Hash(ClipboardFormat.Image, [1, 2]));
    }
}
