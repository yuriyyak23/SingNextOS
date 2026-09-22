# P15.8 — Kernel handoff and fresh CXL admission

## Handoff rule

The Boot Capsule transfers evidence and loaded bytes. It does not transfer live runtime authority.

```text
Capsule CXL mapping
        ↓ evidence only
HybridBootInfo
        ↓
Kernel
        ↓ fresh validation
Runtime HybridCPU/CXL provider
        ↓
new device/fabric/memory generations
        ↓
RegionAuthority
```

## Kernel entry sequence

1. validate `KernelEntryAbiV1` version/reserved/ranges;
2. copy `HybridBootInfo` into kernel-owned RAM;
3. parse BootInfo using `HybridBootInfoCodec`;
4. require exactly one valid required security-evidence record;
5. reject misclassified physical/aperture records;
6. initialize minimal runtime platform provider;
7. perform fresh endpoint discovery or mandatory liveness/generation revalidation;
8. retire/invalidate/quarantine temporary aperture;
9. only then allow normal CXL provider binding/Region allocation.

## Freshness semantics

`fresh authority != blind rediscovery`.

A performance-optimized profile may reuse capsule topology evidence as a hint, but kernel/provider MUST revalidate at least:

- endpoint presence;
- device generation/liveness;
- reset sequence continuity;
- current decoder/fabric state or intentional replacement;
- current security/admission policy;
- no intervening rebind/reset.

The result is always a **new runtime authority generation**.

## `HybridBootInfoImporter` changes

The current importer is semantically close to final. P15 should:

- keep evidence-only enforcement;
- add production implementations of `IFreshCxlBootDiscovery` and `IFirmwareApertureRetirement`;
- bind importer invocation into actual kernel early boot;
- carry a typed selection/security snapshot sufficient for diagnostics and boot confirmation, without provider-private authority handles;
- ensure duplicate or unknown required records fail before external provider actions.

## Boot confirmation

The kernel/runtime must expose one trusted control-plane operation:

```text
ConfirmBoot(rollbackDomain, imageId, generation, bootNonce)
```

It is callable only after the configured health milestone. The operation updates protected state atomically and advances rollback floor. A mere successful kernel entry or process startup MUST NOT auto-confirm.

## Aperture failure after entry

If fresh CXL admission fails:

- boot aperture is never adopted as runtime memory;
- attempt release/invalidate;
- if closure is ambiguous, quarantine;
- continue from normal RAM if policy permits;
- otherwise enter local recovery/reboot path.
