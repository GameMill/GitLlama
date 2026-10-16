using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GitLlama.Git;
using LibGit2Sharp;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed partial class ManagedHttpsTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RealTlsHandlesBrowserDelayAndCleanShutdownWithoutDisablingCertificateChecks(bool trustFixture, bool closeAfterLogin)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var authorized = 0;
        var logins = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                    using var tls = new SslStream(connection.GetStream());
                    try
                    {
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 }, stop.Token);
                        var header = new StringBuilder();
                        var buffer = new byte[1];
                        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        {
                            if (await tls.ReadAsync(buffer, stop.Token) == 0) break;
                            header.Append((char)buffer[0]);
                            if (header.Length > 16384) throw new IOException("Invalid fixture request.");
                        }
                        if (!header.ToString().Contains("Authorization: Basic", StringComparison.OrdinalIgnoreCase))
                        {
                            await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=fixture\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n"), stop.Token);
                            await tls.FlushAsync(stop.Token);
                            // The connection closes while the credential helper is still awaiting its browser.
                            await Task.Delay(80, stop.Token);
                        }
                        else
                        {
                            Interlocked.Increment(ref authorized);
                            if (!closeAfterLogin)
                            {
                                const string body = "001e# service=git-upload-pack\n00000000";
                                var reply = "HTTP/1.1 200 OK\r\nContent-Type: application/x-git-upload-pack-advertisement\r\nContent-Length: "
                                    + Encoding.ASCII.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
                                await tls.WriteAsync(Encoding.ASCII.GetBytes(reply), stop.Token);
                                await tls.FlushAsync(stop.Token);
                            }
                        }
                        await tls.ShutdownAsync();
                    }
                    catch (Exception error) when (error is IOException or AuthenticationException) { }
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var options = new GitNetworkOptions
            {
                UseCredentialManager = true,
                CredentialHelper = async (_, ct) =>
                {
                    Interlocked.Increment(ref logins);
                    await Task.Delay(300, ct);
                    return new UsernamePasswordCredentials { Username = "fixture", Password = "fixture-secret" };
                },
                // This pin is confined to this disposable test server; production uses normal certificate validation.
                HttpsHandlerFactory = trustFixture ? () => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    SslOptions = new SslClientAuthenticationOptions
                    { RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == certificate.Thumbprint }
                } : null
            };
            var operation = new GitRepositoryLifecycleService().CloneAsync($"https://127.0.0.1:{port}/repo", Path.Combine(root, "tls-clone"), options, stop.Token);
            if (trustFixture && !closeAfterLogin)
            {
                await operation;
                Assert.Equal(1, logins);
                Assert.Equal(1, authorized);
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
                Assert.Contains("secure HTTPS connection", error.Message);
                Assert.DoesNotContain("fixture-secret", error.Message);
                if (!trustFixture) Assert.Equal(0, logins);
            }
        }
        finally
        {
            stop.Cancel();
            await server;
            listener.Stop();
        }
    }
}
