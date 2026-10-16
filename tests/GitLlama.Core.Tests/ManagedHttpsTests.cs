using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GitLlama.Git;
using LibGit2Sharp;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed partial class ManagedHttpsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gitllama-https-" + Guid.NewGuid().ToString("N"));
    private readonly Signature author = new("Test", "test@example.invalid", DateTimeOffset.UtcNow);

    public ManagedHttpsTests() => Directory.CreateDirectory(root);

    private string Seed()
    {
        var source = Path.Combine(root, "seed");
        Repository.Init(source);
        using var repo = new Repository(source);
        File.WriteAllText(Path.Combine(source, "initial.txt"), "initial\n");
        Commands.Stage(repo, "initial.txt");
        repo.Commit("initial", author, author);
        var bare = Path.Combine(root, "remote.git");
        Repository.Clone(source, bare, new CloneOptions { IsBare = true });
        using var remote = new Repository(bare);
        remote.Config.Set("http.receivepack", true);
        return bare;
    }

    [Fact]
    public async Task PrivateCloneFetchPushAndTagsUseStreamingNativeGitWithHttpAuthentication()
    {
        var bare = Seed();
        var server = new GitServer(root);
        var logins = 0;
        var options = new GitNetworkOptions
        {
            UseCredentialManager = true,
            CredentialHost = "fixture.invalid",
            HttpsHandlerFactory = () => server.CreateHandler(),
            CredentialHelper = async (_, ct) =>
            {
                Interlocked.Increment(ref logins);
                await Task.Delay(30, ct);
                return new UsernamePasswordCredentials { Username = "fixture", Password = "fixture-password" };
            }
        };
        var clone = Path.Combine(root, "clone");
        await new GitRepositoryLifecycleService().CloneAsync("https://fixture.invalid/remote.git", clone, options);
        Assert.Equal("initial\n", File.ReadAllText(Path.Combine(clone, "initial.txt")).Replace("\r\n", "\n"));
        string branch;
        using (var repo = new Repository(clone))
        {
            branch = repo.Head.FriendlyName;
            // Incompressible multi-megabyte content exercises bounded upload backpressure.
            File.WriteAllBytes(Path.Combine(clone, "large.bin"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
            Commands.Stage(repo, "large.bin");
            repo.Commit("large upload", author, author);
            repo.ApplyTag("https-streamed");
        }
        var remotes = new GitRemoteService();
        // Each operation has its own credential state, matching the app's options snapshots.
        GitNetworkOptions Fresh() => new()
        {
            Secret = "fixture-password", Username = "fixture", CredentialHost = "fixture.invalid",
            HttpsHandlerFactory = () => server.CreateHandler()
        };
        await remotes.PushCurrentBranchAsync(clone, "origin", network: Fresh());
        using (var remote = new Repository(bare))
        {
            Assert.Equal("large upload", remote.Branches[branch].Tip.MessageShort);
            Assert.NotNull(remote.Tags["https-streamed"]);
        }
        await remotes.FetchAsync(clone, "origin", network: Fresh());
        Assert.Equal(1, logins);
        Assert.True(server.UploadBytes > 4 * 1024 * 1024);
        Assert.Contains(server.Authorization, header => header?.StartsWith("Basic ") == true);
    }

    [Theory]
    [InlineData("https://other.example/team/_git/repo/info/refs?service=git-upload-pack", "redirect")]
    [InlineData("https://dev.azure.com/blocked/project/_git/repo/info/refs?service=git-upload-pack", "not connected")]
    public async Task BearerCredentialsAreScopedAndRedirectsCannotSendThemElsewhere(string destination, string message)
    {
        var requests = new List<HttpRequestMessage>();
        var options = new GitNetworkOptions
        {
            AllowedAzureOrganizations = ["team"], AzureCredentialScopeUrl = "https://dev.azure.com/team/",
            AzureOAuthTokenProvider = _ => Task.FromResult("fixture-bearer"),
            HttpsHandlerFactory = () => new DelegateHandler(request =>
            {
                requests.Add(request);
                Assert.Equal("Bearer fixture-bearer", request.Headers.Authorization?.ToString());
                var result = new HttpResponseMessage(HttpStatusCode.Redirect);
                result.Headers.Location = new Uri(destination);
                return result;
            })
        };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new GitRepositoryLifecycleService().CloneAsync(
            "https://dev.azure.com/team/project/_git/repo", Path.Combine(root, "redirect"), options));
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-bearer", error.Message);
        Assert.Single(requests);
    }

    [Fact]
    public async Task CanceledRequestsAndInterruptedTlsReturnWithoutCredentialLeakage()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var options = new GitNetworkOptions { HttpsHandlerFactory = () => new WaitingHandler() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitRepositoryLifecycleService().CloneAsync(
            "https://fixture.invalid/remote.git", Path.Combine(root, "cancel"), options, timeout.Token));
        var broken = new GitNetworkOptions
        {
            Secret = "fixture-private-secret", CredentialHost = "fixture.invalid",
            HttpsHandlerFactory = () => new DelegateHandler(_ => throw new HttpRequestException("opaque TLS failure"))
        };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new GitRepositoryLifecycleService().CloneAsync(
            "https://fixture.invalid/remote.git", Path.Combine(root, "broken"), broken));
        Assert.Contains("HTTPS connection", error.Message);
        Assert.DoesNotContain("fixture-private-secret", error.Message);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); }
    }
    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(handle(request));
    }

    // The test server uses Git's CGI backend as an independent reference implementation.
    // GitLlama's client calls only libgit2 and HttpClient throughout these tests.
    private sealed class GitServer(string directory)
    {
        public long UploadBytes;
        public List<string?> Authorization { get; } = [];
        public HttpMessageHandler CreateHandler() => new BackendHandler(this, directory);

        private sealed class BackendHandler(GitServer server, string directory) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var authorization = request.Headers.Authorization?.ToString();
                server.Authorization.Add(authorization);
                if (authorization is null)
                {
                    var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=fixture"));
                    return challenge;
                }
                var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("http-backend");
                start.Environment["GIT_PROJECT_ROOT"] = directory;
                start.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
                start.Environment["REQUEST_METHOD"] = request.Method.Method;
                start.Environment["PATH_INFO"] = request.RequestUri!.AbsolutePath;
                start.Environment["QUERY_STRING"] = request.RequestUri.Query.TrimStart('?');
                start.Environment["CONTENT_TYPE"] = request.Content?.Headers.ContentType?.ToString() ?? "";
                start.Environment["REMOTE_USER"] = "fixture";
                using var process = Process.Start(start)!;
                using var output = new MemoryStream();
                var reading = process.StandardOutput.BaseStream.CopyToAsync(output, ct);
                var errors = process.StandardError.ReadToEndAsync(ct);
                if (request.Content is not null)
                {
                    using var counted = new CountedStream(process.StandardInput.BaseStream, count => Interlocked.Add(ref server.UploadBytes, count));
                    await request.Content.CopyToAsync(counted, ct);
                }
                process.StandardInput.Close();
                await process.WaitForExitAsync(ct);
                await reading;
                var data = output.ToArray();
                var headerText = Encoding.UTF8.GetString(data);
                var headerEnd = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var separator = 4;
                if (headerEnd < 0) { headerEnd = headerText.IndexOf("\n\n", StringComparison.Ordinal); separator = 2; }
                Assert.True(headerEnd >= 0, await errors);
                var headerBytes = Encoding.UTF8.GetByteCount(headerText[..headerEnd]);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(data[(headerBytes + separator)..]) };
                foreach (var line in headerText[..headerEnd].Split('\n'))
                {
                    var parts = line.TrimEnd('\r').Split(':', 2);
                    if (parts.Length != 2) continue;
                    if (parts[0].Equals("Status", StringComparison.OrdinalIgnoreCase))
                        response.StatusCode = (HttpStatusCode)int.Parse(parts[1].Trim().Split(' ')[0]);
                    else if (parts[0].Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(parts[1].Trim());
                }
                return response;
            }
        }
    }

    private sealed class CountedStream(Stream target, Action<int> count) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override int Read(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int length) { target.Write(buffer, offset, length); count(length); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { await target.WriteAsync(buffer, ct); count(buffer.Length); }
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, true);
    }
}
