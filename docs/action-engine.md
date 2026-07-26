# Action engine and rollback safety

## Purpose and boundary

Every future kernctl optimization must run through the platform-independent action
engine in `Kernctl.Core.Actions`. The engine controls lifecycle, persistence,
cancellation, verification, rollback, history, and recovery. An action owns only its
narrow detection and platform operation.

This milestone registers no production system actions. Test actions change only
in-memory state or isolated temporary directories. No registry, service, power-plan,
process, network, or user-file mutation was added.

## Required lifecycle

```text
Detect
  ↓
Plan and explain
  ↓
Validate every action in the group
  ↓
Capture original state
  ↓
Validate, integrity-protect, and atomically persist snapshot
  ↓
Apply one action
  ↓
Verify observed state
  ↓
Apply next action or commit
  ↓ on any failure/cancellation after mutation
Roll back in reverse order
```

No action may claim success merely because `ApplyAsync` returned. A transaction
commits only after every applicable action has passed `VerifyAsync`.

## Action contract

`ISystemAction` exposes one stable `ActionDescriptor` and seven asynchronous stages:

1. `DetectAsync` reads current state and availability.
2. `PlanAsync` creates the immutable user-facing proposal.
3. `ValidateAsync` checks preconditions without mutation.
4. `CaptureStateAsync` returns an explicit versioned JSON payload.
5. `ApplyAsync` performs one narrow operation.
6. `VerifyAsync` independently observes the desired result.
7. `RollbackAsync` restores from the persisted `ActionStateSnapshot`.

Descriptors include a stable action ID and schema version, display text, category,
`Low`/`Moderate`/`High` risk, `StandardUser`/`Administrator` privilege, restart
requirement, supported platforms, rollback availability, runtime availability, and
an optional duration estimate.

Action IDs are durable data identifiers. Never rename or reuse one for a different
operation. Increment the action schema when plan or rollback compatibility changes.
The engine rejects a plan or snapshot whose ID/version no longer matches the
registered action.

## Immutable plans

An `ActionPlan` records:

- action ID and schema version;
- detected current and requested desired state;
- named proposed operations;
- affected resource identifiers;
- risk, privilege, restart, and rollback metadata;
- warnings and unavailability reasons;
- a user-facing explanation.

Collections use immutable arrays. The engine compares the supplied plan with its
persisted journal representation immediately before mutation and re-runs validation
for every action. Editing or replacing a plan after review is not supported.

## State machines

Transaction states:

```text
Created → Planning → Planned → AwaitingConfirmation
                                      ├─→ Committed (dry run)
                                      └─→ Applying ⇄ Verifying → Committed
                                               ├─→ CancellationRequested
                                               └─→ RollingBack
CancellationRequested → RollingBack
RecoveryRequired      → RollingBack
RollingBack           → RolledBack | PartiallyRolledBack | RecoveryRequired
```

`Failed` is used when no successful result can be claimed and no mutation requires
rollback. `RecoveryRequired` identifies interrupted or unsafe-to-continue mutation.
Terminal history states are `Committed`, `RolledBack`, `PartiallyRolledBack`, and
`Failed`. A committed reversible transaction may later enter an explicit rollback.

Individual action states:

```text
Pending → Detected → Planned → Validated → SnapshotPersisted
                                              ↓
Applying → Applied → Verified
    └──────────────→ Failed
Applied | Verified | possibly-mutated Failed → RollingBack
RollingBack → RolledBack | RollbackFailed
```

Unavailable actions may become `Skipped` before mutation. Transition guards reject
every undeclared edge with `InvalidStateTransitionException`; critical state is never
inferred from log text.

## Ordered execution and rollback

The engine permits only one mutating transaction in the process at a time. It applies
actions sequentially for deterministic ordering. Before each apply:

1. capture the action payload;
2. reject missing, oversized, sensitive, or malformed content;
3. add transaction/action ownership and UTC timestamp;
4. compute SHA-256 over the JSON payload;
5. atomically persist `SnapshotPersisted`;
6. persist the `Applying` transition;
7. call `ApplyAsync`.

If apply reports possible partial mutation, verification fails, or cancellation is
observed after apply begins, that action participates in rollback. Rollback candidates
run in reverse action order. Failure to roll back one action is recorded, but earlier
rollback attempts continue. A non-reversible action that already changed state makes
the final result `PartiallyRolledBack`.

## Cancellation

Cancellation is cooperative:

- before mutation, execution stops and archives a safe failed/cancelled result;
- during capture, no apply has occurred and already-applied earlier actions roll back;
- during apply or verification, the engine conservatively assumes mutation and
  attempts rollback;
- after apply returns, the engine persists the action result before observing the
  next cancellation boundary;
- rollback uses an independent internal token so cancellation of the original
  operation cannot cancel recovery.

`RequestCancellation(transactionId)` signals the active action. An action that wraps
an indivisible platform operation should finish that atomic operation, return, and
allow the engine to journal and roll back.

## Dry runs

Dry run executes detection, planning, and validation only. It reports risk,
privilege, restart, resources, warnings, and availability. It does not call snapshot
capture, apply, verify, rollback, or a mutating platform service. Dry runs are marked
in journal history as simulations.

Tests assert that dry-run operation logs contain no capture or mutation stage.

## Journal layout and format

Production root:

```text
%LocalAppData%/kernctl/transactions/
├── active/
│   └── 0123456789abcdef0123456789abcdef.json
└── archive/
    └── 0123456789abcdef0123456789abcdef.json
```

The application generates transaction IDs. No user input becomes a path segment.
The current user's local application-data ACL provides the normal per-user boundary;
no administrator privileges are requested.

Top-level journal schema version is `1`:

```json
{
  "journalSchemaVersion": 1,
  "transactionId": "00000000-0000-0000-0000-000000000000",
  "state": "applying",
  "isDryRun": false,
  "startedAtUtc": "2026-07-26T10:00:00+00:00",
  "updatedAtUtc": "2026-07-26T10:00:01+00:00",
  "completedAtUtc": null,
  "restartRequirement": "none",
  "rollbackAttempted": false,
  "actions": [
    {
      "order": 0,
      "actionId": "stable.action-id",
      "displayName": "Example action",
      "actionSchemaVersion": 1,
      "state": "snapshotPersisted",
      "plan": {},
      "snapshot": {
        "snapshotSchemaVersion": 1,
        "transactionId": "00000000-0000-0000-0000-000000000000",
        "actionId": "stable.action-id",
        "actionSchemaVersion": 1,
        "capturedAtUtc": "2026-07-26T10:00:01+00:00",
        "originalState": {},
        "integrity": {
          "algorithm": "SHA-256",
          "digest": "<hex digest>",
          "payloadBytes": 2
        }
      }
    }
  ],
  "errors": []
}
```

Plans and payloads are normal JSON data. The serializer does not enable arbitrary
object or polymorphic type metadata. Unknown safe JSON fields are tolerated; an
unsupported required schema version is rejected clearly.

Snapshot payloads are limited to 256 KB and full journals to 1 MB. Field names
resembling passwords, tokens, cookies, secrets, credentials, authorization data,
`$type`, or `$values` are rejected recursively. Actions must avoid collecting such
data in the first place.

Each write creates a uniquely named temporary sibling and atomically replaces the
canonical generated filename. A crash therefore leaves the previous valid journal
or the new valid journal, never a half-written canonical JSON file. Completed records
move to `archive`; default retention is the latest 100 transactions.

## Crash recovery

At startup `ActionRecoveryViewModel` asks the engine to scan active journals. The
scanner validates schema, action ordering, plan ownership, snapshot ownership,
payload size, prohibited fields, and SHA-256 integrity.

Recovery information identifies:

- recorded transaction state and UTC start time;
- ordered action names;
- applied and verified actions;
- available snapshots;
- whether all possibly mutated actions can roll back;
- administrator metadata;
- possible manual intervention.

kernctl never resumes apply. A compatible recovery request transitions the journal
to `RecoveryRequired`/`RollingBack` and calls rollback in reverse order. An
interruption before mutation is archived as a safe failed transaction. Missing
actions, incompatible schemas, invalid integrity, non-reversible changes, or malformed
journals are surfaced for manual review.

## History and logging

`IActionHistoryService` reads archived journals and returns only transaction ID,
times, action names, final typed state, dry-run/rollback flags, restart requirement,
and distinct safe error summaries. It omits technical diagnostics, stack traces, and
snapshot payloads.

`ActionTransactionEngine` uses `Microsoft.Extensions.Logging` with structured
transaction/action IDs for unexpected failures. Journals and user history are not a
second diagnostic logging system. Default history retention is 100 entries; future
diagnostic-log retention must be configured separately.

## UI foundations

- `ActionReviewDialog` presents description, resources, risk text, privilege,
  restart, rollback, warnings, and a dry-run option.
- `ActionProgressPanel` presents the current action and lifecycle stage. It uses exact
  completed-action counts and stays indeterminate when a meaningful total is unknown.
- `ActionRecoveryDialog` presents journal evidence and an explicit recovery decision.

High-risk and manual-intervention states use visible text as well as colour. Test
actions are not registered in Release or exposed through production navigation.

## Implementing a future action

1. Choose a stable lowercase/digit/dot/hyphen action ID and schema version.
2. Add complete typed descriptor metadata.
3. Detect state through a narrow mockable platform contract.
4. Build an immutable plan whose resources and operations match the actual code.
5. Validate every precondition again immediately before execution.
6. Define a minimal explicit snapshot payload containing no credentials or private
   file contents.
7. Implement one bounded apply operation; never accept an arbitrary command.
8. Verify by independently reading observed state.
9. If reversible, restore only from the supplied persisted snapshot.
10. Register the action through dependency injection.
11. Add success, apply failure, verification failure, cancellation, snapshot,
    rollback, and crash-recovery tests using temporary or in-memory state.
12. Add platform integration tests only inside an explicitly isolated environment.

## Prohibited action behaviour

An action or platform service must never:

- mutate state outside `IActionTransactionEngine`;
- apply before its snapshot is durably persisted;
- claim success without verification;
- accept arbitrary commands, scripts, registry paths, service names, or file paths;
- deserialize arbitrary CLR types from a journal;
- store passwords, cookies, tokens, credentials, private file contents, or stack
  traces in primary history;
- swallow rollback failures;
- run the desktop UI permanently elevated;
- make a test touch the actual registry, services, power configuration, network, or
  user files.

These constraints are permanent repository rules, not optional guidance.
