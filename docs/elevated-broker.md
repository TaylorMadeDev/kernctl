# Restricted elevated Windows broker

## Purpose and invariant

The Avalonia application stays unelevated. A confirmed transaction that contains an
administrator-required action asks `IActionPrivilegeBroker` for a short-lived session
before snapshot capture or apply:

```text
Kernctl.App (standard user)
    -> IActionTransactionEngine
    -> Kernctl.Broker.Client
    -> Windows runas / UAC
    -> Kernctl.Broker (elevated)
    -> exact diagnostic allowlist
```

This milestone proves the elevation boundary and contains no production system
mutation. The only production operation IDs are:

- `broker.get-info`
- `broker.get-capabilities`
- `broker.ping`
- `broker.shutdown`

The client API exposes only typed methods for those diagnostics. The broker registry
uses exact ordinal identifiers and explicit construction; it does not reflect over
methods, load plugins, accept scripts, or provide a command-execution fallback.

## Transport and protocol

Each session uses a 256-bit unpredictable named-pipe suffix and a separate GUID
session ID. The broker creates one pipe instance with:

- a protected DACL granting the initiating user read/write access and retaining
  administrator/System access;
- `FILE_FLAG_FIRST_PIPE_INSTANCE`;
- `PIPE_REJECT_REMOTE_CLIENTS`;
- asynchronous byte mode;
- one accepted client;
- a 20-second connection timeout and 15-second idle timeout;
- at most eight requests.

The pipe name is not treated as an authentication secret. The DACL, Windows-reported
client identity, first-instance behavior, and narrowly bounded allowlist are the
security controls. Microsoft documents that named-pipe security descriptors control
access to both ends and warns that default descriptors grant broader read access;
it also recommends a logon SID when a terminal-services session boundary is needed.
See [Named Pipe Security and Access Rights](https://learn.microsoft.com/windows/win32/ipc/named-pipe-security-and-access-rights).

Messages are four-byte big-endian length-prefixed UTF-8 JSON. Frames are capped at
64 KB before allocation. The source-generated serializer has an explicit DTO set,
camel-case property names, strict unmapped-property rejection, no polymorphic type
metadata, and no `BinaryFormatter`.

Every operation request carries protocol, session, request, operation, and payload
versions plus UTC creation/expiry timestamps. Request IDs cannot be reused within a
session. Unknown operation IDs are rejected before payload validation or execution.
Each operation has a payload validator, risk classification, mutation flag, maximum
duration, and safe audit description.

## Client verification

The broker does not trust the PID or path in JSON alone. After the pipe connects it
uses the pipe handle to obtain the actual client PID and Windows session ID. Microsoft
documents these APIs at
[GetNamedPipeClientProcessId](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)
and
[GetNamedPipeClientSessionId](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getnamedpipeclientsessionid).

The broker then opens that process with query-only access and verifies:

1. actual PID from the pipe;
2. exact process creation time, protecting against PID reuse;
3. Windows session ID;
4. full process image path;
5. user SID from the process access token;
6. SHA-256 executable identity captured immediately before launch;
7. the `Kernctl.App.exe` / `Kernctl.Broker.exe` side-by-side layout;
8. handshake values against both the OS evidence and launch envelope.

Windows access tokens are the OS security context and contain the process user's SID;
see [Access Tokens](https://learn.microsoft.com/windows/win32/secauthz/access-tokens).
Process creation time comes from `GetProcessTimes`, which supports limited-information
process handles.

Release builds fail closed unless both executables pass Windows Authenticode trust
verification and have the same signer thumbprint. `WinVerifyTrust` performs the
Windows Authenticode policy check. Debug builds have one documented exception: two
unsigned executables are accepted only when the real client is named
`Kernctl.App.exe`, the real broker is named `Kernctl.Broker.exe`, both resolve to the
same directory, and all PID/session/start-time/SID/hash checks pass. A partially
signed or mismatched pair is rejected even in Debug.

The parent process is not enforced. `runas` can involve the Windows shell, Application
Information service, and over-the-shoulder administrator credentials, so a stable
direct parent relationship is not reliable. PID, creation time, pipe-reported
identity, session, token SID, resolved layout, hash, and Release signatures form the
documented identity policy instead.

## UAC and launch

The client resolves exactly
`AppContext.BaseDirectory\Kernctl.Broker.exe`; it never searches the working
directory or `PATH`. It launches that absolute path with the Windows `runas` verb.
Microsoft documents that `runas` launches as administrator and displays UAC consent
or credential UI; see
[ShellExecute verbs](https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shellexecutea).

Launch arguments contain only bounded non-secret routing and identity data. No
password, bearer token, cookie, or authentication secret is placed on the command
line. UAC cancellation (Windows error 1223) maps to a normal cancelled result. If the
app closes during consent, it does not attempt to kill an elevated process it does
not own; a successfully launched broker still exits on connection/idle timeout.

The app build copies the broker executable, DLL, dependency manifest, and runtime
configuration beside `Kernctl.App.exe`. The app publish target publishes the broker
into the same destination. Any other layout fails closed as unsupported.

## Transaction integration

Planning reports `ActionPrivilegeLevel.Administrator` before confirmation. Calling
`ExecuteAsync` represents explicit confirmation. The engine:

1. validates every plan again;
2. journals a safe elevation-request record;
3. reports preparing, awaiting-consent, connecting, verifying, and ready/declined
   states;
4. opens the restricted broker;
5. only then enters snapshot and apply;
6. keeps the session through verification and automatic rollback;
7. disposes it deterministically.

Dry runs and standard-user transactions do not call the privilege broker. Consent
decline or broker failure transitions to a safe pre-mutation failure. Explicit
administrator rollback and crash recovery reacquire a restricted session. Journals
store only requested/granted/declined/failed outcome text, never pipe names,
handshake material, command lines, or process paths.

## Audit events

`kernctl-Broker` emits structured ETW `EventSource` events for process start/stop,
client verification, handshake completion, request rejection, and operation outcome.
It never writes request payloads, handshake fields, executable paths, SIDs, hashes,
tokens, or command lines. ETW avoids creating an event-log source or mutating the
registry during this diagnostic milestone.

## Threat model and remaining limitations

Protected assets are administrator authority, broker operation inputs, transaction
integrity, and private process/session data. Relevant attackers include a remote pipe
client, another local user/session, a same-user process racing the endpoint, malformed
or replayed protocol input, a replaced executable, and a caller attempting to turn a
diagnostic into arbitrary execution.

The design rejects remote clients in the kernel pipe mode, limits the DACL to the
initiating SID plus administrative principals, verifies the actual pipe client,
detects PID reuse, uses a single first pipe instance, binds all requests to one
expiring session, rejects request replay, bounds all input, and exposes no mutating or
arbitrary operation.

It does not claim to protect a user who has already approved a malicious elevated
binary, a machine with a compromised administrator/kernel, or a process already able
to inject code into the legitimate kernctl UI. A malicious process already running
as the same user can observe non-secret launch metadata and can trigger its own UAC
prompt, but it cannot silently gain elevation through this broker: it must pass the
OS/process/layout/signature policy and can invoke only the fixed diagnostic allowlist.
Unsigned Debug output is intentionally weaker and must not be distributed.

Over-the-shoulder elevation can run the broker under a different administrator
account. The pipe DACL still grants the initiating user's SID, and the broker validates
that SID on the actual client process rather than requiring both processes to share
an account.

## Verification

Automated tests use only in-memory action state and diagnostic IPC. They cover strict
serialization, framing limits, incompatible/expired sessions, unsafe and unknown
operation IDs, exact production capabilities, UAC cancellation, transaction
orchestration, rollback elevation, safe journaling, a real Windows secured named-pipe
round trip, and rejection by the production identity verifier outside the trusted
application layout. Tests never modify registry, services, power configuration,
network state, or user files.
