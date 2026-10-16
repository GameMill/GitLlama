using System.Security.Cryptography;
using System.Text;

namespace GitLlama.Git;

public sealed record SshPublicKey(string Text, string Type, string Fingerprint);

public static class SshPublicKeyReader
{
    public static async Task<SshPublicKey> ReadAsync(string path, CancellationToken ct = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 32768)
        {
            throw new InvalidOperationException("Choose an OpenSSH or PuTTY SSH-2 public key file (.pub).");
        }

        return Parse(await File.ReadAllTextAsync(path, ct));
    }

    public static SshPublicKey Parse(string text)
    {
        var value = text.Trim();
        if (
            value.Contains("PRIVATE KEY", StringComparison.Ordinal)
            || value.StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                "This is a private key. Choose its public .pub file; private key contents are never displayed or copied."
            );
        }

        if (value.StartsWith("---- BEGIN SSH2 PUBLIC KEY ----", StringComparison.Ordinal))
        {
            return ParseSsh2(value);
        }

        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (
            parts.Length < 2
            || value.Contains('\n')
            || value.Contains('\r')
            || !(
                parts[0].StartsWith("ssh-", StringComparison.Ordinal)
                || parts[0].StartsWith("ecdsa-", StringComparison.Ordinal)
                || parts[0].StartsWith("sk-", StringComparison.Ordinal)
            )
        )
        {
            throw new InvalidOperationException("Choose an OpenSSH or PuTTY SSH-2 public key file (.pub).");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The public key contains invalid Base64 data.");
        }

        if (bytes.Length < 8)
        {
            throw new InvalidOperationException("The public key is incomplete.");
        }

        var typeLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (typeLength > bytes.Length - 4 || Encoding.ASCII.GetString(bytes, 4, (int)typeLength) != parts[0])
        {
            throw new InvalidOperationException("The public key type does not match its data.");
        }

        return new(value, parts[0], "SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('='));
    }

    private static SshPublicKey ParseSsh2(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines[^1].Trim() != "---- END SSH2 PUBLIC KEY ----")
        {
            throw new InvalidOperationException("The SSH-2 public key is incomplete: its end marker is missing.");
        }

        var data = new StringBuilder();
        var continuingHeader = false;
        for (var index = 1; index < lines.Length - 1; index++)
        {
            var line = lines[index].Trim();
            // RFC 4716 headers (including PuTTY's Comment) can span several lines.
            if (data.Length == 0 && (continuingHeader || line.Contains(':')))
            {
                continuingHeader = line.EndsWith('\\');
                continue;
            }

            data.Append(line);
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data.ToString());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The public key contains invalid Base64 data.");
        }

        if (continuingHeader || bytes.Length < 8)
        {
            throw new InvalidOperationException("The public key is incomplete.");
        }

        var typeLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (typeLength == 0 || typeLength > bytes.Length - 4)
        {
            throw new InvalidOperationException("The public key type is invalid.");
        }

        var type = Encoding.ASCII.GetString(bytes, 4, (int)typeLength);
        // Normalize public data for copying into hosting providers; never include private material.
        return Parse(type + " " + Convert.ToBase64String(bytes));
    }
}
