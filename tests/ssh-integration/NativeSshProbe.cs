using System.Diagnostics;
using System.Security.Cryptography;
using GitLlama.Git;
using LibGit2Sharp;

internal static class NativeSshProbe
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "network-investigation", "fixtures", "ssh-" + Guid.NewGuid().ToString("N"));
        var seed = Path.Combine(root, "seed");
        Repository.Init(seed);
        var signature = new Signature("Test", "test@example.invalid", DateTimeOffset.UtcNow);
        using (var repo = new Repository(seed))
        {
            File.WriteAllText(Path.Combine(seed, "initial.txt"), "initial\n");
            Commands.Stage(repo, "initial.txt");
            repo.Commit("initial", signature, signature);
        }
        var bare = Path.Combine(root, "remote.git");
        Repository.Clone(seed, bare, new CloneOptions { IsBare = true });
        var start = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(Directory.GetCurrentDirectory(), "tests", "ssh-integration", "server.py"));
        start.ArgumentList.Add(bare);
        using var server = Process.Start(start)!;
        var errors = server.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            var line = await server.StandardOutput.ReadLineAsync(timeout.Token);
            if (!int.TryParse(line, out var port)) throw new Exception("Fixture server failed: " + await errors);
            var keyPath = Path.Combine(root, "key.pem");
            using (var rsa = RSA.Create(2048)) File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());
            using var session = new SshKeySession(Path.Combine(root, "trusted.json"));
            var prompts = 0;
            session.RequestHostTrust = (_, _) => { prompts++; return Task.FromResult(true); };
            var options = new GitNetworkOptions { UseSshAgent = false, SshPrivateKeyPath = keyPath, SshSession = session };
            var url = $"ssh://git@127.0.0.1:{port}/repo";
            var clone = Path.Combine(root, "clone");
            await new GitRepositoryLifecycleService().CloneAsync(url, clone, options, timeout.Token);
            Console.WriteLine("Direct SSH clone passed; host prompts=" + prompts);
            using (var repo = new Repository(clone))
            {
                File.WriteAllBytes(Path.Combine(clone, "new.bin"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
                Commands.Stage(repo, "new.bin");
                repo.Commit("native SSH push", signature, signature);
                repo.ApplyTag("native-ssh");
            }
            var remote = new GitRemoteService();
            await remote.PushCurrentBranchAsync(clone, "origin", timeout.Token, options);
            Console.WriteLine("Direct SSH 2 MB push passed; host prompts=" + prompts);
            await remote.FetchAsync(clone, "origin", timeout.Token, options);
            using var check = new Repository(bare);
            Console.WriteLine("Direct SSH fetch and tag passed: " + check.Head.Tip.MessageShort + "; tag=" + check.Tags["native-ssh"]?.FriendlyName);
            var puttyKey = Path.Combine(root, "key.ppk");
            File.WriteAllText(puttyKey, GitLlama.Core.Tests.SshKeySessionTests.CreateEncryptedTestKey("fixture-passphrase"));
            var unlocks = 0;
            session.RequestPassphrase = (_, _) => { unlocks++; return Task.FromResult<string?>("fixture-passphrase"); };
            var puttyOptions = new GitNetworkOptions { UseSshAgent = false, SshPrivateKeyPath = puttyKey, SshSession = session };
            var puttyClone = Path.Combine(root, "putty-clone");
            await new GitRepositoryLifecycleService().CloneAsync(url, puttyClone, puttyOptions, timeout.Token);
            await remote.FetchAsync(puttyClone, "origin", timeout.Token, puttyOptions);
            if (unlocks != 1 || prompts != 1) throw new Exception("SSH key unlock or host trust was not reused.");
            Console.WriteLine("Encrypted PuTTY clone and fetch passed without Pageant; unlock prompts=" + unlocks);
            var launchProperty = typeof(GitNetworkOptions).GetProperty("SshProcessFactory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            using var agentSession = new SshKeySession(Path.Combine(root, "agent-trusted.json"));
            var agentPrompts = 0;
            agentSession.RequestHostTrust = (_, _) => { agentPrompts++; return Task.FromResult(true); };
            var agentOptions = new GitNetworkOptions { UseSshAgent = true, SshSession = agentSession };
            // A disposable identity isolates this compatibility-path test from the real user's agent and keys.
            launchProperty.SetValue(agentOptions, (Func<ProcessStartInfo, Process>)(info =>
            {
                info.ArgumentList.Insert(0, keyPath);
                info.ArgumentList.Insert(0, "-i");
                info.ArgumentList.Insert(0, "IdentitiesOnly=yes");
                info.ArgumentList.Insert(0, "-o");
                var bundledSsh = "C:/Program Files/Git/usr/bin/ssh.exe";
                info.FileName = Environment.GetEnvironmentVariable("GITLLAMA_TEST_SSH")
                    ?? (OperatingSystem.IsWindows() && File.Exists(bundledSsh) ? bundledSsh : "ssh");
                return Process.Start(info)!;
            }));
            await new GitRepositoryLifecycleService().CloneAsync(url, Path.Combine(root, "agent-clone"), agentOptions, timeout.Token);
            await remote.FetchAsync(Path.Combine(root, "agent-clone"), "origin", timeout.Token, agentOptions);
            if (agentPrompts != 1) throw new Exception("Agent host trust was not reused.");
            Console.WriteLine("OpenSSH compatibility clone passed; in-app host prompts=" + agentPrompts);
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}
