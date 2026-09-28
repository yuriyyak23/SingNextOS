# Phase 11 — Representative Service Migration

## Goal

Move real SingNextOS services onto the strengthened ledger/seal/sentry model in small independently reversible waves.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Migration order

### Wave A — Network/socket

Mostly completed as Phase-06 pilot; remove compatibility shortcuts after qualification.

### Wave B — File service

Preserve session/object generation semantics. A sealed handle may replace the public raw object identity, but it must not replace the capability that authorizes operations. File open/read/write/close bind the sealed identity to an exact capability lineage/operation lease through the Authority Composition Protocol. Namespace/create authority remains a separate explicit capability.

### Wave C — Process service

Maintain strict separation:

```text
ProcessHandle = identity
Process control capability = authority
sealed control object = exact process-object identity/lifecycle binding
```

Child creation/delegation uses the single capability ledger and atomic quota/depth rules.

### Wave D — selected GUI/queue/TLS/session objects

Only migrate services that actually exist and have a concrete use case. Do not invent `SharedArena` to make migration uniform.

## Confused-deputy review

For every method record:

```text
caller identity
resource/object being acted on
required explicit authority
exact target subject
whether delegation occurs
whether external effect occurs
whether Region ownership/borrow changes
what closes authority on failure
which authoritative owners/pins participate in the admission attempt
which static EffectRevocationPolicy applies
lock order, provider-call boundary and reverse compensation order
```

A service's own internal privilege does not authorize it to use a caller-supplied resource unless the caller provided the exact required authority. The checklist is emitted as a deterministic review artifact per migrated method and is checked for completeness in CI; it is not merely prose in a PR description.

## Primary paths

```text
src/Runtime/SingPlus.Runtime/NativeServices/RuntimeNativeServiceHosts.cs
src/Sip/SingPlus.Sip/FileSystem/
src/Sip/SingPlus.Sip/Networking/
src/Sip/SingPlus.Sip/Process/
src/Sip/SingPlus.Sip/Gui/
contracts/SingPlus.Contracts/NativeServiceContracts.cs
tests/SingPlus.Tests/NativeServices/
```

## PR slices

One service family per PR. Each PR carries compatibility adapter, negative tests and rollback flag only while migration is incomplete.

## Tests

Per service:

- guessed object token;
- right object wrong caller/session;
- revoked object capability;
- valid sealed object identity with missing/wrong capability lineage;
- service restart/stale handle;
- sibling object enumeration/substitution;
- rights/operation widening;
- exception/cancel cleanup;
- delegated response authority exactly declared;
- process identity does not imply terminate/debug authority.

## Exit criteria

Network, filesystem and process control each demonstrate the strengthened model in production runtime code, not only model/tests. No migrated method treats sealed-handle possession as rights authority. Compatibility V1 paths are either removed or explicitly non-ManagedCap legacy surfaces.
