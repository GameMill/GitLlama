using System.Buffers.Binary;
using System.Text;
using GitLlama.Git;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class SshPublicKeyReaderTests
{
    [Theory]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-rsa")]
    [InlineData("ecdsa-sha2-nistp256")]
    public void PuttyPublicKeysNormalizeToOpenSshWithTheSameFingerprint(string type)
    {
        var bytes = new byte[4 + type.Length + 36];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)type.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        var base64 = Convert.ToBase64String(bytes);
        var expected = SshPublicKeyReader.Parse(type + " " + base64);
        var wrapped = string.Join("\r\n", base64.Chunk(32).Select(chars => new string(chars)));
        var putty = "---- BEGIN SSH2 PUBLIC KEY ----\r\n"
            + "Comment: \"exported from PuTTY \\\r\nwith a continued comment\"\r\n"
            + wrapped + "\r\n---- END SSH2 PUBLIC KEY ----\r\n";

        var actual = SshPublicKeyReader.Parse(putty);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("Comment", actual.Text);
    }

    [Theory]
    [InlineData("---- BEGIN SSH2 PUBLIC KEY ----\nAAAA\n")]
    [InlineData("---- BEGIN SSH2 PUBLIC KEY ----\ninvalid!\n---- END SSH2 PUBLIC KEY ----")]
    [InlineData("---- BEGIN SSH2 PUBLIC KEY ----\nAAAA\n---- END SSH2 PUBLIC KEY ----")]
    [InlineData("---- BEGIN SSH2 PUBLIC KEY ----\n/////wAAAAA=\n---- END SSH2 PUBLIC KEY ----")]
    [InlineData("---- BEGIN SSH2 PUBLIC KEY ----\nAAAA\n---- END SSH2 PUBLIC KEY ----\ntrailing data")]
    public void MalformedPuttyPublicKeysAreRejected(string text) =>
        Assert.Throws<InvalidOperationException>(() => SshPublicKeyReader.Parse(text));

    [Theory]
    [InlineData("PuTTY-User-Key-File-2: ssh-rsa\nPrivate-Lines: 1\nsecret")]
    [InlineData("PuTTY-User-Key-File-3: ssh-ed25519\nPrivate-Lines: 1\nsecret")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nsecret")]
    public void PrivateKeysAreNeverLoadedAsPublicKeys(string text)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SshPublicKeyReader.Parse(text));
        Assert.Contains("private key", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }
}
