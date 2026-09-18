# H00-H17 Completeness And Correctness Audit

Audit date: 2026-09-18.

## Baseline and isolation

SingNextOS is a Git worktree at baseline HEAD
`d32c72b06f3ad8e755ca656d1e46c32cdc059759`. The worktree was already dirty
and is shared with another session implementing
`virtualization-securecompute-cxl-refactoring-roadmap`. This audit did not
reset, checkout, commit, push or use a remote. After that plan's Phase 5
end-to-end contour became executable, H17 added only the remaining composed
fault cases to its existing test class and the shared Type-2 lifecycle.

HybridCPU ISE was used read-only for its H00-H17 phase evidence. Its packaged
contract is consumed here from
`.packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg`, SHA-256
`B96E99BDA066EE585B26A11CBFA7B68CE6BF44FC0006679483CCC1A4EEB678C2`.
Both SingNextOS consumers pin exact version `[1.14.0]`; locked restore passes.

## Phase audit

| Phase | Result | Executable conclusion |
|---|---|---|
| H00 | PASS | Authority, lane and lifecycle decisions are recorded. CPU legality, provider authority, replay evidence, visibility, publication and release remain distinct. |
| H01 | PASS | The 1.14.0 package contains the additive immutable operation contract, opaque generations and ABI baselines, with no implementation dependency. |
| H02 | PASS | CPU guard and provider admission remain independent exact gates; no SingNextOS type enters the contract ABI. |
| H03 | PASS | Replay evidence cannot authorize a second provider effect; stale and ambiguous effects remain barriers. |
| H04 | PASS (model) | Lane 7 uses the provider-neutral adapter seam without CXL topology in decode or scheduling. This is not physical-provider proof. |
| H05 | PASS (model) | Lane 6 remains distinct and its provider mediation preserves DSC semantics. This is not a physical DMA/CXL claim. |
| H06 | PASS | `DeviceComplete`, `Visible`, `Published` and `Released` are separate states and receipts. Completion alone cannot publish. |
| H07 | PASS | Compiler IR carries semantic intent and no CXL topology or live authority. |
| H08 | PASS | Lowering retains existing carriers and defers provider selection to runtime admission. |
| H09 | PASS | `HybridCpuExternalOperationProvider` executes the published 1.14.0 ABI over the real SingNextOS `RuntimeKernel`; 62 focused lifecycle/CXL tests pass. |
| H10 | PASS (repository model) | One immutable image produces identical output over local, Type-3, Type-2 and generic external-provider contours. Physical Type-2/Type-3 hardware is not claimed. |
| H11 | PASS (repository model) | Reset, generation drift, cancellation and provider loss fail closed. Provider notifications from physical hardware remain outside the repository model. |
| H12 | PASS (repository model) | Negative deployment conformance covers hot-remove, visibility failure, stale generations and ABI exclusions. Physical lab/QEMU conformance remains external evidence. |
| H13 | PASS (manifest) | Migration ordering and non-claim boundaries are documented. No merge or deployment claim is made. |
| H14 | PASS (contract) | SecureCompute exact generation, compensation and evidence/authority separation are covered. `ProductionSecure` is not promoted. |
| H15 | PASS (contract seam) | Exact child/secure/mapping/I/O composition and publication-authorized event delivery exist and fail closed. |
| H16 | PASS | Compiler secure/virtual intent remains semantic and provider-neutral. |
| H17 | PASS (repository executable model) | The full 18-row matrix now includes clean composed execution/teardown plus exact guest-work reconfiguration, live-mapping hot-remove and post-completion security reset. No physical-provider or `ProductionSecure` claim is made. |

## Defect corrected by this audit

`HybridCpuExternalOperationProvider.Poll` previously inspected only the
lifecycle state. A SingNextOS completion with disposition `Faulted` could
therefore become a successful `DeviceComplete` receipt, while provider loss at
`Submitted` could be returned as `Pending` indefinitely.

The adapter now checks disposition before emitting a success receipt:

- `Faulted`, `Discarded` and `Cancelled` return a faulted poll result and a
  faulted stage-appropriate receipt;
- `ProviderLost` returns `Unavailable` with the contract's fail-closed
  `Unknown` outcome;
- neither path advances to visibility, publication or release.

`FaultedCompletionAndProviderLossNeverBecomePendingOrSuccessfulReceipts`
locks this behavior down.

## Correlation, stale rejection and dependency boundary

The adapter accepts only the exact immutable request stored for its typed
correlation. Request identity, operation generation, scope, contract version
and `ExternalGenerationSet` therefore participate in value equality. Duplicate
admission, a copied correlation with a different operation, and a request from
an earlier generation set are rejected as stale before submit or publication.

SingNextOS remains authority for prepare, admit, submit, completion,
visibility, publication and release. Provider callbacks execute outside the
adapter lock. Provider absence, timeout, cancellation, `NotFound`, fault or
stale state never proves success or closure.

`SingPlus.Runtime` references only the versioned Contracts package plus its
normal SingNextOS abstractions. Contracts has no dependency on SingNextOS,
HybridCPU ISE, GUI, diagnostics or a transport. Public-surface guards reject
HDM, DPA, BDF, physical-address, fabric-route and switch-port identities.

## H17 matrix status

The repository contains focused evidence for stale
child/secure/guest/fabric/security generations, ordinary Type-2 fabric
reconfiguration, ordinary Type-3 hot-remove, ambiguous closure, failed
compensation, event authorization, opaque same-endpoint binding identity,
topology-free compiler intent and unsupported secure-operation denial. The
focused H17 lifecycle/CXL suite passes 150/150.

The positive composed scenario now covers Type-3 placement, exact guest
mapping, secure guest overlays, bounded virtual I/O, secure virtualized Type-2
execution, staged visibility/publication, guest event delivery and clean exact
closure through process reclaim. Rows 10-12 are now composed tests over that
same path:

- Fabric Manager drain closes the exact live Type-2 provider effect before
  external-operation release and reclaim;
- Type-3 hot-remove with live guest mappings blocks publication, preserves
  ownership, and permits reclaim only after exact mapping/backing closure;
- a security-generation reset injected after `DeviceComplete` is observed is
  rejected before visibility, publication and guest event delivery.

The last case exposed and corrected a time-of-check gap. Type-2 completion now
revalidates required security both before observing completion and again after
recording `DeviceComplete`, before any visibility acquire. H17 is closed for
the deterministic repository executable model. `ProductionSecure` remains
unadvertised because no independent production provider was qualified.

## Qualification snapshot

```text
dotnet restore src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj --locked-mode
PASS

dotnet restore tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj --locked-mode
PASS

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build \
  --filter '<H09/H10/H12 + ExternalOperation + Type2 + Type3>'
PASS - 62/62

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build \
  --filter '<architecture/dependency/GUI + H09/H10/H12>'
PASS - 45/45

dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore
PASS - 12/12

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build \
  --filter '<H17 focused lifecycle/CXL matrix>'
PASS - 150/150

dotnet build SingNextOS.slnx --no-restore
PASS - 0 warnings, 0 errors

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build
PASS - 994 passed, 2 skipped

dotnet test SingNextOS.slnx --no-build -m:1
PASS - 1124 passed, 2 skipped, 0 failed
```

## Remaining boundary

There is no real CXL transport, physical Type-2/Type-3 device run, multi-host
production deployment or production-security proof in this evidence. H00-H17
are closed for repository contract/model execution only; they are not hardware
certification or a `ProductionSecure` claim. Future promotion requires an
independently qualified production provider and the same negative matrix in
that environment.
