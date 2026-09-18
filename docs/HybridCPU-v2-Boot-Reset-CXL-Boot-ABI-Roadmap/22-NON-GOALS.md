# 22 — Non-Goals

This roadmap defines the minimum product architecture required to reset HybridCPU-v2, locate and authenticate a SingNextOS installation on CXL Type-3 persistent memory, transfer control safely, and preserve SingNextOS authority boundaries. The following are intentionally outside v1.

## Firmware and boot environment

- A UEFI/BIOS compatibility implementation, ACPI implementation, shell, filesystem suite, network stack, general driver framework, or long-lived firmware runtime.
- A complete SingNextOS CXL provider in ROM. Stage-0 implements only bounded boot discovery/mapping operations.
- Direct reset-vector execution from CXL memory.
- Production kernel execute-in-place from CXL persistent memory. V1 copies verified Stage-1/kernel/services to normal RAM.
- Arbitrary filesystem discovery on boot media. The boot volume uses a compact fixed/versioned metadata format and signed component descriptors.
- Making CXL LSA the canonical boot database. LSA is an optional locator/cache only.

## Identity and authority

- `deviceId == 0`, enumeration order, PCI BDF, DSN, port ID, HDM decoder index, HPA, DPA, MLD ID or fabric route as a SingNext installation identity.
- Treating DSN or any hardware identifier as a capability.
- Letting BootInfo contain a live `OwnedRegion`, provider capability, reusable decoder handle, or fabric authority.
- Reusing a firmware mapping as SingNextOS authority merely because the HPA/decoder programming still exists.
- Unifying `ImageGeneration`, firmware mapping generation, provider generation, region generation, and fabric generation into one counter.

## ISA and CPU execution

- CXL-specific load/store, mailbox, discovery, decoder-management or boot instructions.
- A new privilege architecture solely for CXL boot. The `PlatformFirmwareExecutionDomain` is an implementation/security boundary built from existing privileged platform mechanisms, not a new instruction-set promise.
- Redesigning the existing VLIW bundle, pipeline, replay, retire, scheduler, or compiler semantics.
- Making QEMU a requirement for HybridCPU instruction execution. QEMU is initially a CXL protocol/model validator only.

## CXL runtime scope

- Replacing SingNextOS `OwnedRegion`, `RegionUse`, provider or fabric-manager contracts.
- Exposing provider-private DPA/HDM/interleave/fabric routing into normal application APIs.
- General-purpose CXL fabric orchestration in firmware.
- Runtime hotplug/reconfiguration policy beyond the handoff rule that SingNextOS performs fresh provider discovery/admission.
- Booting a v1 image striped/interleaved across multiple CXL devices. Replication/failover is supported; boot-payload interleave is not.
- Treating Direct Coherent Write or other future-gated SingNextOS provider features as a boot prerequisite.

## Security and operations

- A universal PKI, certificate transparency system or remote attestation service. V1 defines an algorithm-agile local trust root/trust-store interface and signed manifests.
- Network recovery inside immutable ROM. A signed local recovery environment may later implement network/service recovery.
- A specific TPM/secure-element/fuse vendor contract. The semantic requirements are fixed; hardware binding remains platform-specific.
- Claiming CXL IDE alone makes a boot device trusted. Media signature/rollback verification remains mandatory.
- Preserving writable logs as a security authority. Diagnostic logs are evidence only and may be lossy.

## Media/update scope

- GPT compatibility as a requirement. A future tooling layer may expose or wrap GPT, but Stage-0 does not need a general partition parser.
- Device-local “active slot” metadata as the authoritative A/B selector. Protected platform `BootState` owns trial/confirmed policy.
- Updating immutable ROM through the ordinary OS A/B media mechanism. ROM/firmware update requires a separate platform process and signing policy.
- Automatic clone/replica conflict resolution when two equally eligible same-generation manifests contradict each other. V1 fails the logical target as ambiguous and falls back safely.

## Performance

- Fastest possible cold boot at the cost of broader immutable TCB, unbounded probing, skipped verification, or authority reuse.
- Scanning full persistent capacities. Stage-0 reads only bounded locator/header/manifest regions and selected payloads.
- Mandatory parallel enumeration. Parallel probing is a later optimization if determinism and bounded failure behavior are preserved.

Any proposal that moves one of these items into scope must state which invariant changes, update the decision record, add negative/security tests, and identify whether it changes only a backend or the cross-project ABI.
