# P15.2 — Proposed API contracts

The interfaces below are proposed semantic contracts; exact names may be adjusted once project boundaries compile, but authority direction MUST remain unchanged.

## 1. Platform descriptor

```csharp
public sealed record HybridPlatformDescriptorV1(
    uint Version,
    Guid PlatformId,
    BootPhysicalRange Rom,
    BootPhysicalRange BootRam,
    IReadOnlyList<BootPhysicalRange> SystemRam,
    IReadOnlyList<BootPciConfigRoot> PciRoots,
    BootPhysicalRange TemporaryCxlAperture,
    ulong RequiredPlatformFeatures,
    ulong ResetSequence);
```

Rules:

- descriptor contains platform facts, not runtime capabilities;
- ranges are checked, non-overlapping where required, and fixed-width;
- boot aperture MUST be outside ordinary allocatable RAM;
- descriptor is immutable for a boot epoch.

## 2. Narrow boot platform roles

```csharp
public interface IBootPciConfiguration
{
    BootIoResult<uint> Read32(BootPciAddress address, ushort offset);
    BootIoResult Write32(BootPciAddress address, ushort offset, uint value);
}

public interface IBootCxlTransport
{
    BootIoResult<BootCxlMailboxReply> Execute(
        BootCxlEndpoint endpoint,
        BootCxlMailboxRequest request,
        BootDeadline deadline);
}

public interface IBootPersistentCapacity
{
    BootIoResult Read(
        BootCxlEndpoint endpoint,
        ulong persistentOffset,
        Span<byte> destination,
        BootDeadline deadline);
}

public interface IBootTemporaryMapping
{
    BootIoResult<BootTemporaryMappingLease> Create(
        BootTemporaryMappingRequest request,
        BootDeadline deadline);

    BootIoResult DestroyOrQuarantine(
        BootTemporaryMappingLease lease,
        BootDeadline deadline);
}

public interface IBootProtectedState
{
    BootIoResult<ProtectedBootStateV1> Read();
    BootIoResult Commit(ProtectedBootStateTransitionV1 transition);
}

public interface IBootSignatureVerifier
{
    BootTrustDecision Verify(
        BootSignedObjectKind kind,
        ReadOnlySpan<byte> signedBytes,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> keyId);
}

public interface IBootLocalImageSource
{
    BootIoResult<BootLocalImageDescriptor> Query(BootLocalImageKind kind);
    BootIoResult Read(BootLocalImageDescriptor image, ulong offset, Span<byte> destination);
}

public interface IBootResetControl
{
    ResetReason ResetReason { get; }
    ulong ResetSequence { get; }
    void RequestReset(BootResetRequest request);
}
```

No role returns `OwnedRegion`, runtime device leases, provider generations, OS process identities or capability IDs.

## 3. Shared Boot Core decisions

```csharp
public sealed class BootSelector
{
    public BootSelectionResult Select(
        BootSelectionPolicy policy,
        IReadOnlyList<BootCandidate> candidates);
}

public sealed class VerifiedComponentLoader
{
    public BootLoadResult Load(
        IReadOnlyList<BootComponentSource> components,
        IBootPersistentCapacity source,
        IBootRamWriter destination,
        IBootHashProvider hashProvider);
}

public sealed class TemporaryApertureCoordinator
{
    public BootApertureResult OpenSingleTarget(
        BootCxlPath path,
        BootTemporaryMappingRequest request,
        IBootTemporaryMapping platform);
}
```

## 4. Capsule composition

```csharp
public sealed record CapsuleServices(
    HybridPlatformDescriptorV1 Platform,
    IBootPciConfiguration Pci,
    IBootCxlTransport Cxl,
    IBootPersistentCapacity PersistentCapacity,
    IBootTemporaryMapping TemporaryMapping,
    IBootProtectedState ProtectedState,
    IBootSignatureVerifier SignatureVerifier,
    IBootLocalImageSource LocalImages,
    IBootResetControl Reset,
    IBootDebugSink Debug);

public static class CapsuleEntryPoint
{
    public static BootCapsuleExit Run(
        in HybridPlatformDescriptorV1 platform,
        CapsuleServices services);
}
```

## 5. Kernel entry ABI

Move `KernelEntryAbiV1` from the current Stage1 model into the contracts package and freeze it as an explicit fixed-width ABI.

Minimum fields:

```text
AbiVersion
EntryFlags
HybridBootInfoAddress
HybridBootInfoLength
PlatformDescriptorAddress or zero
PlatformDescriptorLength or zero
ResetSequence
Reserved[]
```

The kernel MUST validate all ranges before dereference and MUST copy required evidence into kernel-owned memory before reclaiming boot scratch.

## 6. Runtime fresh-admission seam

Keep the existing `HybridBootInfoImporter` rule, but replace test-only seams with platform implementations:

```csharp
public interface IFreshCxlBootDiscovery
{
    IReadOnlyList<CxlEndpointId> EnumerateCurrentEndpoints();
}

public interface IFirmwareApertureRetirement
{
    FirmwareApertureDisposition ReleaseInvalidateOrQuarantine(ulong resetSequence);
}
```

Production implementations belong to the runtime HybridCPU provider path, not Boot Core.

## 7. Required result semantics

Every pre-kernel external operation returns a typed result, never an exception-based success contract:

```text
Success
Unsupported
Denied
Timeout
Malformed
StaleGeneration
LinkLost
AmbiguousEffect
Quarantined
HardwareFault
```

`AmbiguousEffect` MUST never be treated as `NotAccepted`; it requires quarantine or reset/recovery.
