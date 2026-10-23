using System.Net;
using Microsoft.Extensions.Time.Testing;
using RemoteConnect.Core.Security;

namespace RemoteConnect.Core.Tests;

public class SecurityTests
{
    private static readonly byte[] CertHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly IPAddress Ip = IPAddress.Parse("203.0.113.5");

    [Fact]
    public void Proof_WithCorrectPassword_Verifies()
    {
        var key = PasswordVerifier.CreateKey("secret", iterations: 1000);
        var nonce = PasswordVerifier.CreateNonce();
        var proof = PasswordVerifier.ComputeProof(PasswordVerifier.DeriveKey("secret", key.Salt, key.Iterations), nonce, CertHash);
        Assert.True(PasswordVerifier.Verify(key, nonce, CertHash, proof));
    }

    [Fact]
    public void Proof_WithWrongPassword_Fails()
    {
        var key = PasswordVerifier.CreateKey("secret", iterations: 1000);
        var nonce = PasswordVerifier.CreateNonce();
        var proof = PasswordVerifier.ComputeProof(PasswordVerifier.DeriveKey("Secret", key.Salt, key.Iterations), nonce, CertHash);
        Assert.False(PasswordVerifier.Verify(key, nonce, CertHash, proof));
    }

    [Fact]
    public void Proof_ForOtherCertificate_Fails()
    {
        var key = PasswordVerifier.CreateKey("secret", iterations: 1000);
        var nonce = PasswordVerifier.CreateNonce();
        var otherCert = CertHash.Reverse().ToArray();
        var proof = PasswordVerifier.ComputeProof(key.Key, nonce, otherCert);
        Assert.False(PasswordVerifier.Verify(key, nonce, CertHash, proof));
    }

    [Fact]
    public void DeriveKey_RejectsExcessiveIterations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordVerifier.DeriveKey("x", [1], PasswordVerifier.MaxIterations + 1));
    }

    [Fact]
    public void OneTimePassword_HasExpectedShape()
    {
        for (var i = 0; i < 100; i++)
        {
            var password = OneTimePassword.Generate();
            Assert.Matches("^[a-z2-9]{3}-[a-z2-9]{3}$", password);
            Assert.DoesNotContain(password, c => "01ilo".Contains(c));
        }
    }

    [Fact]
    public void Fingerprint_IsFourGroups()
    {
        Assert.Equal("0001-0203-0405-0607", Fingerprint.Format(CertHash));
    }

    [Fact]
    public void RateLimiter_CountsDownAttempts_ThenBlocks()
    {
        var limiter = new AuthRateLimiter(new FakeTimeProvider());
        for (var left = 4; left >= 1; left--)
        {
            var outcome = limiter.RegisterFailure(Ip);
            Assert.False(outcome.IsBlocked);
            Assert.Equal(left, outcome.AttemptsLeft);
        }

        var blocked = limiter.RegisterFailure(Ip);
        Assert.True(blocked.IsBlocked);
        Assert.Equal(TimeSpan.FromMinutes(5), blocked.BlockedFor);
        Assert.True(limiter.IsBlocked(Ip, out _));
        Assert.Single(limiter.GetBlocked());
    }

    [Fact]
    public void RateLimiter_BlockExpires()
    {
        var time = new FakeTimeProvider();
        var limiter = new AuthRateLimiter(time);
        for (var i = 0; i < 5; i++)
        {
            limiter.RegisterFailure(Ip);
        }

        time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(limiter.IsBlocked(Ip, out var remaining));
        Assert.Equal(TimeSpan.FromMinutes(1), remaining);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(limiter.IsBlocked(Ip, out _));
        Assert.Equal(4, limiter.RegisterFailure(Ip).AttemptsLeft);
    }

    [Fact]
    public void RateLimiter_ManualUnblock()
    {
        var limiter = new AuthRateLimiter(new FakeTimeProvider());
        for (var i = 0; i < 5; i++)
        {
            limiter.RegisterFailure(Ip);
        }

        var changed = 0;
        limiter.Changed += (_, _) => changed++;
        Assert.True(limiter.Unblock(Ip));
        Assert.False(limiter.IsBlocked(Ip, out _));
        Assert.Empty(limiter.GetBlocked());
        Assert.Equal(1, changed);
    }

    [Fact]
    public void RateLimiter_SuccessResetsCounter()
    {
        var limiter = new AuthRateLimiter(new FakeTimeProvider());
        limiter.RegisterFailure(Ip);
        limiter.RegisterFailure(Ip);
        limiter.RegisterSuccess(Ip);
        Assert.Equal(4, limiter.RegisterFailure(Ip).AttemptsLeft);
    }

    [Fact]
    public void RateLimiter_TreatsMappedIpv4AsSameAddress()
    {
        var limiter = new AuthRateLimiter(new FakeTimeProvider(), maxFailures: 1);
        limiter.RegisterFailure(Ip.MapToIPv6());
        Assert.True(limiter.IsBlocked(Ip, out _));
    }

    [Fact]
    public void RateLimiter_IsolatesAddresses()
    {
        var limiter = new AuthRateLimiter(new FakeTimeProvider(), maxFailures: 1);
        limiter.RegisterFailure(Ip);
        Assert.False(limiter.IsBlocked(IPAddress.Parse("198.51.100.1"), out _));
    }
}
