using System.Security.Cryptography;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GitLlama.Git;

public sealed record SshUnlockRequest(string KeyPath, bool Retry);

public sealed record SshHostTrustRequest(
    string Host,
    int Port,
    string Algorithm,
    string Fingerprint,
    string? PreviousFingerprint
);

/// <summary>Decrypted keys remain in this process only. The trust file contains public fingerprints only.</summary>
public sealed class SshKeySession : IDisposable
{
    private readonly Dictionary<string, (string Hash, PrivateKeyFile Key)> _keys = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<string, string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _unlockGate = new(1);
    private readonly object _sync = new();
    private readonly string? _trustFile;
    public string? KnownHostsFile => _trustFile is null ? null : _trustFile + ".known_hosts";
    private int _generation;
    public Func<SshUnlockRequest, CancellationToken, Task<string?>>? RequestPassphrase { get; set; }
    public Func<SshHostTrustRequest, CancellationToken, Task<bool>>? RequestHostTrust { get; set; }

    public SshKeySession(string? trustFile = null)
    {
        _trustFile = trustFile;
        if (trustFile is not null && File.Exists(trustFile) && new FileInfo(trustFile).Length < 65536)
        {
            try
            {
                foreach (
                    var item in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(trustFile))
                        ?? []
                )
                {
                    _hosts[item.Key] = item.Value;
                }
            }
            catch (JsonException) { }
        }
    }

    public async Task<PrivateKeyFile> UnlockAsync(string path, CancellationToken ct)
    {
        await _unlockGate.WaitAsync(ct).ConfigureAwait(false);
        byte[]? bytes = null;
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
            {
                throw new InvalidOperationException("Choose an existing SSH private key smaller than 1 MB.");
            }

            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            int generation;
            lock (_sync)
            {
                generation = _generation;
                if (_keys.TryGetValue(path, out var cached) && cached.Hash == hash)
                {
                    return cached.Key;
                }

                if (_keys.Remove(path, out var previous))
                {
                    previous.Key.Dispose();
                }
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 256)));
            var encrypted =
                text.Contains("Encryption:", StringComparison.Ordinal)
                && !text.Contains("Encryption: none", StringComparison.Ordinal);
            PrivateKeyFile? key = null;
            if (!encrypted)
            {
                try
                {
                    key = new PrivateKeyFile(new MemoryStream(bytes, writable: false));
                }
                catch (SshPassPhraseNullOrEmptyException) { encrypted = true; }
                catch (Exception)
                {
                    throw new InvalidOperationException(
                        "The SSH private key could not be read. Choose a valid OpenSSH or PuTTY key."
                    );
                }
            }
            if (encrypted)
            {
                for (var attempt = 0; attempt < 3 && key is null; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var passphrase = RequestPassphrase is null
                        ? null
                        : await RequestPassphrase(new(path, attempt > 0), ct).ConfigureAwait(false);
                    if (passphrase is null)
                    {
                        throw new OperationCanceledException("SSH key unlock canceled.", ct);
                    }

                    try
                    {
                        key = new PrivateKeyFile(new MemoryStream(bytes, writable: false), passphrase);
                    }
                    catch (Exception)
                    {
                        if (attempt == 2)
                        {
                            throw new InvalidOperationException(
                                "The key could not be unlocked. Check its passphrase and key format, then retry."
                            );
                        }
                    }
                    finally
                    {
                        passphrase = null;
                    }
                }
            }

            if (ct.IsCancellationRequested)
            {
                key!.Dispose();
                ct.ThrowIfCancellationRequested();
            }

            lock (_sync)
            {
                if (generation != _generation)
                {
                    key!.Dispose();
                    throw new OperationCanceledException("SSH session was locked.");
                }

                _keys[path] = (hash, key!);
            }

            return key!;
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            _unlockGate.Release();
        }
    }

    public async Task<bool> VerifyHostAsync(
        string host,
        int port,
        string algorithm,
        byte[] publicKey,
        CancellationToken ct
    )
    {
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKey)).TrimEnd('=');
        var id = $"{host}:{port}:{algorithm}";
        string? previous;
        lock (_sync)
        {
            _hosts.TryGetValue(id, out previous);
            if (previous == fingerprint)
            {
                SaveKnownHost(host, port, publicKey);
                return true;
            }
        }

        if (
            RequestHostTrust is null
            || !await RequestHostTrust(new(host, port, algorithm, fingerprint, previous), ct).ConfigureAwait(false)
        )
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_trustFile is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_trustFile)!);
                var saved = new Dictionary<string, string>(_hosts, StringComparer.OrdinalIgnoreCase)
                {
                    [id] = fingerprint,
                };
                File.WriteAllText(_trustFile, JsonSerializer.Serialize(saved));
            }

            _hosts[id] = fingerprint;
            SaveKnownHost(host, port, publicKey);
        }

        return true;
    }

    private void SaveKnownHost(string host, int port, byte[] publicKey)
    {
        if (KnownHostsFile is not { } file) return;
        if (host.Any(char.IsWhiteSpace) || host.Any(char.IsControl))
            throw new InvalidOperationException("Invalid SSH host name.");
        if (publicKey.Length < 8) throw new InvalidOperationException("Invalid SSH host key.");
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(publicKey);
        if (length == 0 || length > publicKey.Length - 4) throw new InvalidOperationException("Invalid SSH host key type.");
        var type = System.Text.Encoding.ASCII.GetString(publicKey, 4, (int)length);
        var encoded = Convert.ToBase64String(publicKey);
        SshPublicKeyReader.Parse(type + " " + encoded);
        var name = port == 22 ? host : $"[{host}]:{port}";
        var prefix = name + " " + type + " ";
        var lines = File.Exists(file) ? File.ReadAllLines(file).Where(line => !line.StartsWith(prefix, StringComparison.Ordinal)).ToList() : [];
        lines.Add(prefix + encoded);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        try { File.WriteAllLines(temporary, lines); File.Move(temporary, file, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _generation++;
            foreach (var entry in _keys.Values)
            {
                entry.Key.Dispose();
            }

            _keys.Clear();
        }
    }

    public void Dispose() => Clear();
}
