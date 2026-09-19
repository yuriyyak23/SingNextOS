# Phase 10 — Security, SecureCompute and Multi-host Gating

Status: **Complete for the software/model security and conservative multi-host
gate.** Hardware IDE/TEE/FPGA claims are skipped by user scope. See
[10_PHASE10_IMPLEMENTATION_EVIDENCE.md](10_PHASE10_IMPLEMENTATION_EVIDENCE.md).

## Goal

Integrate CXL security state and advanced sharing only after the authority, lifecycle, Type-3/Type-2, hardware and reconfiguration foundations are working. Security evidence strengthens provider admission; it never becomes region/device authority.

## Security evidence model

Introduce or extend evidence records for facts such as:

```text
LinkEncryptionAvailable
IdeEnabled
DeviceAuthenticated
DeviceSecurityState
FirmwareMeasurementState
TrustedExecutionCapability
SecurityPolicyVersion
EvidenceGeneration
```

Exact fields must follow the mechanisms actually supported by the platform/device/spec revision.

## Authority separation

The invariant is absolute:

```text
security evidence != authority
```

A trusted/authenticated CXL device still requires:

- a valid `DeviceLease`/service authority;
- exact `RegionAuthority`;
- compatible `RegionUse`;
- current platform/fabric/mapping bindings;
- lifecycle admission.

Conversely, valid region authority does not mean a device satisfies a SecureCompute policy.

## CXL IDE and transport security

Where supported, CXL IDE/security state should be consumed as provider/platform evidence that transport/link protection requirements are currently satisfied. Do not claim IDE supplies ownership, publication, replay, DMA authorization or application capability semantics.

Evidence must be invalidated or regenerated when its underlying security session/device/link state changes.

## SecureCompute integration

SecureCompute admission may require a predicate over evidence, for example:

```text
required trust policy
+ device/service identity
+ current security evidence
+ workload policy
-> readiness result
```

The readiness result is still not a memory binding. Memory/device bindings are materialized only through the normal authority path.

## Multi-host/shared-memory gate

Do not enable writable multi-host shared regions merely because CXL supports fabric/shared-memory mechanisms.

Before writable multi-host sharing, SingNextOS needs an explicit distributed ownership/use contract covering at least:

- host/principal identity;
- ownership transfer/arbitration;
- writer exclusion or synchronization semantics;
- mutation epoch propagation/coordination;
- failure/restart/fencing;
- reclaim when one host disappears;
- publication and visibility across hosts;
- trusted coordinator/fabric-manager authority boundaries.

Until that exists, support one of:

- single-host ownership with rebinding between hosts after reclaim;
- read-only sharing under an explicit provider contract;
- staging/copy between host-owned regions.

## Peer-to-peer and multi-host security

P2P/multi-host routes require both access authority and platform isolation. Security evidence may reject an otherwise-authorized route, but it may not create one.

## Negative tests

- authenticated device without region authority -> access denied;
- authorized region with failed required IDE/security evidence -> secure-required operation denied;
- security session reset -> prior evidence generation rejected;
- multi-host writable sharing requested without distributed ownership contract -> reject;
- FM assigns memory to second host before first host reclaim completes -> fail closed;
- security evidence remains cached across device reset -> stale evidence test failure.

## Acceptance criteria

- SecureCompute consumes CXL evidence through existing evidence taxonomy/separation;
- evidence generation is tied to actual security/session state;
- no security token appears in `RegionAuthority` as a substitute for ownership/access rights;
- multi-host writable memory remains explicitly disabled until the distributed authority protocol exists and is tested;
- single-host and staged paths remain functional when advanced security features are unavailable, subject to caller policy.

## External blockers

- exact CXL security/TEE mechanisms available in the targeted CXL revision and hardware;
- device/vendor support for authentication/measurement interfaces;
- platform key management and firmware policy;
- a separately reviewed distributed ownership/fencing design before writable multi-host shared regions.
