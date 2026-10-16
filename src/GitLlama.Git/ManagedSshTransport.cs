using System.Diagnostics;
using LibGit2Sharp;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GitLlama.Git;

/// <summary>Git protocol stays in libgit2; private keys use SSH.NET and agents use an SSH compatibility process.</summary>
public sealed class ManagedSshTransport : SmartSubtransport
{
    private readonly GitTransportScope? scope = GitTransportScope.Current;
    private readonly GitNetworkOptions options = GitTransportScope.Current?.Options ?? new();
    private readonly CancellationToken cancellation = GitTransportScope.Current?.Cancellation ?? default;
    private SshStream? stream;

    protected override SmartSubtransportStream Action(string url, GitSmartSubtransportAction action)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            options.EnsureAzureAccess(url);
            var receive = action is GitSmartSubtransportAction.ReceivePackList or GitSmartSubtransportAction.ReceivePack;
            var list = action is GitSmartSubtransportAction.UploadPackList or GitSmartSubtransportAction.ReceivePackList;
            if (list)
            {
                stream?.Release();
                stream = new SshStream(this, Parse(url), receive);
            }
            return stream ?? throw new InvalidOperationException("The SSH Git session is not connected.");
        }
        catch (Exception error) { scope?.Fail(error); throw; }
    }

    internal sealed record Target(string Host, int Port, string User, string Path)
    {
        public string Command(bool receive) => (receive ? "git-receive-pack " : "git-upload-pack ") + "'" + Path.Replace("'", "'\\''") + "'";
    }

    internal static Target Parse(string url)
    {
        var endpoint = ManagedSshBridge.Endpoint(url);
        string path;
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.UserInfo.Contains(':'))
                throw new InvalidOperationException("Use an SSH URL with a username and repository path only.");
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else path = url[(url.IndexOf(':') + 1)..];
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || path.StartsWith('-')
            || endpoint.User.Any(char.IsControl) || endpoint.User.StartsWith('-'))
            throw new InvalidOperationException("The SSH repository address is invalid.");
        return new(endpoint.Host, endpoint.Port, string.IsNullOrEmpty(endpoint.User) ? "git" : endpoint.User, path);
    }

    protected override void Close()
    {
        stream?.Release();
        stream = null;
        base.Close();
    }

    private sealed class SshStream(ManagedSshTransport owner, Target target, bool receive) : SmartSubtransportStream(owner)
    {
        private SshClient? client;
        private SshCommand? command;
        private Task? execution;
        private Process? process;
        private Task<string>? error;
        private Stream? input;
        private Stream? output;
        private int released;
        private bool retried;

        private void Connect()
        {
            if (output is not null) return;
            if (owner.options.UseSshAgent) { StartAgent(); return; }
            if (owner.options.SshSession is not { } session || string.IsNullOrWhiteSpace(owner.options.SshPrivateKeyPath))
                throw new InvalidOperationException("Choose an SSH private key or enable the local SSH agent in Settings > SSH.");
            var key = session.UnlockAsync(owner.options.SshPrivateKeyPath, owner.cancellation).GetAwaiter().GetResult();
            var connection = new ConnectionInfo(target.Host, target.Port, target.User,
                new PrivateKeyAuthenticationMethod(target.User, new ModernSshKey(key))) { Timeout = TimeSpan.FromSeconds(30) };
            client = new SshClient(connection);
            Exception? trustFailure = null;
            client.HostKeyReceived += (_, args) =>
            {
                args.CanTrust = false;
                try { args.CanTrust = session.VerifyHostAsync(target.Host, target.Port, args.HostKeyName, args.HostKey, owner.cancellation).GetAwaiter().GetResult(); }
                catch (Exception failure) { trustFailure = failure; }
            };
            try { client.ConnectAsync(owner.cancellation).GetAwaiter().GetResult(); }
            catch (Exception failure) when (failure is SshException or System.Net.Sockets.SocketException)
            {
                if (trustFailure is not null) throw trustFailure;
                throw new InvalidOperationException("SSH authentication or host verification failed. Check your key, server fingerprint, and repository access.");
            }
            command = client.CreateCommand(target.Command(receive));
            command.CommandTimeout = Timeout.InfiniteTimeSpan;
            execution = command.ExecuteAsync(owner.cancellation);
            output = command.OutputStream;
            input = command.CreateInputStream();
        }

        private void StartAgent()
        {
            var putty = owner.options.SshPrivateKeyPath.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(putty ? GitSystemTransport.FindPlink()
                ?? throw new InvalidOperationException("Install PuTTY to use your PuTTY key with Pageant.") : "ssh")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            if (putty)
            {
                foreach (var argument in new[] { "-ssh", "-batch", "-agent", "-P", target.Port.ToString() }) start.ArgumentList.Add(argument);
                if (!string.IsNullOrWhiteSpace(owner.options.SshPrivateKeyPath))
                { start.ArgumentList.Add("-i"); start.ArgumentList.Add(Path.GetFullPath(owner.options.SshPrivateKeyPath)); }
                if (owner.options.SshSession?.KnownHostsFile is { } known && File.Exists(known))
                {
                    var host = target.Port == 22 ? target.Host : $"[{target.Host}]:{target.Port}";
                    foreach (var line in File.ReadLines(known))
                    {
                        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length != 3 || fields[0] != host) continue;
                        var fingerprint = "SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(fields[2]))).TrimEnd('=');
                        start.ArgumentList.Add("-hostkey");
                        start.ArgumentList.Add(fingerprint);
                    }
                }
            }
            else
            {
                foreach (var argument in new[] { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-p", target.Port.ToString() }) start.ArgumentList.Add(argument);
                if (owner.options.SshSession?.KnownHostsFile is { } known && File.Exists(known))
                {
                    start.ArgumentList.Add("-o");
                    start.ArgumentList.Add("UserKnownHostsFile=\"~/.ssh/known_hosts\" \"~/.ssh/known_hosts2\" \"" + known.Replace('\\', '/').Replace("\"", "\\\"") + "\"");
                }
            }
            start.ArgumentList.Add(target.User + "@" + target.Host);
            start.ArgumentList.Add(target.Command(receive));
            try { process = owner.options.SshProcessFactory is { } factory ? factory(start) : Process.Start(start)!; }
            catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Install OpenSSH or choose a private key with agent mode off in Settings > SSH."); }
            error = process.StandardError.ReadToEndAsync(owner.cancellation);
            input = process.StandardInput.BaseStream;
            output = process.StandardOutput.BaseStream;
        }

        private bool TrustUnknownAgentHost(string failure)
        {
            // Never hide an existing OpenSSH changed-host warning by adding another trust entry.
            var unknown = failure.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("host key is not cached", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("Cannot confirm a host key", StringComparison.OrdinalIgnoreCase);
            if (retried || !unknown
                || failure.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("POTENTIAL SECURITY BREACH", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("does not match", StringComparison.OrdinalIgnoreCase)
                || owner.options.SshSession is not { KnownHostsFile: not null } session) return false;
            retried = true;
            var accepted = false;
            Exception? trustFailure = null;
            using var probe = new SshClient(new ConnectionInfo(target.Host, target.Port, target.User,
                new NoneAuthenticationMethod(target.User)) { Timeout = TimeSpan.FromSeconds(30) });
            probe.HostKeyReceived += (_, args) =>
            {
                args.CanTrust = false;
                try { accepted = args.CanTrust = session.VerifyHostAsync(target.Host, target.Port, args.HostKeyName, args.HostKey, owner.cancellation).GetAwaiter().GetResult(); }
                catch (Exception failure) { trustFailure = failure; }
            };
            try { probe.ConnectAsync(owner.cancellation).GetAwaiter().GetResult(); }
            catch (SshAuthenticationException) when (accepted) { /* Only the host key was requested. */ }
            catch (SshConnectionException) when (trustFailure is not null) { throw trustFailure; }
            catch (SshConnectionException) when (!accepted) { throw new OperationCanceledException("SSH host verification canceled."); }
            if (!accepted) throw new OperationCanceledException("SSH host verification canceled.");
            process!.Dispose();
            process = null;
            input = output = null;
            StartAgent();
            return true;
        }

        public override int Read(Stream dataStream, long length, out long bytesRead)
        {
            try
            {
                Connect();
                var buffer = new byte[(int)Math.Min(length, 65536)];
                var count = output!.ReadAsync(buffer.AsMemory(), owner.cancellation).GetAwaiter().GetResult();
                if (count == 0 && process is not null)
                {
                    process.WaitForExitAsync(owner.cancellation).GetAwaiter().GetResult();
                    if (process.ExitCode != 0)
                    {
                        var failure = error!.GetAwaiter().GetResult();
                        if (TrustUnknownAgentHost(failure)) return Read(dataStream, length, out bytesRead);
                        throw new InvalidOperationException(GitSystemTransport.ConnectionFailureMessage(failure, process.StartInfo.FileName.Contains("plink", StringComparison.OrdinalIgnoreCase)));
                    }
                }
                else if (count == 0 && command is not null)
                {
                    execution!.GetAwaiter().GetResult();
                    if (command.ExitStatus != 0) throw new InvalidOperationException("The SSH server rejected the Git operation. Check the repository URL and account access.");
                }
                dataStream.Write(buffer, 0, count);
                bytesRead = count;
                return 0;
            }
            catch (Exception failure) { owner.scope?.Fail(failure); throw; }
        }

        public override int Write(Stream dataStream, long length)
        {
            try
            {
                Connect();
                var buffer = new byte[65536];
                while (length > 0)
                {
                    owner.cancellation.ThrowIfCancellationRequested();
                    var count = dataStream.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
                    if (count == 0) throw new EndOfStreamException();
                    input!.WriteAsync(buffer.AsMemory(0, count), owner.cancellation).GetAwaiter().GetResult();
                    length -= count;
                }
                input!.FlushAsync(owner.cancellation).GetAwaiter().GetResult();
                return 0;
            }
            catch (IOException) when (process is not null)
            {
                owner.cancellation.ThrowIfCancellationRequested();
                var message = process.WaitForExit(1000) ? error!.GetAwaiter().GetResult() : "";
                var failure = new InvalidOperationException(GitSystemTransport.ConnectionFailureMessage(message, process.StartInfo.FileName.Contains("plink", StringComparison.OrdinalIgnoreCase)));
                owner.scope?.Fail(failure);
                throw failure;
            }
            catch (Exception failure) { owner.scope?.Fail(failure); throw; }
        }

        internal void Release()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            input?.Dispose();
            command?.Dispose();
            client?.Dispose();
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
        protected override void Free() { Release(); base.Free(); }
    }
}
