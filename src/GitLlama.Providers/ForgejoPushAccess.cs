using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GitLlama.Core;

namespace GitLlama.Providers;

public enum ForgejoPushAccess
{
    Allowed,
    Unknown,
    NoAccess,
    NoWritePermission,
    SignInRequired,
}

public sealed class ForgejoPushAccessChecker : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _api;

    public ForgejoPushAccessChecker(HttpClient? client = null, Uri? baseUri = null)
    {
        _ownsClient = client is null;
        _client =
            client
            ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
        _api = new Uri(ForgejoDefaults.Normalize((baseUri ?? ForgejoDefaults.BaseUri).AbsoluteUri), "api/v1/");
    }

    public async Task<ForgejoPushAccess> CheckAsync(HostingRepository repository, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                _api,
                "repos/"
                    + Uri.EscapeDataString(repository.Owner)
                    + "/"
                    + Uri.EscapeDataString(repository.Name)
            )
        );
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("token", token.Trim());
        }

        request.Headers.UserAgent.ParseAdd("GitLlama/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return ForgejoPushAccess.SignInRequired;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return ForgejoPushAccess.NoAccess;
        }

        if (!response.IsSuccessStatusCode)
        {
            return ForgejoPushAccess.Unknown;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (
            json.RootElement.TryGetProperty("permissions", out var permissions)
            && permissions.TryGetProperty("push", out var push)
        )
        {
            return push.GetBoolean() ? ForgejoPushAccess.Allowed : ForgejoPushAccess.NoWritePermission;
        }

        return ForgejoPushAccess.Unknown;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
