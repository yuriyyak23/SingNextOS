# Phase 02 implementation evidence: Virtual I/O lifecycle

## Scope

This increment adds kernel-private `VirtualIoBinding` lifecycle composition for
the existing provider-neutral bounded Virtual I/O contract.  It does not add a
CXL endpoint/fabric identity, provider handle, or a public/SIP capability.

## Authority composition

`RuntimeKernel.BindVirtualIo` accepts an exact current virtual-domain child,
parent `PlatformDeviceLease`, bounded `PlatformVirtualIoProfile`, and optional
exact `SecureExecutionBinding`.  The platform bridge remains the authority for
the external child/device Virtual I/O lease.  The kernel owns registry,
revalidation, quarantine, and teardown sequencing.

The profile is validated by the existing `PlatformVirtualIoContract`, which
requires non-empty rights, a positive transfer limit, and a subset of the exact
parent device rights.  A secure binding is revalidated separately; neither it
nor Virtual I/O is reinterpreted as the other authority.

## Lifecycle

* Stale virtual-domain, child, device, or secure-execution state fails closed
  and quarantines the internal binding.
* Virtual-domain destroy, parent device revoke, process teardown, and backend
  reset stop further use.  Process teardown closes Virtual I/O before closing
  secure or virtual roots.
* Provider close exceptions and nonterminal results leave the internal binding
  quarantined and prevent lower-authority reclaim through the existing teardown
  failure path.

## Verification

```text
dotnet build src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj --no-restore
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter
  FullyQualifiedName~ProcessTeardownLifecycleTests|FullyQualifiedName~SecureExecutionBindingTests|FullyQualifiedName~CxlType3MemoryProviderTests --no-restore
git diff --check
```

The runtime build succeeded with zero warnings/errors.  The focused teardown,
secure-execution, and Type-3 suite passed 67 tests; `git diff --check` passed.

## Boundaries

This is not a real CXL transport implementation, does not promote
`ProductionSecure`, and does not execute Phase 05 validation work.
