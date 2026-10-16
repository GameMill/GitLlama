using System.Text.RegularExpressions;
using GitLlama.Core;

namespace GitLlama.Desktop.Views;

public static class IntegrationBrowserPolicy
{
    public static bool Allows(Uri uri)
    {
        if (
            !uri.IsAbsoluteUri
            || uri.Scheme != "https"
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
        )
        {
            return false;
        }

        if (uri.Host == "github.com")
        {
            return uri.AbsolutePath == "/login/device"
                || Regex.IsMatch(uri.AbsolutePath, @"^/settings/connections/applications/[A-Za-z0-9]+$");
        }

        if (uri.Host is "microsoft.com" or "www.microsoft.com")
            return uri.AbsolutePath.TrimEnd('/') == "/devicelogin";

        if (uri.Host == "login.microsoftonline.com")
            return Regex.IsMatch(uri.AbsolutePath, @"^/(common|organizations|[0-9a-fA-F-]{36})/oauth2/deviceauth/?$");

        return uri.Host == "dev.azure.com"
            && uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                is [var org, "_usersSettings", "tokens"]
            && AzureDevOpsRemote.ValidOrganization(org);
    }
}
