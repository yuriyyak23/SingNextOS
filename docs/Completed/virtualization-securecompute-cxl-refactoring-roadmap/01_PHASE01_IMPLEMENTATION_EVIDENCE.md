# Phase 01 implementation evidence: secure guest memory and Type-3 composition

## Baseline

The worktree is a Git worktree.  The baseline commit used for this increment was
`d32c72b06f3ad8e755ca656d1e46c32cdc059759`; it already contained unrelated,
uncommitted .NET 11 enablement and documentation changes.  Those changes were
preserved.

## Reused authority and mapping model

`VirtualDomainAuthority` remains the sole source for `GuestRegionMapping` and
its hidden exact parent `PlatformRegionMapping`.  `RegionAuthority` remains the
owner of the `OwnedRegion` and `RegionBackingLease`.  `CxlType3MemoryAuthority`
remains the authority for its fabric and memory generations.  No CXL guest
capability, CXL region type, second platform mapping, or public/SIP handle was
introduced.

The new kernel-private `SecureGuestRegionBinding` stores the exact active
`SecureExecutionBinding`, guest mapping, parent platform mapping, and secure
region class.  It calls the existing provider secure-region operation with the
already-resolved parent mapping only.

## Lifecycle and failure semantics

* Binding validates current process, secure execution, virtual domain, guest
  mapping and hidden parent lineage before the provider effect.  It revalidates
  the same lineage afterwards; a changed result is compensated or quarantined.
* If the region has a Type-3 placement, the placement revalidates its exact
  backing lease, endpoint/fabric binding, and memory binding before a secure
  overlay provider effect.  A stale generation changes the placement to
  `MigrationRequired` and blocks the effect.
* Guest mapping closure first closes every dependent secure overlay.  A failed
  or unknown secure unbind leaves the overlay quarantined, blocks guest closure
  and blocks process teardown/reclaim.
* Type-3 close refuses to release backing while `RegionAuthority` reports the
  existing platform/guest mapping reservation.  A secure overlay therefore
  cannot outlive the guest-mapping-backed region, and closing only the overlay
  never releases Type-3 backing.
* Provider calls execute outside the new overlay registry lock.  Provider
  exceptions and non-terminal responses are treated as ambiguous and remain
  quarantined; no local retry fabricates terminal closure.

## Changed files

* `src/Runtime/SingPlus.Runtime/SecureCompute/RuntimeKernel.SecureGuestRegion.cs`
* `src/Runtime/SingPlus.Runtime/Virtualization/RuntimeKernel.Virtualization.cs`
* `src/Runtime/SingPlus.Runtime/SecureCompute/RuntimeKernel.SecureCompute.cs`
* `src/Runtime/SingPlus.Runtime/Cxl/CxlTeardownParticipant.cs`
* `src/Runtime/SingPlus.Runtime/Cxl/CxlType3MemoryAuthority.cs`
* `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`
* `tests/SingPlus.Tests/Platform/SecureExecutionBindingTests.cs`

## Focused qualification

Commands run after the implementation:

```text
dotnet build src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj --no-restore
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter FullyQualifiedName~SecureExecutionBindingTests --no-restore
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter 'FullyQualifiedName~SecureExecutionBindingTests|FullyQualifiedName~CxlType3MemoryProviderTests' --no-restore
```

The runtime build succeeded with zero warnings and zero errors.  The combined
focused suite passed 62 tests.  The final secure-execution focused rerun follows
the same implementation and is recorded with the full qualification commands
for the phase.

## Boundaries and remaining gaps

The deterministic test provider is test-only and does not promote any feature
claim.  This phase does not implement real CXL transport, physical mappings,
fabric identities, CXL.mem mechanics, ProductionSecure promotion, confidential
migration, or any Phase 02+ behavior.  Raw CXL/provider/platform identities
remain internal to the provider bridge and are absent from public/SIP ABI.
