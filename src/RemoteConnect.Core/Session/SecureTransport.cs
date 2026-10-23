using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RemoteConnect.Core.Security;

namespace RemoteConnect.Core.Session;

/// <summary>
/// TLS поверх TCP (FR-SEC-1). Версию выбирает система: TLS 1.3 на Windows 11, TLS 1.2 на Windows 10.
/// Сертификат хоста самоподписанный, поэтому цепочка не проверяется; вместо этого клиент
/// сверяет отпечаток с запомненным (TOFU), а пароль привязывается к сертификату.
/// </summary>
public static class SecureTransport
{
    public const string TargetHost = "RemoteConnect";
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    public static async Task<SslStream> AuthenticateAsServerAsync(NetworkStream stream, X509Certificate2 certificate, CancellationToken ct)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HandshakeTimeout);
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = Protocols,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cts.Token).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <returns>TLS-поток и SHA-256 сертификата хоста.</returns>
    public static async Task<(SslStream Stream, byte[] CertificateHash)> AuthenticateAsClientAsync(NetworkStream stream, CancellationToken ct)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HandshakeTimeout);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = TargetHost,
                    EnabledSslProtocols = Protocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
                },
                cts.Token).ConfigureAwait(false);

            var remote = ssl.RemoteCertificate ?? throw new AuthenticationException("Хост не предъявил сертификат");
            return (ssl, Fingerprint.Hash(remote.GetRawCertData()));
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<TcpClient> ConnectTcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var client = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        client.Client.DualMode = true;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Хост {host}:{port} не отвечает");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
