# Phase 10 — Security, SecureCompute and Multi-host Evidence

## Implemented security boundary

**Complete for the software/model scope.** `CxlSecurityProperties` and
`CxlSecurityStateSnapshot` express link encryption availability, IDE state,
device authentication, firmware measurement and trusted-execution capability
as evidence with an exact device and evidence generation.

`CxlSecurityAuthority` evaluates a required predicate only after validating the
existing device lease, RegionUse and fabric binding. Its readiness result is not
a mapping or capability. Missing required evidence rejects an authorized
operation; trusted evidence cannot authorize a missing region use. A security
session reset increments only evidence generation and does not mutate region
authority.

`CxlSecurityModelProvider` implements the existing `EvidenceRecord` taxonomy and
does not implement any platform authority interface. Hardware-rooted is false;
the model therefore makes no IDE or TEE hardware claim. When security is not a
caller requirement, an unavailable provider leaves ordinary staged execution
eligible.

## Multi-host gate

`CxlMultiHostGate` permits single-host writable binding and explicit provider-
supported read-only sharing. A second writer remains rejected through Active,
Fenced and Reclaiming states; rebinding is possible only after verified
`CompleteReclaim` releases the RegionUse. A boolean cannot enable distributed
writable authority: that path returns `PlatformUnsupported` until a reviewed
protocol exists.

Host disappearance must therefore be represented as Fence -> Reclaiming ->
Released. There is no silent ownership reassignment, cached security authority
or CXL token inside `RegionAuthority`.

## Executable evidence

Focused CXL security, existing SecureCompute evidence, fabric, authority and
planner tests prove:

- authenticated evidence without RegionUse rejects;
- valid authority with missing IDE evidence rejects;
- security reset makes the captured evidence generation stale while RegionUse
  remains valid;
- unavailable optional security preserves the non-secure staged path;
- second-host writable binding waits for full fence/reclaim;
- unimplemented distributed writable sharing cannot be enabled by a flag;
- read-only sharing requires explicit provider support;
- security evidence surface contains no capability/region authority token.

Final Phase 10 qualification:

```text
dotnet restore SingNextOS.slnx --force --no-cache
  PASS — 26 projects restored from a forced, uncached restore

focused CXL security/SecureCompute/fabric/authority/planner tests
  PASS — 57/57

dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
  PASS — 829/829 total (historical Phase 10 qualification snapshot; superseded by Phase 14's 844/844 full run)
    699 SingPlus.Tests
     60 SingPlus.Platform.HybridCpu.Tests
     58 HybridCPU_NeutralRuntime.Tests
     12 HybridCpu_ExecutableAdapter.Tests
```

## Skipped external scope

Physical IDE, device authentication, key management, TEE integration and FPGA
claims require the hardware/specification environment explicitly excluded by
the user. They are not represented as completed evidence.
