using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>libgit2 processes Git data; .NET handles verified HTTPS and streaming HTTP requests.</summary>
public sealed class ManagedHttpsTransport : RpcSmartSubtransport
{
    private readonly GitTransportScope? scope = GitTransportScope.Current;
    private readonly GitNetworkOptions options = GitTransportScope.Current?.Options ?? new();
    private readonly CancellationToken cancellation = GitTransportScope.Current?.Cancellation ?? default;
    private HttpClient? client;
    private Uri? repository;
    private AuthenticationHeaderValue? authorization;

    protected override SmartSubtransportStream Action(string url, GitSmartSubtransportAction action)
    {
        try { return CreateStream(url, action); }
        catch (Exception error) { scope?.Fail(error); throw; }
    }

    private SmartSubtransportStream CreateStream(string url, GitSmartSubtransportAction action)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme != "https"
            || !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Query) || !string.IsNullOrEmpty(target.Fragment))
            throw new InvalidOperationException("Use an HTTPS repository URL without embedded credentials, query, or fragment.");
        options.EnsureAzureAccess(url);
        if (repository is not null && !SameOrigin(repository, target))
            throw new InvalidOperationException("The HTTPS repository host changed during the operation.");
        repository ??= target;
        client ??= new HttpClient(options.HttpsHandlerFactory?.Invoke() ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        }) { Timeout = Timeout.InfiniteTimeSpan };

        if (authorization is null && options.UsesAzureToken(url))
        {
            authorization = string.IsNullOrEmpty(options.AzurePersonalAccessToken)
                ? new("Bearer", options.AzureOAuthTokenProvider!(cancellation).GetAwaiter().GetResult())
                : Basic("GitLlama", options.AzurePersonalAccessToken);
        }
        else if (authorization is null && !string.IsNullOrEmpty(options.Secret)
            && target.Authority.Equals(options.CredentialHost, StringComparison.OrdinalIgnoreCase))
            authorization = Basic(string.IsNullOrWhiteSpace(options.Username) ? "x-access-token" : options.Username, options.Secret);

        var post = action is GitSmartSubtransportAction.UploadPack or GitSmartSubtransportAction.ReceivePack;
        var service = action is GitSmartSubtransportAction.ReceivePackList or GitSmartSubtransportAction.ReceivePack
            ? "git-receive-pack" : "git-upload-pack";
        return new HttpsStream(this, post, service, cancellation);
    }

    private static AuthenticationHeaderValue Basic(string user, string secret) =>
        new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(user + ":" + secret)));

    internal static bool SameOrigin(Uri first, Uri second) => first.Scheme == second.Scheme
        && first.Host.Equals(second.Host, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port;

    private HttpRequestMessage Request(bool post, string service, Uri endpoint, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, endpoint) { Content = content };
        request.Headers.UserAgent.ParseAdd("git/2.0 (GitLlama)");
        request.Headers.Authorization = authorization;
        request.Headers.Accept.Add(new("application/x-" + service + (post ? "-result" : "-advertisement")));
        if (content is not null) content.Headers.ContentType = new("application/x-" + service + "-request");
        return request;
    }

    private HttpResponseMessage Discover(string service, CancellationToken ct)
    {
        var endpoint = new Uri(repository!.AbsoluteUri.TrimEnd('/') + "/info/refs?service=" + service);
        var authenticated = authorization is not null;
        for (var attempt = 0; attempt < 7; attempt++)
        {
            using var request = Request(false, service, endpoint);
            var response = client!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
            if (response.StatusCode == HttpStatusCode.Unauthorized && !authenticated)
            {
                // Release the network response before a browser-based credential helper waits for the user.
                response.Dispose();
                ct.ThrowIfCancellationRequested();
                var result = AcquireCredentials(out var credentials, options.Username, typeof(UsernamePasswordCredentials));
                if (result != 0 || credentials is not UsernamePasswordCredentials password)
                    throw new InvalidOperationException("Sign in through Settings > Integrations or Authentication to access this repository.");
                authorization = Basic(password.Username, password.Password);
                authenticated = true;
                continue;
            }
            if ((int)response.StatusCode is 301 or 302 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidOperationException("The server returned an invalid repository redirect.");
                var redirected = new Uri(endpoint, location);
                if (!SameOrigin(repository, redirected) || !string.IsNullOrEmpty(redirected.UserInfo)
                    || !redirected.AbsolutePath.EndsWith("/info/refs", StringComparison.Ordinal)
                    || redirected.Query != "?service=" + service || !string.IsNullOrEmpty(redirected.Fragment))
                    throw new InvalidOperationException("The repository redirected to another address. Update its remote URL before connecting.");
                var redirectedRepository = new UriBuilder(redirected) { Path = redirected.AbsolutePath[..^10], Query = "" }.Uri;
                options.EnsureAzureAccess(redirectedRepository.AbsoluteUri);
                repository = redirectedRepository;
                endpoint = redirected;
                continue;
            }
            Validate(response, "application/x-" + service + "-advertisement");
            return response;
        }
        throw new InvalidOperationException("The repository returned too many redirects or authentication challenges.");
    }

    private static void Validate(HttpResponseMessage response, string expectedType)
    {
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException(status switch
            {
                HttpStatusCode.Unauthorized => "HTTPS authentication was rejected. Reconnect your account or check your credential helper.",
                HttpStatusCode.Forbidden => "The server denied repository access. Check your account permissions and organization access.",
                HttpStatusCode.NotFound => "The repository was not found, or your account does not have access to it.",
                _ => $"The Git server returned HTTP {(int)status}. Check the remote URL and connection, then retry.",
            });
        }
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            response.Dispose();
            throw new InvalidOperationException("The server returned a web page instead of Git data. Check the repository URL and account access.");
        }
    }

    protected override void Dispose()
    {
        client?.Dispose();
        base.Dispose();
    }

    private sealed class HttpsStream : SmartSubtransportStream
    {
        private readonly ManagedHttpsTransport owner;
        private readonly bool post;
        private readonly string service;
        private readonly CancellationTokenSource stop;
        private StreamingBody? body;
        private Task<HttpResponseMessage>? sending;
        private HttpRequestMessage? request;
        private HttpResponseMessage? response;
        private Stream? input;

        public HttpsStream(ManagedHttpsTransport owner, bool post, string service, CancellationToken ct) : base(owner)
        {
            this.owner = owner;
            this.post = post;
            this.service = service;
            stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        public override int Write(Stream dataStream, long length)
        {
            try { return WriteCore(dataStream, length); }
            catch (Exception error) { owner.scope?.Fail(error); throw; }
        }

        private int WriteCore(Stream dataStream, long length)
        {
            stop.Token.ThrowIfCancellationRequested();
            if (!post) throw new InvalidOperationException("Unexpected data in a Git discovery request.");
            if (sending is null)
            {
                body = new StreamingBody(stop.Token);
                request = owner.Request(true, service, new Uri(owner.repository!.AbsoluteUri.TrimEnd('/') + "/" + service), body);
                sending = owner.client!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
            }
            while (length > 0)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                var transferred = false;
                try
                {
                    var count = dataStream.Read(buffer, 0, (int)Math.Min(length, 65536));
                    if (count == 0) throw new EndOfStreamException("The Git request ended unexpectedly.");
                    var writing = body!.EnqueueAsync(buffer, count).AsTask();
                    if (Task.WhenAny(writing, sending).GetAwaiter().GetResult() == sending && !writing.IsCompleted)
                    {
                        stop.Cancel();
                        try { writing.GetAwaiter().GetResult(); transferred = true; }
                        catch (OperationCanceledException) { }
                        using var earlyResponse = sending.GetAwaiter().GetResult();
                        Validate(earlyResponse, "application/x-" + service + "-result");
                        throw new InvalidOperationException("The Git server ended the upload before all data was sent.");
                    }
                    writing.GetAwaiter().GetResult();
                    transferred = true;
                    length -= count;
                }
                catch (HttpRequestException) { throw ConnectionError(); }
                finally { if (!transferred) ArrayPool<byte>.Shared.Return(buffer); }
            }
            return 0;
        }

        public override int Read(Stream dataStream, long length, out long bytesRead)
        {
            try { return ReadCore(dataStream, length, out bytesRead); }
            catch (Exception error) { owner.scope?.Fail(error); throw; }
        }

        private int ReadCore(Stream dataStream, long length, out long bytesRead)
        {
            stop.Token.ThrowIfCancellationRequested();
            try
            {
                if (input is null)
                {
                    if (post)
                    {
                        if (sending is null) throw new InvalidOperationException("The Git request has no data.");
                        body!.Complete();
                        response = sending.GetAwaiter().GetResult();
                        Validate(response, "application/x-" + service + "-result");
                    }
                    else response = owner.Discover(service, stop.Token);
                    input = response.Content.ReadAsStream(stop.Token);
                }
                var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 65536));
                try
                {
                    var count = input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, 65536)), stop.Token).GetAwaiter().GetResult();
                    dataStream.Write(buffer, 0, count);
                    bytesRead = count;
                    return 0;
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            catch (HttpRequestException) { throw ConnectionError(); }
            catch (IOException) { throw ConnectionError(); }
        }

        private static InvalidOperationException ConnectionError() => new(
            "The secure HTTPS connection was interrupted or could not be verified. Check the network, proxy, or security software, then retry.");

        protected override void Free()
        {
            stop.Cancel();
            body?.Complete();
            input?.Dispose();
            response?.Dispose();
            request?.Dispose();
            body?.Dispose();
            stop.Dispose();
            base.Free();
        }
    }

    /// <summary>Bounded producer/consumer upload: pack data is streamed, never buffered as a complete file.</summary>
    private sealed class StreamingBody(CancellationToken ct) : HttpContent
    {
        private readonly Channel<(byte[] Buffer, int Count)> blocks = Channel.CreateBounded<(byte[], int)>(8);
        public ValueTask EnqueueAsync(byte[] buffer, int count) => blocks.Writer.WriteAsync((buffer, count), ct);
        public void Complete() => blocks.Writer.TryComplete();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            try
            {
                await foreach (var block in blocks.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try { await stream.WriteAsync(block.Buffer.AsMemory(0, block.Count), ct).ConfigureAwait(false); }
                    finally { ArrayPool<byte>.Shared.Return(block.Buffer); }
                }
            }
            finally { Drain(); }
        }
        private void Drain() { while (blocks.Reader.TryRead(out var block)) ArrayPool<byte>.Shared.Return(block.Buffer); }
        protected override void Dispose(bool disposing) { Complete(); if (disposing) Drain(); base.Dispose(disposing); }
    }
}
