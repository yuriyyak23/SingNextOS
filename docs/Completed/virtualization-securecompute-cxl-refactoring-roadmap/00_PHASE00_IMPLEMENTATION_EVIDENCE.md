# Phase 00 — Authority Composition And SecureExecutionBinding

Date: 2026-09-18. Scope: SingNextOS kernel-private composition and deterministic
qualification only. No Phase 01+ implementation is included.

## Baseline and dependency facts

- Main repository is a Git worktree. Initial and final audited HEAD:
  `d32c72b06f3ad8e755ca656d1e46c32cdc059759`.
- `git status --short` was run before reading implementation sources or editing.
  Baseline was dirty: .NET 11 changes in Directory.Build.props/targets, global.json,
  SingNextOS.slnx, project files, admission/analyzer/generator/qualification tests
  and tools, runtime reclaim observability, and untracked roadmap/review files.
  These pre-existing changes were retained. The eight implementation/test paths
  listed below were clean or absent at baseline. No reset, checkout, commit,
  push, remote Git operation, or HybridCPU ISE write was performed.
- Actual solution: `SingNextOS.slnx` (25 projects). SDK:
  `11.0.100-rc.1.26425.128`; runtime/tests target `net11.0`; existing language
  version 13 and preview opt-in were not changed.
- SingPlus.Runtime references SingPlus.Contracts, Platform.Abstractions and Sip.
  Platform.HybridCpu retains its existing neutral Contracts/Model references.
  HybridCpu_ExecutableAdapter retains the existing versioned ExternalRuntime
  and ExternalRuntime.Contracts **1.3.0** packages and neutral Contracts reference.
  Existing source/package pins in HybridCpuExternalRuntimePrerequisite were
  inspected, not rewritten. No project/package reference was added.
- Read-only external directory `C:\Users\Yuriy Kurnosov\Desktop\HybridCPU ISE`
  is **not a Git worktree** (`git rev-parse HEAD`: not a git repository).
  No external source SHA is asserted. Its Contracts project, README, secure
  composition API and packaged nuspec were inspected. Local package
  `artifacts/h15/packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg`
  declares 1.14.0, net11.0, no implementation dependencies, and no production
  security claim. SHA-256:
  `53850665B15D7C8D52782F955C0ED4915D229410D6FE3168AC131181D06E080E`.
  ExternalSecureExecutionBinding carries exact child/secure/parent lineage and
  policy generation; schema presence is not positive provider admission.
  Package 1.14.0 was **not adopted** or promoted by this change.

Audited sources include roadmap README/Phase 00; existing mutation/RegionUse,
visibility/publication, CXL security and reclaim-closure documents; EXT-HCPU-006
and SecureCompute/composition external-contract documents; current runtime
virtual/secure records, platform parent/child/secure ledgers, backend reset,
process teardown, OwnedRegion reclaim reservations, CXL closure participant and
authority bridge; corresponding virtualization, secure, CXL and reclaim tests.
No later-phase roadmap was used as an implementation mandate.

## Internal mapping and authority split

| Existing owner/type | Reuse or additive change |
| --- | --- |
| VirtualDomainAuthority.Record | Reuse exact handle/generation, owner, state, parent and child bindings; no second virtual-domain registry. |
| RuntimeKernel.SecureRecord | Reuse exact secure handle/generation, owner, state, capabilities and secure bridge binding; add local policy/protection generation snapshots. |
| PlatformAuthorityBridge domain/child/secure ledgers | Reuse existing exact parent lease, child lease and secure lease; add private composition snapshot lookup. |
| RuntimeKernel | Own only the new relation ledger, admissions/drain markers, call reservations, effect/publication pins, quarantine and terminal tombstones. |
| Internal ISecureExecutionProvider v1 | Optional positive admission/revalidation/terminal-close contract; currently implemented only by a deterministic test provider. |
| Existing process cleanup and region reservations | Preserve existing root/region ownership; add relation drain before root closure and an explicit final process-reclaim barrier. |

VirtualDomain and SecureDomain remain independent local authority roots.
BindSecureExecution requires **both exact owner-bound Configure capabilities**.
It mints no capability, domain, process, mapping or device authority. Parent
equality and child-authority subset are checked against existing ledgers.
Model-only virtual domains and nested secure composition are denied. Creating
nesting under an already composed parent is also denied, without implementing
confidential nesting.

The kernel owns local capability validation, process/domain ownership, exact
relation identity/generation, registry state, draining/quarantine and reclaim.
The provider owns external secure admission, external correlation/generation,
policy/protection continuity and terminal closure/containment. Provider-issued
receipts are accepted only within the kernel-reserved exact request and after
local authority checks; diagnostics/evidence cannot reserve a relation or grant
rights. CXL remains substrate. DeviceComplete, Visible and Published are not
collapsed into one state, and no CXL readiness/evidence policy is promoted.

## Lifecycle and exactness

Internal relation identity is `(Id, Generation)`; an ID is never rebound to a
different context. The request snapshots the exact local parent binding,
VirtualDomain handle/generation, SecureDomain handle/generation, child binding,
secure bridge binding, external parent/child/secure leases and their generations,
local policy/protection generations, and a canonical proven-property snapshot.
The positive receipt additionally names the exact request, nonzero external
correlation/provider generation, policy/protection generations, contract version
1 and ProductionSecure-compatible admission. Feature presence alone is denied.

State transitions:

```text
Binding -> Active -> Checking -> Active
                  -> Closing -> Closed
any ambiguous/stale state -> Quarantined -> Closing -> Closed
invalid/partial admission -> Quarantined -> exact compensation -> Closed
                                                   | ambiguous -> Quarantined
```

Registry reservations and state transitions use a dedicated lock. **New provider
Bind/Revalidate/Close calls execute outside both the relation lock and the global
platform-memory lock.** Process relation drain runs before entering the existing
process teardown lock. After admission/revalidation the kernel rechecks local
records, generations, drain markers, provider-contract continuity and the reserved
state. Fake-provider callbacks assert neither lock is held for every new call.
The pre-existing standalone platform/root teardown orchestration is retained.

An accepted correlation/provider-generation pair is retained to reject duplicate
or replayed admission, including after terminal closure. Receipt identity cannot
be substituted across bindings. Missing, stale or mismatched local authority,
child/secure lease, parent, policy/protection generation or external revalidation
denies the operation. Existing secure-region bind/unbind increments local
protection generation; no guest secure-memory overlay is introduced.

Existing virtual/secure effect paths pin participating relations for their call
lifetime and revalidate composition before provider effects. Existing guest
mapping, trap-observation and event publication paths revalidate before returning
or committing guest-visible state. Event staging is rolled back if publication
revalidation fails. This adds a guard to existing publication, not new VirtualIo
or physical-to-guest event wiring. A quarantined relation rejects new effects,
publication and ordinary bind without making another revalidation provider call.

Close requires exact local relation identity/generation and a positive terminal
receipt for the saved exact request and saved admission response (or null if the
response was lost). ProviderClosed and ProviderEffectContained are distinct
terminal predicates. Unavailable, disconnect, missing/revoked, timeout,
cancellation, unknown outcome, nonterminal or mismatched receipt is **not** closure.
Exceptions during materialization/revalidation/close are treated as ambiguous.
Partial admission is compensatingly closed against its reserved request; a lost
response can be recovered against that request without fabricating a lease.
Unproven compensation retains the registry entry and both authority dependencies.
Close during admission, checking, another close or a pinned effect returns
draining; terminal close is idempotent and does not call the provider twice.

Domain destruction stops composition admissions and drains dependent relations
before standalone root closure. Root states remain Draining while dependent
regions require cleanup; exact resource cleanup remains available after terminal
relation close. Process teardown drains all its relations before standalone
secure/virtual domains, capability revocation and local reclaim. Live,
in-flight or quarantined relations abort teardown; the final local cleanup
barrier independently forbids process reclaim while any such relation remains.
Backend reset quarantines relations without treating reset as containment.
Independent root quarantine after reset remains independently pinned even if the
relation itself later reaches exact terminal closure.

## Focused and regression qualification

The focused file contains 44 deterministic cases: successful composition;
both capability failures and revoked authority; parent/owner mismatch;
stale virtual/secure generations; stale/missing/closed child and secure bridge
lease; policy/protection/provider generation changes; absent/unsupported or
non-production provider contract; diagnostic/evidence objects cannot supply
authority; no implicit bind from features or metadata; duplicate/replayed and
cross-binding receipt; partial admission and lost/cancelled response compensation;
ambiguous close, timeout, unknown outcome and cancellation quarantine;
no subsequent effects/publication; in-flight provider effect pins; teardown
ordering/reclaim block; terminal containment, exact recovery and idempotence;
post-event publication revalidation/rollback; protection change and drain
admissions; backend reset; private API/ABI shape.

The existing relevant suite covers VirtualDomain/secure evidence and provider
paths, CXL Type-3/Type-2, fabric/security/multi-host authority, reclaim and process
teardown. No production check was disabled or relaxed.

Final qualification results (no production check disabled):

| Command / suite | Result |
| --- | --- |
| New focused tests | exit 0; 44 passed, 0 failed, 0 skipped |
| Relevant existing tests | exit 0; 179 passed, 0 failed, 0 skipped |
| `dotnet build "SingNextOS.slnx"` | exit 0; 0 warnings, 0 errors |
| Final `dotnet test "SingNextOS.slnx"` | exit 0; 1105 passed, 0 failed, 2 existing opt-in skipped |
| Main SingPlus.Tests | 975 passed, 2 skipped (977 total) |
| SingPlus.Platform.HybridCpu.Tests | 60 passed |
| HybridCPU_NeutralRuntime.Tests | 58 passed |
| HybridCpu_ExecutableAdapter.Tests | 12 passed |
| `git diff --check` | exit 0; line-ending notices only |
| Supplemental new/untracked-file whitespace check | no trailing whitespace |

The two existing skips are
`OptInSuspendedSdkProbeHasDeterministicExitDisposition` and
`AttachFailureTerminatesSuspendedChild`; they require the existing
`SINGPLUS_RUN_SUSPENDED_CHILD_QUALIFICATION=1` host opt-in. Their checks or
environment were not altered. The final full-test run took approximately
70 seconds for the main test project. HEAD remained unchanged.

Commands, executed in order for the final implementation:

```powershell
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter FullyQualifiedName~SecureExecutionBindingTests
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter 'FullyQualifiedName~Virtualization|FullyQualifiedName~SecureCompute|FullyQualifiedName~Cxl|FullyQualifiedName~Reclaim|FullyQualifiedName~ProcessTeardown'
dotnet build "SingNextOS.slnx"
dotnet test "SingNextOS.slnx"
git diff --check
```

Local command logs are retained under `artifacts/phase00-authority/` (ignored build
artifacts). The implementation was qualified incrementally; early focused-test
compile/fixture errors were corrected before qualification. An earlier solution
run passed all 1105 tests with two existing opt-in skips. A later complete run
reported the existing GUI test
`TripleBuffersRemainSeparateAndUnavailableScanoutFallsBackToCpuOwnershipProtocol`
at `Phase9GuiOwnershipTests.cs:225` (1-second SpinWait for Loaned state). That file
was already dirty and was not edited here. This result is retained, not concealed;
its root cause is not asserted to be infrastructure or attributed to Phase 00
without proof. That attempt exited 1 (1104 passed, 1 failed, 2 skipped).
Follow-up isolated GUI test passed (exit 0, 1/1), and the final complete solution
retry passed (exit 0, 1105 passed, 2 skips) with identical code and tests:

```powershell
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --filter FullyQualifiedName~TripleBuffersRemainSeparateAndUnavailableScanoutFallsBackToCpuOwnershipProtocol
dotnet test "SingNextOS.slnx"
```

Logs: `focused.log`, `regression.log`, `build.log`, `solution-tests.log`,
`solution-tests-gui-failure.log`, `gui-isolated.log`, `diff-check.log` under
`artifacts/phase00-authority/`. The intermittent GUI timing observation remains
an honest qualification limitation; no unrelated GUI test was edited to make
the suite green.

## Changed files

Projects changed: **SingPlus.Runtime**, **SingPlus.Tests** (additive source only;
their pre-existing project-file edits were not modified).

1. `src/Runtime/SingPlus.Runtime/SecureCompute/SecureExecutionContracts.cs` — private relation/provider boundary.
2. `src/Runtime/SingPlus.Runtime/SecureCompute/RuntimeKernel.SecureExecution.cs` — relation lifecycle/guards/drain/recovery.
3. `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.SecureExecution.cs` — exact existing-ledger snapshot.
4. `src/Runtime/SingPlus.Runtime/SecureCompute/RuntimeKernel.SecureCompute.cs` — generations, effect pins/revalidation and dependent drain.
5. `src/Runtime/SingPlus.Runtime/Virtualization/RuntimeKernel.Virtualization.cs` — effect/publication guards and dependent drain.
6. `src/Runtime/SingPlus.Runtime/RuntimeKernel.ProcessTeardown.cs` — pre-root relation drain and final reclaim barrier.
7. `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformBackendReset.cs` — relation quarantine on reset.
8. `tests/SingPlus.Tests/Platform/SecureExecutionBindingTests.cs` — focused deterministic matrix.
9. This evidence document.

All new composition types and operations are internal/private. Public Contracts,
SIP protocol/generated client surface and provider feature claims are unchanged.
No CXL endpoint, BDF, HDM, DPA, route, Fabric Manager identity, raw platform handle
or physical mapping was added to a public/SIP API. The test checks exported API
shape and the absence of public SecureExecution operations/types.

## Remaining gates and explicit non-claims

- No production implementation of the new internal composition provider exists
  in this slice. The existing HybridCPU adapter stays on qualified 1.3.0 and its
  SecureDomains gate remains Unavailable. Adopting a newer external package and
  adapter requires separately pinned source/package and provider conformance;
  the read-only external tree has no assertable Git SHA.
- Local policy generation is snapshotted, but no policy-reconfiguration API or
  Phase 04 orchestration is added. External current policy/protection generation
  validation belongs to the provider. Terminal recovery closes the relation;
  it does not manufacture independent root closure or provider-loss containment.
- Closed relation/correlation tombstones are retained to reject replay. No
  tombstone-retention/compaction policy or production transport recovery service
  is introduced. New provider methods are a synchronous kernel-private seam.
- No new Type-3 secure guest memory/overlay, VirtualIo wiring, Type-2 context,
  CXL topology/transport, DirectCoherentWrite, P2P, multi-host writable secure
  memory, confidential migration, nested confidential execution, ISA/compiler
  or VMX semantics is implemented. Phase 01–05 remain outside this change.

**Phase 00 is not ProductionSecure promotion, real production CXL-provider
integration, or a production-security claim. Fake-provider tests and this
evidence document prove only the implemented deterministic software contracts;
neither can supply local or external authority.**
