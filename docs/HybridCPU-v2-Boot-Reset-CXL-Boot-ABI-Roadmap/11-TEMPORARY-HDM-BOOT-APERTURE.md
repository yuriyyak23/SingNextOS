# Temporary HDM Boot Aperture

## 1. Purpose

Expose a small part of one CXL Type-3 persistent capacity to CPU loads long enough to read canonical boot metadata/payload, without turning that mapping into long-lived OS authority.

## 2. Address-space contract

The platform profile reserves one or more HPA ranges of type `FirmwareCxlBootAperture`. The absolute address is **not** part of cross-platform Boot ABI. `HybridBootInfo` reports the actual range.

Reference simulator profile:

```text
aperture size: 256 MiB
minimum supported: 16 MiB
mapping granularity: backend-reported, at least boot-anchor support
cacheability: platform-defined normal memory semantics after decoder enable
OS memory-map type: FirmwareTemporaryCxlAperture (not usable RAM)
```

## 3. Mapping model

Do not model this as “write decoder 0”. A CXL route can require decoder programming at host bridge, switch upstream port and endpoint. The architecture models an **atomic mapping transaction**:

```csharp
public interface ITemporaryBootApertureManager
{
    TemporaryBootMapping MapSingleTarget(
        CxlEndpointHandle endpoint,
        CxlPersistentRange source,
        PlatformHpaRange aperture,
        BootMappingOptions options,
        Deadline deadline);

    void Remap(TemporaryBootMapping mapping,
               CxlPersistentRange source,
               Deadline deadline);

    void Unmap(TemporaryBootMapping mapping, Deadline deadline);
}
```

Backend owns decoder indices/register details.

## 4. v1 mapping restrictions

- exactly one target endpoint/logical device per mapping;
- no interleave across multiple Type-3 devices;
- source range must lie in persistent capacity;
- no overlap with volatile partition unless future manifest profile permits it;
- HPA aperture is fixed/reserved by platform, not selected from arbitrary RAM;
- all decoder hops must commit or operation fails and rolls back/invalidates partial state;
- prior warm-reset mappings are not adopted without revalidation.

## 5. Why no boot interleave

Interleave would require multiple endpoints to be healthy and consistently programmed before the OS starts; it enlarges Stage-0 topology logic and makes a single device loss fatal to metadata fetch. Boot metadata/payload performance does not justify that complexity. Runtime SingNextOS remains free to create interleaved CXL regions after provider admission.

## 6. Mapping descriptor

Firmware-private:

```c
struct TemporaryBootMappingV1 {
    uint64_t token;                 // random/epoch-qualified firmware handle
    uint64_t mappingGeneration;     // firmware-local stale-handle detection
    uint64_t hpaBase;
    uint64_t hpaBytes;
    uint64_t persistentOffset;
    uint64_t sourceBytes;
    uint32_t flags;
    uint32_t decoderHopCount;
};
```

`token` never becomes an OS capability. Final BootInfo omits usable control token and carries only descriptive evidence.

## 7. Endpoint decoder semantics

At the endpoint the mapping translates incoming HPA to DPA/persistent-capacity location. Upstream decoders route HPA toward the correct port. Stage-0 backend validates:

- target range/alignment/granularity;
- decoder availability;
- HPA range fits platform CXL window/CFMW equivalent;
- no conflicting enabled decoder;
- commit/enable completion;
- read-back state if hardware supports it.

Exact CXL revision/register sequence is backend-specific.

## 8. CFMW/platform windows

On QEMU/ACPI-like systems a CXL Fixed Memory Window bounds HPA space routable to CXL host bridges. The real backend must allocate boot aperture inside a firmware/platform-provided CXL host window. HybridCPU ABI does not require ACPI or CEDT; those are implementation inputs to `IPlatformCxlWindowProvider`.

## 9. Windowed payload loading

If component > aperture:

```text
for each window:
  Unmap/disable current mapping or quiesce accesses
  program same HPA aperture -> next persistent source range
  increment firmware mappingGeneration
  copy bytes to system RAM
```

No code executes from the window, so remapping cannot invalidate instruction fetch.

## 10. Cache/coherency rules

Stage-0/Stage-1 treat source as read-only. Before reprogramming a window:

- no outstanding CPU load may target old aperture;
- any implementation cache lines for the aperture are invalidated or mapping is configured uncached according to platform profile;
- read completion/fence is observed.

The roadmap does not claim CXL.cache. Type-3 CXL.mem normal host memory semantics are sufficient.

## 11. Handoff representation

`HybridBootInfo` carries:

```c
struct BootTemporaryMappingEvidenceV1 {
    uint64_t hpaBase;
    uint64_t hpaBytes;
    uint64_t persistentOffset;
    uint64_t mappingGeneration;   // diagnostics/stale detection only
    Guid     bootVolumeId;
    Guid     replicaId;
    uint32_t state;               // Enabled, Disabled, UnknownAfterHandoff
    uint32_t flags;               // EvidenceOnly, FirmwareOwned, MustNotUseAsRam
};
```

No decoder index, DPA or route is required in the public ABI. Optional privileged diagnostics can appear in a separate evidence TLV.

## 12. Authority/lifetime

Timeline:

```text
Stage0 Map -> firmware owns configuration authority
Stage1 Remap/read -> still firmware owns
Kernel entry -> mapping is temporary inherited machine state, NOT OS authority
Sing provider fresh discovery -> new provider binding/generation
OS requests firmware mapping release or independently disables/reprograms under provider ownership
Boot mapping invalidated -> token/generation dead
```

At no time is firmware token converted to RegionAuthority.

## 13. Failure handling

- no available platform aperture → candidate/target failure; local recovery may still boot;
- decoder unsupported → candidate unsuitable;
- partial programming failure → disable touched hops, mark path poisoned for this boot attempt;
- read fault after enable → fail candidate, do not retry forever;
- device removed → invalidate mapping immediately;
- stale token/remap generation → fail closed;
- overlapping decoder detected → do not overwrite unknown live mapping unless reset policy explicitly grants firmware ownership of that window.

## 14. Future interleave escape hatch

A future `BootMappingProfileV2` may define an N-way replica/striped boot source, but only with signed layout metadata, atomic multi-target failure semantics and firmware topology support. It must not change BootVolumeId semantics or handoff authority rule.
