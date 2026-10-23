using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitLlama.Infrastructure;

public sealed class GithubStoredConnection
{
    public string ClientId { get; init; } = "";
    public string Login { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? RefreshExpiresAt { get; init; }
}

public sealed class ForgejoStoredConnection
{
    public string BaseUrl { get; init; } = "";
    public string Login { get; init; } = "";
    public string AccessToken { get; init; } = "";
}

/// <summary>Per-profile credentials protected with Windows DPAPI for the current OS user.</summary>
public sealed class ProtectedIntegrationStore(string settingsDirectory)
{
    public bool IsSupported => OperatingSystem.IsWindows();

    private string FilePath(string profileId, string provider = "github")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (provider is not ("github" or "azure" or "forgejo"))
        {
            throw new ArgumentException("Unknown integration.");
        }

        return Path.Combine(
            settingsDirectory,
            "connections",
            provider + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId))) + ".dat"
        );
    }

    public async Task SaveAsync(string profileId, GithubStoredConnection connection, CancellationToken ct = default)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Secure integration storage is available on Windows.");
        }

        var clear = JsonSerializer.SerializeToUtf8Bytes(connection);
        try
        {
            await SavePayloadAsync(profileId, "github", clear, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public async Task SavePayloadAsync(string profileId, string provider, byte[] clear, CancellationToken ct = default)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Secure integration storage is available on Windows.");
        }

        var encrypted = Protect(clear, false);
        var file = FilePath(profileId, provider);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, file, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<GithubStoredConnection?> LoadAsync(string profileId, CancellationToken ct = default)
    {
        var clear = await LoadPayloadAsync(profileId, "github", ct);
        if (clear is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GithubStoredConnection>(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public async Task<byte[]?> LoadPayloadAsync(string profileId, string provider, CancellationToken ct = default)
    {
        var file = FilePath(profileId, provider);
        if (!IsSupported || !File.Exists(file))
        {
            return null;
        }

        return Protect(await File.ReadAllBytesAsync(file, ct), true);
    }

    public Task DeleteAsync(string profileId, string provider = "github")
    {
        var file = FilePath(profileId, provider);
        if (File.Exists(file))
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref Blob input,
        IntPtr description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out Blob output
    );

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref Blob input,
        IntPtr description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out Blob output
    );

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Protect(byte[] bytes, bool decrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = decrypt
                ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success)
            {
                throw new CryptographicException(
                    "The saved integration could not be protected or unlocked for this Windows user."
                );
            }

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++)
            {
                Marshal.WriteByte(input.Data, i, 0);
            }

            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                if (decrypt)
                {
                    for (var i = 0; i < output.Length; i++)
                    {
                        Marshal.WriteByte(output.Data, i, 0);
                    }
                }

                LocalFree(output.Data);
            }
        }
    }
}
