# HTTPS and SSH connection investigation

Investigated on 2026-10-16. The affected user is on Windows 11. The available test machine runs Windows 10 build 19045, .NET 8.0.30.

## Findings

The screenshots describe separate failures. Neither screenshot establishes that the user's account credentials are wrong.

### HTTPS

GitLlama uses LibGit2Sharp 0.31.0 with native libgit2 commit `3f4182d15eab74a302718f2de454ffadb1995626`. On Windows this path uses libgit2's Schannel stream implementation. Git Credential Manager supplies credentials; it does not perform the subsequent Git transfer.

The exact `could not decrypt tls message` error was reproduced with an entirely in-process, disposable TLS 1.2 server:

| Scenario | Observed result |
| --- | --- |
| Public GitHub reference discovery, three runs | Success |
| Local server closes TLS cleanly before an HTTP response | Exact reported TLS error |
| Local server challenges for Basic authentication; credential callback waits 800 ms; connection stays open | Success; credentials supplied and an authenticated request received |
| Same authentication delay; server closes its idle connection | Socket-aborted error |
| Server receives the authenticated request, then closes TLS cleanly before sending an HTTP response | Exact reported TLS error |

The native reader checks `SEC_E_CONTEXT_EXPIRED` but not the informational `SEC_I_CONTEXT_EXPIRED` status documented for a peer's orderly TLS shutdown. It also lacks handling for `SEC_I_RENEGOTIATE`. Other statuses fall into the same generic error without preserving the status code. This explains why the message can be misleading: the controlled reproduction needs neither invalid credentials nor damaged encrypted data.

The isolated harness was also run with LibGit2Sharp 0.32.0/native commit `5853918`. The same controlled failures remain. A package upgrade alone is not a demonstrated fix.

The friend's actual reason for losing the connection remains unconfirmed. A server, intermediary, or local network/security software could be involved. The Windows 10 reproduction does not test their Windows 11 machine or prove a TLS 1.3 issue. A login pause alone was insufficient to trigger the exact error in the controlled test.

### SSH

The OpenSSH compatibility path explicitly sets `BatchMode=yes` and `StrictHostKeyChecking=yes`. It therefore cannot ask a new user to trust an unfamiliar host. That path does not invoke `SshKeySession.RequestHostTrust`, so the app reports host verification failure instead of showing its existing fingerprint confirmation UI.

The managed PuTTY-key path uses SSH.NET and already invokes `SshKeySession.VerifyHostAsync`. Its trust store is separate from OpenSSH's `known_hosts`, so trusting a host in that path does not establish trust for OpenSSH. The screenshot's OpenSSH error is consistent with this gap, but does not establish whether the host was unknown or its saved key had changed.

## Initial in-process HTTPS proof of concept

LibGit2Sharp exposes `RpcSmartSubtransport`, allowing GitLlama to retain libgit2's Git protocol, pack handling, object database, and checkout while replacing only the HTTP/TLS layer.

An isolated HTTPS subtransport using .NET `HttpClient` successfully:

- Listed all 9 advertised references from the public `libgit2/TestGitRepository` fixture.
- Cloned and checked out commit `49322bb17d3acc9146f98c97d078513228bbf3c0`.
- Completed those operations through 3 HTTPS requests without launching a Git subprocess.

The probe uses normal .NET certificate validation for public HTTPS requests. Self-signed certificate acceptance is confined to the separate disposable loopback fixture and is not an application change.

This proves architectural feasibility, not resolution of the friend's specific environment. The probe intentionally has no authenticated HTTPS support and limits buffered request bodies to 1 MB. It is not a production transport or a performance benchmark.

## Implementation

The application now registers per-operation HTTPS and SSH subtransports through `GitTransportScope`. Clone, fetch, fast-forward pull, branch push, and tag push/delete retain libgit2 for Git negotiation, packs, object storage, and checkout.

`ManagedHttpsTransport` uses .NET `HttpClient` with normal certificate validation and proxy support. Uploads use a bounded producer/consumer stream instead of buffering complete packs. Authentication challenges release their response before waiting for Git Credential Manager; the authenticated request can establish a fresh connection. OAuth/PAT headers remain scoped to the approved host or Azure organization. Cross-host redirects, HTTPS downgrades, and redirects into unauthorized Azure organizations are refused. Push requests are not automatically replayed after failures. Cancellation and original transport errors reach the UI instead of being lost at the native callback boundary.

`ManagedSshTransport` uses SSH.NET for explicit private keys and the existing in-app unlock and fingerprint prompts. Agent mode retains an OpenSSH/PuTTY connection process for compatibility, while Git processing remains in libgit2. Unknown OpenSSH hosts trigger the in-app trust prompt; accepted public keys are written to an app-managed known_hosts file. Existing changed-host warnings are not bypassed. PuTTY can use explicitly approved host fingerprints. Git Credential Manager and Git LFS remain external components where already configured; this change does not replace them.

Verification includes private HTTPS clone/fetch, a 4 MB incompressible push and reachable tags, cancellation, safe interruption errors, credential scoping, redirect rejection, and actual TLS 1.2 connections with a browser delay and orderly server shutdown. The real TLS fixture also confirms an untrusted certificate fails before credentials are requested. Disposable SSH acceptance covers clone, a 2 MB push, fetch, tags, and first-connection OpenSSH host confirmation. The SSH test server alone uses Git's command-line backend as an independent server implementation.

The friend's Windows 11 environment still needs a retry with the updated build. These checks reproduce and fix the identified transport and trust gaps; they do not establish what caused that machine's connection to close.

## Reproduction assets

The disposable harness and upstream source snapshot are in the ignored `artifacts/network-investigation` folder:

```powershell
dotnet run --project artifacts/network-investigation/NetworkInvestigation.csproj -p:ProbeLibGitVersion=0.31.0
dotnet run --project artifacts/network-investigation/NetworkInvestigation.csproj -p:ProbeLibGitVersion=0.32.0
```

## Primary references

- [Bundled libgit2 Schannel source](https://github.com/libgit2/libgit2/blob/3f4182d15eab74a302718f2de454ffadb1995626/src/libgit2/streams/schannel.c)
- [Microsoft: Schannel DecryptMessage statuses and post-handshake handling](https://learn.microsoft.com/en-us/windows/win32/secauthn/decryptmessage--schannel)
- [LibGit2Sharp smart subtransport implementation and tests](https://github.com/libgit2/libgit2sharp/blob/ee53bdd8a9f5cfecfc8750e28a1f5c0d34690ce0/LibGit2Sharp.Tests/desktop/SmartSubtransportFixture.cs)
