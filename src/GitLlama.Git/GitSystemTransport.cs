using System.Diagnostics;
using LibGit2Sharp;

namespace GitLlama.Git;

public static class GitSystemTransport
{
    public static bool IsSsh(string url) =>
        url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
        || (!Path.IsPathRooted(url) && url.Contains('@') && url.Contains(':') && !url.Contains("://"));

    public static async Task<string> RunAsync(
        string? repositoryPath,
        IEnumerable<string> arguments,
        GitNetworkOptions options,
        CancellationToken ct,
        string? input = null,
        bool ssh = false,
        string? sshRemoteUrl = null,
        string? httpsRemoteUrl = null
    )
    {
        ct.ThrowIfCancellationRequested();
        options.Progress?.Report(new("Connecting through system Git", 0, 0));
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!string.IsNullOrEmpty(repositoryPath))
        {
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(repositoryPath);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "true";
        foreach (
            var trace in new[] { "GIT_TRACE", "GIT_TRACE_CURL", "GIT_CURL_VERBOSE", "GCM_TRACE", "GCM_TRACE_SECRETS" }
        )
        {
            start.Environment.Remove(trace);
        }

        if (!ssh && options.AzureOAuthTokenProvider is not null && string.IsNullOrEmpty(options.AzurePersonalAccessToken))
        {
            var token = await options.AzureOAuthTokenProvider(ct).ConfigureAwait(false);
            ConfigureAzureBearerEnvironment(start, options, token);
        }
        else if (!ssh && !string.IsNullOrWhiteSpace(options.AzurePersonalAccessToken))
        {
            ConfigureAzurePatEnvironment(start, options);
        }
        else if (!ssh && httpsRemoteUrl is not null && !string.IsNullOrEmpty(options.Secret))
        {
            ConfigureHttpsTokenEnvironment(start, options, httpsRemoteUrl);
        }

        using var managed =
            ssh
            && !options.UseSshAgent
            && options.SshPrivateKeyPath.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase)
            && options.SshSession is not null
            && !string.IsNullOrEmpty(options.SshHelperCommand)
                ? new ManagedSshBridge(
                    options.SshSession,
                    options.SshPrivateKeyPath,
                    ManagedSshBridge.Endpoint(
                        sshRemoteUrl ?? throw new InvalidOperationException("SSH remote URL is missing.")
                    ),
                    ct
                )
                : null;
        if (managed is not null)
        {
            managed.Configure(start, options.SshHelperCommand);
        }
        else if (ssh)
        {
            ConfigureSshEnvironment(start, options);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "Install Git with Git Credential Manager and OpenSSH, then restart GitLlama."
            );
        }

        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
        });
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var result = await output.ConfigureAwait(false);
        var failure = await error.ConfigureAwait(false);
        if (managed is not null)
        {
            var connected = managed.Connected;
            if (!connected)
            {
                managed.Dispose();
            }

            await managed.Completion.ConfigureAwait(false);
            if (connected && managed.Failure is not null)
            {
                throw managed.Failure;
            }
        }

        if (process.ExitCode != 0)
        {
            var message = ConnectionFailureMessage(
                failure,
                start.Environment.TryGetValue("GIT_SSH_VARIANT", out var variant) && variant == "plink"
            );
            throw new InvalidOperationException(message);
        }

        options.Progress?.Report(new("Git operation completed", 0, 0));
        return result;
    }

    public static void ConfigureSshEnvironment(
        ProcessStartInfo start,
        GitNetworkOptions options,
        Func<string?>? findPlink = null
    )
    {
        // Override inherited transport settings only for this operation.
        start.Environment.Remove("GIT_SSH");
        if (options.SshPrivateKeyPath.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase))
        {
            var key = Path.GetFullPath(options.SshPrivateKeyPath);
            if (!File.Exists(key))
            {
                throw new InvalidOperationException("Choose an existing PuTTY private key (.ppk).");
            }

            var plink = (findPlink ?? FindPlink)();
            if (string.IsNullOrWhiteSpace(plink) || !File.Exists(plink))
            {
                throw new InvalidOperationException(
                    "Install PuTTY, including Plink, then retry. Your .ppk key does not need conversion."
                );
            }

            start.Environment["GIT_SSH_VARIANT"] = "plink";
            start.Environment["GIT_SSH_COMMAND"] =
                QuoteSshArgument(Path.GetFullPath(plink)) + " -ssh -batch -agent -i " + QuoteSshArgument(key);
            return;
        }

        start.Environment["GIT_SSH_VARIANT"] = "ssh";
        var command = "ssh -o BatchMode=yes -o StrictHostKeyChecking=yes";
        if (!options.UseSshAgent)
        {
            if (string.IsNullOrWhiteSpace(options.SshPrivateKeyPath))
            {
                throw new InvalidOperationException(
                    "Choose an OpenSSH or PuTTY private key, or enable the local SSH agent."
                );
            }

            var key = Path.GetFullPath(options.SshPrivateKeyPath);
            if (!File.Exists(key))
            {
                throw new InvalidOperationException("Choose an existing SSH private key.");
            }

            command += " -o IdentitiesOnly=yes -i " + QuoteSshArgument(key);
        }

        start.Environment["GIT_SSH_COMMAND"] = command;
    }

    // Git invokes GIT_SSH_COMMAND through its shell, including on Windows.
    public static string QuoteSshArgument(string path) => "'" + path.Replace("\\", "/").Replace("'", "'\"'\"'") + "'";

    public static string? FindPlink()
    {
        var executable = OperatingSystem.IsWindows() ? "plink.exe" : "plink";
        var directories = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PuTTY"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PuTTY"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "PuTTY"
            ),
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var directory in directories.Select(path => path.Trim('"')).Where(Path.IsPathFullyQualified))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string ConnectionFailureMessage(string failure, bool putty)
    {
        if (failure.Contains("stale info", StringComparison.OrdinalIgnoreCase))
        {
            return "The branch changed on the server, or the destination branch already exists. Fetch the remote and reopen this action; nothing was overwritten.";
        }

        if (failure.Contains("does not support --atomic", StringComparison.OrdinalIgnoreCase))
        {
            return "This server does not support atomic branch edits. The operation was not applied.";
        }

        if (
            failure.Contains("protected branch", StringComparison.OrdinalIgnoreCase)
            || failure.Contains("hook declined", StringComparison.OrdinalIgnoreCase)
        )
        {
            return "The server rejected the branch change because of repository protection rules or a server hook.";
        }

        if (
            putty
            && (
                failure.Contains("not cached", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("Cannot confirm a host key", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return "PuTTY has not trusted this server yet. Open Settings > SSH > Verify host in PuTTY, check the fingerprint against your hosting provider, and select Accept to save it. Then retry.";
        }

        if (
            putty
            && (
                failure.Contains("does not match", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("POTENTIAL SECURITY BREACH", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return "The server fingerprint differs from PuTTY's saved host key. Verify the change with your hosting provider before updating the trusted key through Settings > SSH > Verify host in PuTTY.";
        }

        if (
            putty
            && (
                failure.Contains("host key", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("host's key", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return "PuTTY host verification failed. Connect to this host with PuTTY, verify its fingerprint, and save the trusted host key before retrying.";
        }

        if (
            putty
            && failure.Contains("Cannot answer interactive prompts in batch mode", StringComparison.OrdinalIgnoreCase)
        )
        {
            return "PuTTY needs an interactive prompt. For an encrypted .ppk key, open Settings > SSH > Load key in Pageant, enter the passphrase there, and keep Pageant running. If the host is not trusted yet, use Verify host in PuTTY first. Then retry.";
        }

        if (
            putty
            && (
                failure.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("Server refused our key", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return "PuTTY key authentication failed. Check that this key is authorized; for an encrypted .ppk key, load and unlock it in Pageant before retrying.";
        }

        if (failure.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
        {
            return "SSH host verification failed. Verify the server fingerprint and add the host to OpenSSH known_hosts before connecting.";
        }

        if (failure.Contains("Permission denied (publickey", StringComparison.OrdinalIgnoreCase))
        {
            return "SSH key authentication failed. Load an authorized key into your agent, or choose the matching OpenSSH private key.";
        }

        if (failure.Contains("unlock file with uncommitted changes", StringComparison.OrdinalIgnoreCase))
        {
            return "This file has uncommitted changes. Commit or discard them before unlocking it, or use Force unlock.";
        }

        if (failure.Contains("Lock exists", StringComparison.OrdinalIgnoreCase))
        {
            return "This file is already locked.";
        }

        if (
            failure.Contains("unlock", StringComparison.OrdinalIgnoreCase)
            && (
                failure.Contains("403", StringComparison.Ordinal)
                || failure.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("locked by", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return "Someone else holds this lock. Use Force unlock if you have permission to release it.";
        }

        return "Git connection failed. Check account access, the configured credential helper, or SSH keys and trusted hosts.";
    }

    public static void ConfigureAzurePatEnvironment(ProcessStartInfo start, GitNetworkOptions options)
        => ConfigureAzureEnvironment(start, options, options.AzurePersonalAccessToken, bearer: false);

    public static void ConfigureAzureBearerEnvironment(ProcessStartInfo start, GitNetworkOptions options, string token)
        => ConfigureAzureEnvironment(start, options, token, bearer: true);

    private static void ConfigureAzureEnvironment(ProcessStartInfo start, GitNetworkOptions options, string token, bool bearer)
    {
        if (
            string.IsNullOrWhiteSpace(token)
            || token.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(options.AzureCredentialScopeUrl, UriKind.Absolute, out var scope)
            || scope.Scheme != "https"
            || !scope.IsDefaultPort
            || !string.IsNullOrEmpty(scope.UserInfo)
            || !string.IsNullOrEmpty(scope.Query)
            || !string.IsNullOrEmpty(scope.Fragment)
            || !scope.AbsolutePath.EndsWith('/')
        )
        {
            throw new InvalidOperationException("Invalid Azure DevOps credential scope.");
        }

        var org =
            scope.Host == "dev.azure.com" ? scope.AbsolutePath.Trim('/')
            : scope.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) ? scope.Host[..^17]
            : "";
        if (!GitLlama.Core.AzureDevOpsRemote.ValidOrganization(org))
        {
            throw new InvalidOperationException("Invalid Azure DevOps credential scope.");
        }

        foreach (
            var key in start
                .Environment.Keys.Where(key => key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase))
                .ToArray()
        )
        {
            start.Environment.Remove(key);
        }

        start.Environment["GIT_CONFIG_COUNT"] = "4";
        start.Environment["GIT_CONFIG_KEY_0"] = "http." + options.AzureCredentialScopeUrl + ".extraheader";
        start.Environment["GIT_CONFIG_VALUE_0"] =
            bearer ? "Authorization: Bearer " + token
                : "Authorization: Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + token));
        start.Environment["GIT_CONFIG_KEY_1"] = "http.followRedirects";
        start.Environment["GIT_CONFIG_VALUE_1"] = "false";
        start.Environment["GIT_CONFIG_KEY_2"] = "credential.helper";
        start.Environment["GIT_CONFIG_VALUE_2"] = "";
        start.Environment["GIT_CONFIG_KEY_3"] = "http.sslVerify";
        start.Environment["GIT_CONFIG_VALUE_3"] = "true";
        start.Environment["GCM_INTERACTIVE"] = "never";
    }

    public static void ConfigureHttpsTokenEnvironment(ProcessStartInfo start, GitNetworkOptions options, string url)
    {
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.Equals(uri.Authority, options.CredentialHost, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException("The HTTPS credential does not belong to this remote host.");
        }

        foreach (
            var key in start
                .Environment.Keys.Where(key => key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase))
                .ToArray()
        )
        {
            start.Environment.Remove(key);
        }

        var scope = uri.GetLeftPart(UriPartial.Authority) + "/";
        start.Environment["GIT_CONFIG_COUNT"] = "5";
        start.Environment["GIT_CONFIG_KEY_0"] = "http." + scope + ".extraheader";
        start.Environment["GIT_CONFIG_VALUE_0"] = "";
        start.Environment["GIT_CONFIG_KEY_1"] = "http." + scope + ".extraheader";
        start.Environment["GIT_CONFIG_VALUE_1"] =
            "Authorization: Basic "
            + Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(
                    (string.IsNullOrEmpty(options.Username) ? "git" : options.Username) + ":" + options.Secret
                )
            );
        start.Environment["GIT_CONFIG_KEY_2"] = "credential.helper";
        start.Environment["GIT_CONFIG_VALUE_2"] = "";
        start.Environment["GIT_CONFIG_KEY_3"] = "http.followRedirects";
        start.Environment["GIT_CONFIG_VALUE_3"] = "false";
        start.Environment["GIT_CONFIG_KEY_4"] = "http.sslVerify";
        start.Environment["GIT_CONFIG_VALUE_4"] = "true";
        start.Environment["GCM_INTERACTIVE"] = "never";
    }

    public static async Task<UsernamePasswordCredentials> GetCredentialsAsync(
        string url,
        string? repositoryPath,
        string username,
        CancellationToken ct
    )
    {
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo)
        )
        {
            throw new InvalidOperationException("Use an HTTPS URL without embedded credentials.");
        }

        if (username.Contains('\n') || username.Contains('\r'))
        {
            throw new ArgumentException("Invalid username.");
        }

        var request =
            $"protocol=https\nhost={uri.Authority}\npath={Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))}\n";
        if (request.Contains('\r') || Uri.UnescapeDataString(uri.AbsolutePath).Contains('\n'))
        {
            throw new ArgumentException("Invalid repository URL.");
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            request += "username=" + username + "\n";
        }

        var result = await RunAsync(repositoryPath, ["credential", "fill"], new GitNetworkOptions(), ct, request + "\n")
            .ConfigureAwait(false);
        return ParseCredentials(result, uri.Authority);
    }

    public static UsernamePasswordCredentials ParseCredentials(string response, string expectedAuthority)
    {
        var fields = response
            .Split('\n')
            .Where(line => line.Contains('='))
            .Select(line => line.TrimEnd('\r').Split('=', 2))
            .GroupBy(parts => parts[0])
            .ToDictionary(group => group.Key, group => group.Last()[1]);
        if (
            fields.GetValueOrDefault("protocol") != "https"
            || !string.Equals(fields.GetValueOrDefault("host"), expectedAuthority, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(fields.GetValueOrDefault("username"))
            || string.IsNullOrEmpty(fields.GetValueOrDefault("password"))
        )
        {
            throw new InvalidOperationException(
                "The credential helper did not return credentials for this HTTPS host."
            );
        }

        return new UsernamePasswordCredentials { Username = fields["username"], Password = fields["password"] };
    }

    public static async Task ForgetCredentialsAsync(string url, string? repositoryPath, CancellationToken ct)
    {
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo)
        )
        {
            throw new InvalidOperationException("Enter an HTTPS repository URL without embedded credentials.");
        }

        var request =
            $"protocol=https\nhost={uri.Authority}\npath={Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))}\n\n";
        if (Uri.UnescapeDataString(uri.AbsolutePath).IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("Invalid repository URL.");
        }

        await RunAsync(repositoryPath, ["credential", "reject"], new GitNetworkOptions(), ct, request)
            .ConfigureAwait(false);
    }

    public static async Task CheckConnectionAsync(
        string url,
        string? repositoryPath,
        GitNetworkOptions options,
        CancellationToken ct
    )
    {
        options.EnsureAzureAccess(url);
        if (IsSsh(url))
        {
            await RunAsync(repositoryPath, ["ls-remote", "--", url, "HEAD"], options, ct, ssh: true, sshRemoteUrl: url)
                .ConfigureAwait(false);
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
        {
            if (options.UsesAzureAuthorization(url))
            {
                await RunAsync(repositoryPath, ["ls-remote", "--", url, "HEAD"], options, ct).ConfigureAwait(false);
                return;
            }

            await GetCredentialsAsync(url, repositoryPath, options.Username, ct).ConfigureAwait(false);
            await RunAsync(repositoryPath, ["ls-remote", "--", url, "HEAD"], options, ct).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("Choose an HTTPS or SSH repository URL.");
    }
}
