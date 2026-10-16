using System.Diagnostics;
using GitLlama.Core.Tests;
using GitLlama.Git;
using LibGit2Sharp;

// Manual acceptance: dotnet run --project tests/ssh-integration/Acceptance.csproj -- <helper-exe> <Paramiko-library-directory>
// Native transport acceptance: set PYTHONPATH to Paramiko's directory, then run with --native.
if (args.Contains("--native")) { await NativeSshProbe.RunAsync(); return; }
var root = Path.Combine(Path.GetTempPath(), "GitLlama-SSH-acceptance-" + Guid.NewGuid());
Directory.CreateDirectory(root);
Process? server = null;
try
{
    var seed = Path.Combine(root, "seed");
    Repository.Init(seed);
    using (var repository = new Repository(seed))
    {
        File.WriteAllText(Path.Combine(seed, "initial.txt"), "initial\n");
        Commands.Stage(repository, "initial.txt");
        var author = new Signature("Disposable test", "test@example.invalid", DateTimeOffset.UtcNow);
        repository.Commit("initial", author, author);
    }
    var remote = Path.Combine(root, "remote.git");
    Repository.Clone(seed, remote, new CloneOptions { IsBare = true });
    var start = new ProcessStartInfo("python")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    start.ArgumentList.Add(Path.Combine(Directory.GetCurrentDirectory(), "tests", "ssh-integration", "server.py"));
    start.ArgumentList.Add(remote);
    start.Environment["PYTHONPATH"] = args[1];
    server = Process.Start(start)!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var line =
        await server.StandardOutput.ReadLineAsync(timeout.Token)
        ?? throw new Exception("Local SSH test server did not start.");
    var port = int.Parse(line);
    var url = $"ssh://git@127.0.0.1:{port}/repo";
    var key = Path.Combine(root, "test.ppk");
    File.WriteAllText(key, SshKeySessionTests.CreateEncryptedTestKey("test-passphrase"));
    using var session = new SshKeySession();
    var unlocks = 0;
    var hosts = 0;
    session.RequestPassphrase = (_, _) =>
    {
        unlocks++;
        return Task.FromResult<string?>("test-passphrase");
    };
    session.RequestHostTrust = (_, _) =>
    {
        hosts++;
        return Task.FromResult(true);
    };
    var options = new GitNetworkOptions
    {
        SshSession = session,
        SshPrivateKeyPath = key,
        UseSshAgent = false,
        SshHelperCommand = GitSystemTransport.QuoteSshArgument(Path.GetFullPath(args[0])),
    };
    var clone = Path.Combine(root, "clone");
    await GitSystemTransport.RunAsync(
        null,
        ["clone", "--", url, clone],
        options,
        timeout.Token,
        ssh: true,
        sshRemoteUrl: url
    );
    if (File.ReadAllText(Path.Combine(clone, "initial.txt")).Replace("\r\n", "\n") != "initial\n")
    {
        throw new Exception("Clone content mismatch.");
    }
    string branch;
    using (var repository = new Repository(clone))
    {
        branch = repository.Head.FriendlyName;
        File.WriteAllText(
            Path.Combine(clone, "new.bin"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(128 * 1024))
        );
        Commands.Stage(repository, "new.bin");
        var author = new Signature("Disposable test", "test@example.invalid", DateTimeOffset.UtcNow);
        repository.Commit("push acceptance", author, author);
    }
    await GitSystemTransport.RunAsync(
        clone,
        ["push", "origin", branch],
        options,
        timeout.Token,
        ssh: true,
        sshRemoteUrl: url
    );
    using (var repository = new Repository(remote))
    {
        if (repository.Branches[branch].Tip.MessageShort != "push acceptance")
        {
            throw new Exception("Remote push was not stored.");
        }
    }
    var remotes = new GitRemoteService();
    await remotes.ApplyBranchAsync(
        await remotes.PrepareBranchAsync(clone, "origin", null),
        RemoteBranchOperation.Create,
        "feature/test",
        options,
        timeout.Token
    );
    await remotes.ApplyBranchAsync(
        await remotes.PrepareBranchAsync(clone, "origin", "feature/test"),
        RemoteBranchOperation.Rename,
        "feature/renamed",
        options,
        timeout.Token
    );
    await remotes.ApplyBranchAsync(
        await remotes.PrepareBranchAsync(clone, "origin", "feature/renamed"),
        RemoteBranchOperation.Delete,
        "",
        options,
        timeout.Token
    );
    using (var repository = new Repository(remote))
    {
        if (repository.Branches["feature/test"] is not null || repository.Branches["feature/renamed"] is not null)
        {
            throw new Exception("Remote branch edit result mismatch.");
        }
    }
    if (unlocks != 1 || hosts != 1)
    {
        throw new Exception($"Expected one unlock/trust prompt; got {unlocks}/{hosts}.");
    }
    session.Clear();
    await GitSystemTransport.CheckConnectionAsync(url, clone, options, timeout.Token);
    if (unlocks != 2)
    {
        throw new Exception("Session lock did not require another unlock.");
    }
    Console.WriteLine(
        "PASS: real SSH clone, binary push, remote branch create/rename/delete and read check without Pageant; one session unlock; locking requires another unlock."
    );
}
finally
{
    if (server is not null)
    {
        if (!server.HasExited)
        {
            server.Kill(entireProcessTree: true);
            server.WaitForExit();
        }
        Console.Error.Write(await server.StandardError.ReadToEndAsync());
        server.Dispose();
    }
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        File.SetAttributes(file, FileAttributes.Normal);
    }
    Directory.Delete(root, recursive: true);
}
