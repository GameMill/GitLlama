using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GitLlama.Git;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class SshKeySessionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GitLlama-SSH-session-" + Guid.NewGuid());

    private string Key(string name = "test.ppk")
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        File.WriteAllText(path, CreateEncryptedTestKey("test-passphrase"));
        return path;
    }

    [Fact]
    public async Task UnlockCachesKeyAndLockRequiresPassphraseAgain()
    {
        using var session = new SshKeySession();
        var requests = 0;
        session.RequestPassphrase = (_, _) =>
        {
            requests++;
            return Task.FromResult<string?>("test-passphrase");
        };
        var path = Key();
        var first = await session.UnlockAsync(path, default);
        Assert.Same(first, await session.UnlockAsync(path, default));
        Assert.Equal(1, requests);
        session.Clear();
        Assert.NotSame(first, await session.UnlockAsync(path, default));
        Assert.Equal(2, requests);
        Assert.Single(Directory.GetFiles(root));
        Assert.Contains("Encryption: aes256-cbc", File.ReadAllText(path));
    }

    [Fact]
    public async Task WrongPassphraseRetriesAndCancelDoesNotCache()
    {
        using var session = new SshKeySession();
        var requests = new List<bool>();
        session.RequestPassphrase = (request, _) =>
        {
            requests.Add(request.Retry);
            return Task.FromResult<string?>(requests.Count == 1 ? "wrong" : "test-passphrase");
        };
        var path = Key();
        await session.UnlockAsync(path, default);
        Assert.Equal(new[] { false, true }, requests);
        session.Clear();
        session.RequestPassphrase = (_, _) => Task.FromResult<string?>(null);
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.UnlockAsync(path, default));
        session.RequestPassphrase = (_, _) => Task.FromResult<string?>("test-passphrase");
        await session.UnlockAsync(path, default);
    }

    [Fact]
    public async Task ChangedKeyRequiresNewUnlock()
    {
        using var session = new SshKeySession();
        var count = 0;
        session.RequestPassphrase = (_, _) =>
        {
            count++;
            return Task.FromResult<string?>("test-passphrase");
        };
        var path = Key();
        await session.UnlockAsync(path, default);
        Key();
        await session.UnlockAsync(path, default);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task HostTrustRejectsUnknownAndChangedKeysAndPersistsOnlyPublicFingerprint()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "hosts.json");
        var publicKey = new byte[4 + 11 + 4 + 32];
        BinaryPrimitives.WriteInt32BigEndian(publicKey, 11);
        Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(publicKey, 4);
        BinaryPrimitives.WriteInt32BigEndian(publicKey.AsSpan(15), 32);
        var changedKey = publicKey.ToArray();
        changedKey[^1] = 1;
        using var session = new SshKeySession(path);
        Assert.False(await session.VerifyHostAsync("host", 22, "ssh-ed25519", publicKey, default));
        session.RequestHostTrust = (request, _) =>
        {
            Assert.Null(request.PreviousFingerprint);
            return Task.FromResult(true);
        };
        Assert.True(await session.VerifyHostAsync("host", 22, "ssh-ed25519", publicKey, default));
        using var restored = new SshKeySession(path);
        Assert.True(await restored.VerifyHostAsync("host", 22, "ssh-ed25519", publicKey, default));
        restored.RequestHostTrust = (request, _) =>
        {
            Assert.NotNull(request.PreviousFingerprint);
            return Task.FromResult(false);
        };
        Assert.False(await restored.VerifyHostAsync("host", 22, "ssh-ed25519", changedKey, default));
        Assert.DoesNotContain("passphrase", File.ReadAllText(path));
        Assert.Contains("SHA256:", File.ReadAllText(path));
        Assert.Contains("host ssh-ed25519 " + Convert.ToBase64String(publicKey), File.ReadAllText(restored.KnownHostsFile!));
        Assert.DoesNotContain(Convert.ToBase64String(changedKey), File.ReadAllText(restored.KnownHostsFile!));
    }

    [Fact]
    public async Task CancellationDuringPromptLeavesKeyLocked()
    {
        using var session = new SshKeySession();
        using var stop = new CancellationTokenSource();
        session.RequestPassphrase = async (_, ct) =>
        {
            stop.Cancel();
            await Task.Delay(1, ct);
            return "test-passphrase";
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.UnlockAsync(Key(), stop.Token));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EncryptedPuttyVersionsUnlockWithoutExternalAgent(int version)
    {
        using var session = new SshKeySession();
        var path = Key();
        File.WriteAllText(path, CreateEncryptedTestKey("test-passphrase", version));
        session.RequestPassphrase = (_, _) => Task.FromResult<string?>("test-passphrase");
        var key = await session.UnlockAsync(path, default);
        Assert.Contains(key.HostKeyAlgorithms, algorithm => algorithm.Name == "rsa-sha2-512");
    }

    [Fact]
    public void BridgeParsesPortAndRejectsUnexpectedDestination()
    {
        var request = ManagedSshBridge.ParseArguments([
            "-o",
            "SendEnv=GIT_PROTOCOL",
            "-p",
            "2222",
            "git@host",
            "git-upload-pack 'repo'",
        ]);
        Assert.Equal(2222, request.Port);
        ManagedSshBridge.ValidateRequest(request, ManagedSshBridge.Endpoint("ssh://git@host:2222/repo"));
        Assert.Throws<InvalidOperationException>(() =>
            ManagedSshBridge.ValidateRequest(request, ManagedSshBridge.Endpoint("git@other:repo"))
        );
        Assert.Throws<InvalidOperationException>(() =>
            ManagedSshBridge.ValidateRequest(request with { Command = "sh -c unsafe" }, request)
        );
    }

    // Generated disposable RSA key; this fixture is never registered with a hosting service.
    public static string CreateEncryptedTestKey(string passphrase, int version = 2)
    {
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportParameters(true);
        static byte[] Blob(params byte[][] fields)
        {
            using var stream = new MemoryStream();
            foreach (var field in fields)
            {
                var size = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(size, field.Length);
                stream.Write(size);
                stream.Write(field);
            }

            return stream.ToArray();
        }

        static byte[] Mp(byte[] bytes) =>
            (bytes[0] & 0x80) != 0
                ? new byte[] { 0 }
                    .Concat(bytes)
                    .ToArray()
                : bytes;
        var publicBlob = Blob("ssh-rsa"u8.ToArray(), Mp(key.Exponent!), Mp(key.Modulus!));
        var raw = Blob(Mp(key.D!), Mp(key.P!), Mp(key.Q!), Mp(key.InverseQ!));
        var padded = new byte[(raw.Length + 15) / 16 * 16];
        raw.CopyTo(padded, 0);
        var pass = Encoding.UTF8.GetBytes(passphrase);
        var encryptionKey = SHA1.HashData(
                new byte[4]
                    .Concat(pass)
                    .ToArray()
            )
            .Concat(
                SHA1.HashData(
                    new byte[] { 0, 0, 0, 1 }
                        .Concat(pass)
                        .ToArray()
                )
            )
            .Take(32)
            .ToArray();
        var macKey = SHA1.HashData("putty-private-key-file-mac-key"u8.ToArray().Concat(pass).ToArray());
        var iv = new byte[16];
        var argonHeaders = "";
        if (version == 3)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var generator = new Org.BouncyCastle.Crypto.Generators.Argon2BytesGenerator();
            generator.Init(
                new Org.BouncyCastle.Crypto.Parameters.Argon2Parameters.Builder(
                    Org.BouncyCastle.Crypto.Parameters.Argon2Parameters.Argon2id
                )
                    .WithSalt(salt)
                    .WithMemoryAsKB(8192)
                    .WithIterations(2)
                    .WithParallelism(1)
                    .Build()
            );
            var derived = new byte[80];
            generator.GenerateBytes(pass, derived);
            encryptionKey = derived[..32];
            iv = derived[32..48];
            macKey = derived[48..];
            argonHeaders =
                "\nKey-Derivation: Argon2id\nArgon2-Memory: 8192\nArgon2-Passes: 2\nArgon2-Parallelism: 1\nArgon2-Salt: "
                + Convert.ToHexString(salt).ToLowerInvariant();
        }

        var macData = Blob(
            "ssh-rsa"u8.ToArray(),
            "aes256-cbc"u8.ToArray(),
            "gitllama-disposable-test"u8.ToArray(),
            publicBlob,
            padded
        );
        var mac = version == 3 ? HMACSHA256.HashData(macKey, macData) : HMACSHA1.HashData(macKey, macData);
        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.IV = iv;
        aes.Padding = PaddingMode.None;
        var encrypted = aes.EncryptCbc(padded, aes.IV, PaddingMode.None);
        static string Lines(byte[] blob)
        {
            var encoded = Convert.ToBase64String(blob);
            var lines = Enumerable
                .Range(0, (encoded.Length + 63) / 64)
                .Select(index => encoded.Substring(index * 64, Math.Min(64, encoded.Length - index * 64)))
                .ToArray();
            return lines.Length + "\n" + string.Join("\n", lines);
        }

        return $"PuTTY-User-Key-File-{version}: ssh-rsa\nEncryption: aes256-cbc\nComment: gitllama-disposable-test\nPublic-Lines: "
            + Lines(publicBlob)
            + argonHeaders
            + "\nPrivate-Lines: "
            + Lines(encrypted)
            + "\nPrivate-MAC: "
            + Convert.ToHexString(mac).ToLowerInvariant()
            + "\n";
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
