# EXT-HCPU-006

**Status:** Satisfied by the pinned ExternalRuntime 1.3.0 V3 facade and the explicitly composed SingNextOS executable child adapter.

## Qualified 1.3.0 evidence — 2026-09-11

The external implementation exposes `IHybridCpuChildDomainRuntimeV3`, inspects an immutable HCEXE package through `HybridCPU.Compiler.Core`, materializes it into ISE-owned memory, and owns a bounded ISE execution task through terminal close. Artifact admission is correlated with exact child and guest-mapping epochs; Start creates a distinct execution generation and returns independently observed non-zero retired-work evidence. Bounded virtual-I/O is parent-scoped and requires exact terminal close. External facade tests pass 34/34.

The implementation is pinned to external commit `fd37b00a207a162baaa860f3f8b96c0c66d7e691`. Repository-local Contracts and Runtime 1.3.0 packages are SHA-qualified by `HybridCpuExternalRuntimePrerequisite` and verified against the tracked package bytes by adapter tests. `VirtualizationDomains = Executable` is published only when `HybridCpuExecutableChildAdapter` is explicitly composed and both executable-artifact and bounded-I/O feature gates pass. Default composition remains unavailable; `NestedDomains` and production trap delivery remain independently unclaimed.

## Required external capability

The external HybridCPU integration must expose truthful, versioned neutral bindings for child virtualization and must keep `RuntimeAdmission` distinct from proof that guest instructions executed. VMX/VMCS compatibility state, a successful child `Start`, an event receipt, or an entryless child lifecycle are not executable-artifact evidence.

For a production `Executable` virtualization claim, the external owner must bind an exact validated executable artifact to the exact child and guest-memory custody before `Start` can produce executable effect.

## Current SingNextOS status

SingNextOS owns and can qualify the following locally:

```text
Phase5-01  complete
  neutral child/guest/event/trap/bounded-I/O contracts

Phase5-02  complete
  bridge-owned local child/guest/I-O ledgers
  local generation != provider generation
  exact parent/owner binding
  exact terminal receipt validation

Phase5-03  complete (Sing-owned RuntimeKernel integration / qualification)
  VirtualDomainAuthority remains local authority
  child lifecycle path is separate from Host ModelOnly
  exact guest mapping custody and deterministic teardown
  event publication occurs only after exact external acceptance
  ambiguous external closure pins/quarantines local authority

Phase5-04  complete for explicit executable-adapter composition
  exact artifact/child/mapping/start-operation correlation
  independently validated terminal retired-work evidence
  production bounded virtual-I/O ownership and terminal close
  pinned ExternalRuntime V3 revision and package hashes
```

None of those facts implies executable guest execution. The locally tracked child facade may expose `RuntimeAdmission`; that classification remains weaker than `Executable`.

## Read-only external baseline — 2026-09-11

Repository analyzed without modification:

```text
repository: https://github.com/yuriyyak23/HybridCPU-v2
master: b98e875d5c05c45b2c47a48f5c312ad38a77784b
```

No branch, commit, pull request, or file modification in `HybridCPU-v2` is part of this requirement or of the SingNextOS Phase5-03 work.

### Existing HCEXE inspector

The audited tree already contains the public inspector:

```text
HybridCpuRestrictedImageBuilderV1.Inspect(byte[])
```

in the `HybridCPU.Compiler.Core` assembly. The inspector validates the restricted HCEXE package, including the `HCEXE001` magic, schema/layout, package checksum, target/managed/native/platform contract digests, embedded image checksum, image base, entry address, stack/startup register state and runtime-bootstrap metadata.

The production startup options bound package size to:

```text
256 MiB + 4096 bytes
```

and the inspected result contains immutable artifact/startup facts including package/image SHA-256 values, image bytes/base, entry address, initial registers and runtime bootstrap metadata.

The inspected HCEXE result is evidence/facts, not execution authority.

### Existing ISE managed loader/runner

The audited ISE code explicitly states that HCEXE parsing remains outside ISE. The managed image loader accepts already inspected image/startup facts, validates bounded layout, writes the exact image into the supplied ISE memory, publishes executable annotations and boots the supplied runtime kernel using the inspected image/entry/bootstrap facts.

The managed guest execution request likewise accepts immutable startup facts rather than HCEXE package syntax. The runner can produce execution evidence such as retired pipeline cycles, final program counter, last retired bundle PC and `LastRetireSequence`.

Therefore the architecture does **not** require `ISE -> Compiler`, an ISA change, a VMX authority change, or raw HCEXE types in SingNextOS.

### Historical pre-V3 dependency gap — resolved

Before ExternalRuntime 1.3.0 V3, `HybridCPU_ExternalRuntime` referenced Contracts and ISE but did not compose the `HybridCPU.Compiler.Core` HCEXE inspector. Its public child API also lacked the executable-artifact admission/receipt correlation required for Phase5-04.

That was an external HybridCPU-v2 task. It is resolved by the pinned V3 implementation recorded above: the external runtime now owns inspection, materialization, execution admission and terminal evidence behind its package boundary. SingNextOS continues to consume that boundary only through `HybridCpu_ExecutableAdapter`; it neither parses HCEXE nor references compiler/ISE implementation types.

## Exact Phase5-04 executable prerequisite

The external contract must provide a bounded semantic operation equivalent to:

```text
validated immutable executable artifact
+ exact child lease
+ exact guest image/mapping custody
+ immutable entry/startup facts
+ execution admission generation
-> Start may produce executable effect
```

The public semantic correlation must be sufficient to prove at least:

```text
ArtifactIdentity
ArtifactDigest
ChildLease
GuestMappingIdentity / image custody identity
ExecutionGeneration
StartOperation
ExecutionReceipt
```

Provider IDs remain provider-private and must not become Sing capabilities.

### Required ownership answers

The qualified Phase5-04 design answers these ownership questions unambiguously:

- **Who owns executable bytes?** The external runtime/artifact-admission owner for the duration of inspection/materialization; SingNextOS must not own HCEXE parser state.
- **Who validates them?** The external HCEXE admission/inspector boundary using the existing restricted-image validation semantics.
- **Who pins their digest?** The external execution-admission record, correlated with the exact child/execution generation.
- **Who maps them into child guest memory?** The external execution owner through the ISE managed image loader (or an equivalent production owner), using exact child guest-memory custody.
- **Who owns entry/startup facts?** The immutable inspected-artifact admission record; ISE consumes these facts but does not parse HCEXE.
- **Who proves `Start` refers to that exact artifact?** A versioned external execution-admission/start contract that binds child lease + artifact identity/digest + mapping/image custody + execution generation + start operation.
- **Who proves execution actually retired guest work?** The external execution owner, using real ISE execution evidence (for example a valid retired-work sequence/result), wrapped in an exact external execution receipt. A lifecycle `Start` receipt alone is insufficient.
- **Who proves terminal execution/close?** The same external execution/child owner through exact terminal receipts that are correlated with the admitted artifact/execution generation and child closure.

## Required negative semantics

The external contract must fail closed for:

```text
empty or oversized artifact
bad HCEXE magic/schema/layout/checksum
wrong target/managed/native/platform digest
image checksum mismatch
entry/startup facts not belonging to the inspected artifact
artifact/child mismatch
artifact/guest-mapping mismatch
stale child or execution generation
replayed start operation
Start before artifact admission/materialization
receipt for another artifact/child/mapping/operation
zero retired-work evidence when an executable-success claim requires retired guest work
revoked/faulted/unknown/malformed/non-terminal closure
```

Ambiguous accepted work must remain pinned/quarantined; it may not be converted into local reclaim by retrying or by manufacturing local evidence.

## Feature/claim rule

The following implications are forbidden:

```text
CreateChild success            != Executable
TransitionChild(Start) success != Executable
RuntimeAdmission               != Executable
child lifecycle                != guest execution
Start receipt                  != executable-artifact evidence
ModelOnly                      != Executable
```

A deterministic fake provider may exercise lifecycle/authority composition in Phase5-03. Its receipts never raise a production feature claim.

Only a positive external path with exact artifact admission/materialization and real execution evidence may support `VirtualizationDomains = Executable` for the specifically proven profile.

## Allowed dependency direction

SingNextOS must retain:

```text
SingNextOS
  -> NeutralRuntime semantic contract
  -> HybridCpu_ExecutableAdapter
  -> HybridCPU ExternalRuntime semantic contract
  -> external artifact admission / execution owner
```

Forbidden direct SingNextOS dependencies include:

```text
HybridCPU_Compiler / HybridCPU.Compiler.Core
HybridCpuRestrictedImageBuilderV1
HybridCpuRestrictedImageV1
HybridCpuIseManagedGuestExecutionRunnerV1
raw ISE/ISA/VMX execution descriptors
```

HCEXE is an external backend artifact concern. SingNextOS may learn only backend-neutral artifact identity/admission state if a future neutral contract actually needs it.

## External technical task

Implement and qualify, in `HybridCPU-v2`, a production executable-child admission composition with these deliverables:

1. a versioned public executable-artifact admission contract with explicit maximum byte bound;
2. runtime-owned invocation of the existing HCEXE inspector, either by adding a correctly packaged `HybridCPU.Compiler.Core` dependency or by extracting a runtime-safe inspector package;
3. an immutable admitted-artifact record that pins package/image digest and startup facts;
4. exact materialization of the inspected image into memory owned by the exact child execution context;
5. exact correlation between artifact, child lease, guest mapping/image custody, execution generation and start operation;
6. an ISE managed execution-owner composition that consumes inspected facts without adding `ISE -> Compiler`;
7. a positive execution receipt that distinguishes accepted lifecycle from actual retired guest work;
8. exact terminal execution/child closure evidence and negative tests for stale/revoked/faulted/malformed/ambiguous states;
9. feature discovery that remains `RuntimeAdmission` until all executable-positive rows pass.

This task must be completed externally. SingNextOS must not emulate the missing mechanism with local HCEXE parsing or a fake `Start` implementation.

## Other EXT-HCPU-006 scope

Nested-domain, platform-evidence and SecureCompute claims remain independently gated. Assigned device/I/O support may be profile-specific; any profile that claims it must provide the corresponding bounded external mechanism and terminal closure evidence.

The correct adapter behavior for absent external functionality is explicit unavailability.
