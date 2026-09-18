# H09/H10/H12 SingNextOS Integration Evidence

## Baseline and isolation

Implementation was performed in SingNextOS at baseline HEAD
`d32c72b06f3ad8e755ca656d1e46c32cdc059759`. The working tree already contained
owner and parallel-session changes. No reset, checkout, commit, push or remote
operation was used. Files belonging to the parallel
`virtualization-securecompute-cxl-refactoring-roadmap` implementation were not
edited by this slice.

The repository-local package feed now contains the exact
`HybridCPU.ExternalRuntime.Contracts` 1.14.0 package. SHA-256:

```text
B96E99BDA066EE585B26A11CBFA7B68CE6BF44FC0006679483CCC1A4EEB678C2
```

`SingPlus.Runtime` and `HybridCpu_ExecutableAdapter` use exact `[1.14.0]`
semantic-contract pins with committed lock-file candidates. The established
executable child implementation remains exactly `[1.3.0]`; NuGet resolves its
additive Contracts dependency to the direct 1.14.0 pin.

## H09 real SingNextOS adapter suite

`HybridCpuExternalOperationProvider` implements the published
`IExternalOperationProvider` and `IExternalOperationCancellationProvider`
interfaces over the actual `RuntimeKernel` external-operation authority. It is
owned by the privileged Runtime layer; no HybridCPU ISE/compiler project or
implementation assembly is referenced.

The adapter translates only semantic effect, visibility, cancellation and
opaque correlation data. SingNextOS creates the operation/scope correlation and
remains authority for preparation, admission, submission, completion,
visibility, publication and release. Provider publication callbacks run
outside the adapter lock.

The integration tests execute the exact sequence:

```text
Prepared -> Admitted -> Submitted -> DeviceComplete -> Visible -> Published -> Released
```

They also reject duplicate admission, cross-operation identity, generation
reconfiguration, completion without visibility, premature publication and
release without proven provider closure. Missing or stale state never writes
the output and never becomes release.

The H00-H17 correctness audit found and repaired one fail-open translation:
a SingNextOS operation whose completion disposition was `Faulted` could be
reported as a successful `DeviceComplete` receipt, and `ProviderLost` at
`Submitted` could remain `Pending`. The adapter now maps faulted, discarded and
cancelled dispositions to faulted receipts, and maps provider loss to an
unavailable poll result with an `Unknown` outcome. The regression test proves
that neither case becomes success, publication or release.

## H10 same-image provider matrix

`HybridCpuCxlDeploymentConformanceTests` uses one immutable byte image and
executes the same copy semantics through four repository execution contours:

1. local owned memory;
2. real SingNextOS Type-3 placement authority;
3. real SingNextOS Type-2 staged accelerator service;
4. the generic versioned external-operation provider adapter.

Every contour produces the same bytes and the image SHA-256 remains unchanged.
Provider selection, Type-3 placement and Type-2 fabric identities never enter
the image or HybridCPU semantic ABI. Direct coherent output remains gated.

## H12 deployment conformance

The deployment negative test combines the actual SingNextOS CXL model and
runtime authorities. It proves:

- Type-3 hot-remove makes the placement migration-required while preserving
  ownership until explicit close;
- Type-2 visibility failure executes no publication callback and reaches
  released state only after provider closure;
- external generation drift returns `Stale`, performs no submit and produces no
  output;
- the adapter assembly dependency and public surface contain no HybridCPU ISE,
  HDM, DPA, BDF, physical-address, route or switch-port identity.

This closes the repository deployment-model conformance gap. It is executable
SingNextOS/CXL provider integration, not evidence of a physical CXL lab, QEMU
machine configuration, hardware certification or production-security status.

## Qualification

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build \
  --filter '<H09/H10/H12 + ExternalOperation + Type2 + Type3>' --nologo -v:q
PASS - 62/62

dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj \
  --no-restore --nologo -v:q
PASS - 12/12

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --filter \
  '<architecture policies + GUI dependency guard + H09/H10/H12>' --nologo -v:q
PASS - 45/45

dotnet build SingNextOS.slnx --no-restore --nologo -v:q
PASS - 0 warnings, 0 errors

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --nologo -v:q
PASS - 994 passed, 2 skipped

dotnet test SingNextOS.slnx --no-build --nologo -v:q -m:1
PASS - 1124 passed, 2 skipped, 0 failed
```

The H17 composed execution, exact closure and remaining fault matrix also pass.
All H09/H10/H12 tests and their architecture/dependency guards remain green.

## Status

- H09 real SingNextOS adapter integration suite: **CLOSED** for the published
  Contracts 1.14.0 repository boundary.
- H10 same-image local/Type-3/Type-2/other-provider matrix: **CLOSED** in the
  executable SingNextOS model environment.
- H12 deployment conformance: **CLOSED** for repository CXL model deployment;
  physical hardware/QEMU qualification remains an external environment claim.
