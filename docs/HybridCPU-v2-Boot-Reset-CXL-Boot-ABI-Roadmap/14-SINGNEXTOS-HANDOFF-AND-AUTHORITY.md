# SingNextOS Handoff and Authority

## 1. Central invariant

**Firmware discovers and boots; SingNextOS authorizes runtime use.** There is no authority inheritance from Stage-0/Stage-1 CXL state.

This directly follows the existing SingNextOS architecture: discovery/evidence are below capability authority; CXL provider-private HPA/DPA/decoder/fabric identities are materialization details; `OwnedRegion` remains the user-facing region authority.

## 2. Handoff transition

```text
Firmware discovers physical device
Firmware reads hardware identity evidence
Firmware maps temporary boot aperture
Firmware verifies signed image and copies it to RAM
Firmware passes HybridBootInfo
                         |
                         | KERNEL ENTRY
                         v
SingNextOS validates BootInfo structure/provenance
SingNextOS starts its own PCI/CXL discovery
SingNextOS provider observes current endpoint/topology
SingNextOS creates fresh provider-private binding generation
SingNextOS region layer creates/adopts normal region authority when needed
SingNextOS tears down/replaces firmware aperture
```

The boot image already lives in local/system RAM, so provider takeover can fail safely without causing instruction-fetch loss. If the OS requires CXL root data later, it handles provider failure through its normal recovery policy.

## 3. Evidence versus authority

### Evidence accepted from firmware

- which logical `BootVolumeId`/`ImageId` was verified;
- image generation and manifest digest;
- physical endpoint facts (DSN/BDF/vendor/device) observed during boot;
- temporary HPA range used;
- verification/security mode;
- reset reason and failure log.

### Authority that firmware cannot provide

- `RegionAuthority`;
- `OwnedRegion`/`OwnedBuffer`;
- `RegionUse`;
- live device lease/resource authority;
- `FabricBindingGeneration` accepted by SingNextOS;
- runtime memory-provider lease;
- user/SIP capability.

## 4. Fresh discovery sequence

Pseudo-code:

```csharp
HybridBootInfo hbi = HybridBootInfoParser.ParseAndValidate(ptr, size);
ReserveEarlyMemory(hbi.MemoryMap);
RecordBootProvenance(hbi.Selection, hbi.SecurityState);

// Evidence can make diagnostics more precise, not skip discovery.
var discovered = cxlDiscoveryProvider.DiscoverFresh();
var bootSourceMatch = CorrelateEvidenceOnly(hbi.BootDeviceEvidence, discovered);

var admission = cxlMemoryProvider.AdmitCurrentTopology(discovered);
if (!admission.Accepted)
{
    // OS can keep running from RAM; mark CXL source unavailable/stale.
    QuarantineFirmwareBootAperture();
    ContinueOrEnterOsRecoveryPolicy();
}
else
{
    // New provider-private generation, independent from firmware generation.
    var providerBinding = admission.Binding;
    RevalidateOrRecreateMappings(providerBinding);
    ReleaseFirmwareBootAperture();
}
```

## 5. `OwnedRegion` relation to boot volume

A boot volume is persistent storage containing boot artifacts. It is **not automatically an `OwnedRegion` representing the entire Type-3 capacity**.

If SingNextOS later wants to expose/mount persistent memory:

1. CXL memory provider discovers usable capacity;
2. placement/backing policy identifies a provider-private persistent range;
3. normal region allocation/admission creates `OwnedRegion`/appropriate persistent storage object according to existing OS semantics;
4. ownership/generation/lifetime rules apply normally.

BootVolumeId may be used by a privileged boot-volume service to locate persistent boot metadata but must not be injected into `RegionAuthority` as a capability token.

## 6. Generation model

Keep generations distinct:

```text
ImageGeneration                    signed boot release ordering
HBV metadataSequence               crash-safe media metadata ordering
LSA locatorGeneration              locator freshness hint
FirmwareBootMappingGeneration      Stage0/1 stale mapping detector
Sing DeviceGeneration              runtime device validity
Sing FabricBindingGeneration       runtime topology binding validity
Sing PlatformMappingGeneration     runtime mapping validity
RegionGeneration / MutationEpoch   OS memory authority/lifetime
```

No equality relationship is implied among them. BootInfo labels each with its namespace/type.

## 7. Matching firmware evidence to fresh discovery

Correlation MAY use DSN/BDF/topology digest/BootVolume header to explain “this appears to be the same endpoint”, but correlation cannot bypass provider checks.

Preferred logic:

- exact DSN + fresh endpoint capability = strong physical correlation evidence;
- changed BDF with same DSN = normal topology movement;
- changed DSN but same signed BootVolume/Replica after replacement = acceptable logical continuity;
- same DSN but different BootVolume = physical device media reprovisioning; trust signed current media/policy, not old evidence;
- no match = record boot source no longer reachable; OS still runs from RAM.

## 8. Firmware mapping teardown protocol

Default v1:

```text
OS marks aperture reserved/non-allocatable
 -> fresh CXL discovery and provider admission
 -> provider quiesces CPU accesses to boot aperture
 -> platform disables firmware-owned decoder path
 -> provider creates desired runtime decoder/window(s)
 -> increment provider mapping/fabric generations
 -> memory manager exposes only newly admitted ranges
```

Alternative “adopt exact decoder state” is future-gated. It would require complete read-back/revalidation and an explicit authority transfer primitive; v1 intentionally avoids it.

## 9. Post-CXL operability consistency

The existing post-CXL roadmap assumes:

- stale generations fail closed;
- reset/reconfiguration do not preserve live external authority silently;
- provider mappings/device leases are beneath normal capability authority.

Boot obeys the same rule: reset starts a new firmware mapping epoch, and kernel startup starts new provider generations. This is not duplicate CXL logic; it is a bootstrap bridge into the already-defined lifecycle.

## 10. Fabric Manager interaction

Fabric Manager may be needed before boot to assign a logical device/path to the host. That activity is external platform provisioning. Stage-0 sees only the resulting endpoint/path and optional platform boot directory evidence.

After SingNextOS starts, its `ICxlFabricProvider`/FM integration becomes authoritative for runtime reconfiguration. Firmware BootInfo route evidence cannot prevent rebinding or grant access after path change.

## 11. Persistent boot-volume service boundary

A future privileged `BootVolumeMaintenanceService` in SingNextOS may:

- locate replicas by BootVolumeId;
- update inactive signed slot;
- request persistence fence;
- update optional HBLR;
- call protected platform `SetTrial`/`ConfirmBoot`.

It still delegates CXL memory access to provider-approved mappings and region/storage authority. It must not open raw decoder/MMIO access to the updater.

## 12. Failure after kernel entry but before provider admission

If kernel is alive in RAM but CXL fresh discovery fails:

- do not keep using firmware aperture as a fallback runtime region;
- reserve/quarantine aperture;
- preserve boot provenance/log;
- if root services were fully copied, continue in degraded mode where policy permits;
- if essential persistent state remains on CXL, enter SingNextOS recovery/reboot policy;
- repeated failures can cause next boot to choose secondary target/local recovery.

This avoids converting “boot succeeded once” into permanent mapping authority.
