# SNO-03: request for an owned production trust/state provider

## Actual gap

`BootManifestVerifier` already consumes `IBootTrustPolicyVerifier`; it requires
an exact key-set ID, trust epoch, role, production lock and signature algorithm.
`ProtectedBootStateMachine.Persist` already consumes `IBootProtectedState` and
requires write, durable flush, authenticated candidate readback, atomic commit
marker and authenticated committed readback. Current source has no concrete
`IBootProtectedState` outside the test fake. The QEMU lab's `DevelopmentTrust`,
compiled development root and static generation floor are intentionally not a
production owner.

## Required producer and consumer

The authorized firmware/platform integration owner must supply one concrete
producer pair, adopted by the native boot startup owner when that owner is
separately authorized:

1. `IBootTrustPolicyVerifier` backed by independently provisioned key-set
   identity and current trust epoch. It must resolve exactly one key, reject
   duplicate/revoked keys, enforce Production/Development/Recovery role and
   production lock, and verify the canonical SHA-384 signed bytes.
2. `IBootProtectedState` backed by authenticated durable monotonic storage for
   each `BootStateDomain`. Its read paths must not return unauthenticated or
   non-committed state; its write/flush/candidate-read/commit sequence must bind
   the reset epoch and return ambiguity rather than assuming no effect.

The consumer is existing `BootManifestVerifier` plus
`ProtectedBootStateMachine`; no parallel manifest parser, state machine or lab
HMAC store is requested. The producer must not derive its key identity, epoch
or rollback floor from BootInfo, QMP, EFI lab files, warm RAM restoration or a
static image-generation floor.

## Admission, effect, publication and closure

Admission is the current provisioned key-set/role/revocation state plus current
durable-store generation. The state effect is candidate write and flush;
publication is the atomic commit marker followed by an exact authenticated
readback. On torn write, failed flush, lost reply, reset, epoch loss or
inconsistent replicas, the result is no boot-state publication and the affected
state remains pinned/quarantined until the original owner supplies recovery
evidence. A completion or HMAC verification alone is not durable closure.

## Required verification before status can move

Run the real provider through existing consumers for: rollback below floor,
revoked and wrong-role key, duplicate key resolution, production-lock rejection
of development role, torn candidate write, post-flush readback mismatch,
lost commit reply, reset during every write phase, replica disagreement and
recovery from the last authenticated committed record. Each terminal result
must record exact provisioned-key/store generations and backend identity.

This request does not authorize firmware replacement, a new UEFI production
loader, `BootCapsuleEntry.Run`, RuntimeKernel execution, or enabling a feature
gate. Until an authorized producer/caller exists, SNO-03 remains
`Missing / scope-blocked`.

## Recommendation review, 25.10.2026

TPM2 NV or trusted platform storage may be investigated as candidates, not
selected/authorized providers. Mutable private UEFI VARS, software TPM and VM
snapshots require a separate threat model; none automatically gives independent
durable monotonic authority. Existing manifest/state consumers remain the target.
