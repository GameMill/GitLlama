using Microsoft.Identity.Client;

namespace GitLlama.Providers;

public sealed record AzureDeviceCode(string Code, Uri VerificationUri);
public sealed record AzureOAuthIdentity(string AccountId, string Login);

public interface IAzureDevOpsOAuth
{
    Task<AzureOAuthIdentity> SignInAsync(Func<AzureDeviceCode, Task> showCode, CancellationToken ct);
    Task<string> AccessTokenAsync(string accountId, CancellationToken ct);
    byte[] ExportCache();
}

/// <summary>Microsoft's public-client device flow with silent renewal through its token cache.</summary>
public sealed class AzureDevOpsOAuth : IAzureDevOpsOAuth
{
    public const string DefaultClientId = "098479fa-bc66-410a-b9b3-5572573018ce";
    public const string DefaultTenantId = "a0f075e9-7679-4e83-a736-510c3798206a";
    private static readonly string[] Scopes = ["499b84ac-1321-427f-aa17-267ca6975798/.default"];
    private readonly IPublicClientApplication application;
    private byte[] tokenCache;
    private readonly object cacheLock = new();

    public AzureDevOpsOAuth(string clientId, string tenant, byte[]? cache = null)
    {
        if (!Guid.TryParse(clientId, out _) || !(Guid.TryParse(tenant, out _) || tenant == "organizations"))
            throw new ArgumentException("Enter a valid application ID and directory (tenant) ID, or use organizations for work or school accounts.");
        application = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority("https://login.microsoftonline.com/" + tenant)
            .WithDefaultRedirectUri().Build();
        tokenCache = cache?.ToArray() ?? [];
        application.UserTokenCache.SetBeforeAccess(args =>
        {
            lock (cacheLock)
                if (tokenCache.Length > 0) args.TokenCache.DeserializeMsalV3(tokenCache, shouldClearExistingCache: true);
        });
        application.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            lock (cacheLock)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(tokenCache);
                tokenCache = args.TokenCache.SerializeMsalV3();
            }
        });
    }

    public async Task<AzureOAuthIdentity> SignInAsync(Func<AzureDeviceCode, Task> showCode, CancellationToken ct)
    {
        try
        {
            var result = await application.AcquireTokenWithDeviceCode(Scopes,
                code => showCode(new(code.UserCode, new Uri(code.VerificationUrl)))).ExecuteAsync(ct);
            return new(result.Account.HomeAccountId.Identifier, result.Account.Username);
        }
        catch (MsalException error) { throw SignInError(error); }
    }

    public async Task<string> AccessTokenAsync(string accountId, CancellationToken ct)
    {
        var account = await application.GetAccountAsync(accountId);
        if (account is null)
            throw new InvalidOperationException("Reconnect your Microsoft account in Settings > Integrations.");
        try { return (await application.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct)).AccessToken; }
        catch (MsalUiRequiredException)
        {
            throw new InvalidOperationException("Microsoft requires you to sign in again. Reconnect in Settings > Integrations; your personal access token connections are unchanged.");
        }
        catch (MsalException error) { throw SignInError(error); }
    }

    private static InvalidOperationException SignInError(MsalException error) => new(
        error.Message.Contains("AADSTS650057", StringComparison.Ordinal)
            ? "The GitLlama app registration needs the Azure DevOps delegated user_impersonation permission. Add it in Microsoft Entra > App registrations > API permissions."
            : "Microsoft sign-in could not complete (" + error.ErrorCode + "). Check the directory ID, app permissions and public client device-flow setting, then retry.");

    public byte[] ExportCache() { lock (cacheLock) return tokenCache.ToArray(); }
}
