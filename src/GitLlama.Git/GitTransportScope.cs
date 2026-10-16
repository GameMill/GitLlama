using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>Per-operation credentials and cancellation for libgit2's in-process transports.</summary>
public sealed class GitTransportScope : IDisposable
{
    private static readonly AsyncLocal<GitTransportScope?> Active = new();
    private static readonly Lazy<SmartSubtransportRegistration<ManagedHttpsTransport>> Registration =
        new(() => GlobalSettings.RegisterSmartSubtransport<ManagedHttpsTransport>("https"));
    private static readonly Lazy<SmartSubtransportRegistration<ManagedSshTransport>> SshRegistration =
        new(() => GlobalSettings.RegisterSmartSubtransport<ManagedSshTransport>("ssh"));
    private readonly GitTransportScope? previous;
    private Exception? failure;
    internal GitNetworkOptions Options { get; }
    internal CancellationToken Cancellation { get; }
    internal static GitTransportScope? Current => Active.Value;

    private GitTransportScope(GitNetworkOptions? options, CancellationToken ct)
    {
        _ = Registration.Value;
        _ = SshRegistration.Value;
        previous = Active.Value;
        Options = options ?? new GitNetworkOptions();
        Cancellation = ct;
        Active.Value = this;
    }

    public static GitTransportScope Enter(GitNetworkOptions? options, CancellationToken ct = default) => new(options, ct);
    internal void Fail(Exception error) => Interlocked.CompareExchange(ref failure, error, null);
    public void Dispose()
    {
        Active.Value = previous;
        Cancellation.ThrowIfCancellationRequested();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
