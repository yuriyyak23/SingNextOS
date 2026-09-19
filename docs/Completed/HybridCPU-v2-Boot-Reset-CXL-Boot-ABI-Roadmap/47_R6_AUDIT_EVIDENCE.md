# R6 Independent Audit Evidence — Temporary Aperture Model

Baseline HEAD before R6: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty status recorded/preserved. Optional adapter plan absent. No destructive/remote Git, network, hardware, or QEMU action was used.

## Audit and remediation

- Geometry, minimum aperture, alignment, source capacity, reserved overlap, decoder availability, non-interleave and reverse compensation are runnable.
- Defect fixed: HPA and reserved-range end calculations could wrap, missing an overlap. All ends are now overflow-checked before comparison.
- Defect fixed: a second `Create` silently replaced the active mapping. It now returns `ActiveMappingExists`; replacement requires explicit destroy/reset.
- Defect fixed: an already-enabled decoder hop was not treated as a conflict. It now fails before programming.
- Defect/evidence mismatch fixed: prior evidence claimed read/link/timeout cleanup but the model had no read path. A bounded `Read` operation now validates subranges; injected timeout/link-loss/read-fault invalidates the mapping generation and leaves no live descriptor. Tests cover all three.
- Reset still invalidates mapping even if physical state might remain. Partial decoder commit is compensated in exact reverse order.

`TemporaryMapping` is firmware-model state only: it is not `OwnedRegion`, `RegionUse`, provider lease, capability or OS mapping. Firmware and mapping generations are distinct and are never compared to image/provider/region generations. Timeout/link/read fault invalidates evidence; it does not assert reclaim, release or closure.

## Command and result

`dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~TemporaryApertureModelTests" --verbosity minimal` — passed 9, failed 0.

Claim level: `ModelValidated`. Real HDM programming, platform aperture placement, DMA/IOMMU isolation, retained decoder cleanup and link behavior remain `FutureGatedRequiresHardware`; owner: platform CXL/firmware backend; missing prerequisite: qualified decoder transaction/resource/reset/isolation contract.

No HybridCPU core/ISE/ISA/compiler/register/fetch/pipeline/replay/memory-controller/retire/scheduler/runtime-legality/microarchitecture implementation changed.
