# Security policy

kernctl is pre-release software. Its transaction engine is implemented, but no
production registry, service, power, process, network, or filesystem mutations are
registered.

## Reporting

Do not open a public issue for a suspected vulnerability. Contact the repository
owner privately through the security-reporting mechanism configured on GitHub.
Include reproduction steps, impact, and the affected revision. Do not include
passwords, authentication material, personal files, or other secrets.

## Security boundaries

- The desktop UI is an unelevated process.
- Privileged operations are isolated in a short-lived, separately elevated Windows
  broker with a versioned protocol and exact allowlist.
- System mutation is prohibited outside a versioned `ISystemAction` running through
  `IActionTransactionEngine`.
- The engine persists and validates an integrity-protected rollback snapshot before
  calling an action's apply stage.
- Action journals use explicit JSON contracts, size limits, deterministic generated
  filenames, atomic replacement, and no polymorphic type metadata.
- Snapshot fields resembling passwords, cookies, tokens, credentials, authorization
  data, or secret material are rejected.
- Logs must be structured and must not contain credentials, tokens, cookies, or file
  contents.
- Broker pipes reject remote clients, use a restrictive initiating-user DACL, accept
  one client, and verify the OS-reported client process before the handshake succeeds.
- Release broker sessions fail closed unless the side-by-side UI and broker
  executables have trusted matching Authenticode signers. Unsigned Debug builds are
  limited to the documented same-directory development policy and must not ship.
- Optimizations require explicit detection, planning, validation, verification, and
  an honest final transaction state. Verification is mandatory even for actions that
  cannot be reversed.

## Recovery boundary

Active journals live inside the current user's local application-data directory and
require no elevation. At startup kernctl scans them but never silently continues an
interrupted mutation. Compatible snapshots may be rolled back only through the same
registered action definition and schema version that created the plan. Missing,
incompatible, non-reversible, or malformed recovery data is surfaced for manual
review rather than guessed or executed.

Diagnostic logging is separate from primary history. User history stores sanitized
error summaries and never stack traces or private file contents.

The complete broker threat model, remaining same-user limitations, protocol limits,
and packaging policy are documented in
[docs/elevated-broker.md](docs/elevated-broker.md).
