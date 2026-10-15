# v3 Delta and Go/No-Go

## What changed from the previous updated roadmap

1. Replaced tentative baseline statements with concrete current-master paths/status for Boot.Contracts, importer, Region/CXL/reset owners and host-debug boot.
2. Removed mandatory physical relocation of Boot.Contracts.
3. Corrected architecture-policy work to reuse existing `Layer.BootContracts`.
4. Made production implementations of fresh discovery and aperture retirement explicit blockers.
5. Clarified that provider generations remain owned by the current provider lifecycle, not P15 importer.
6. Moved capsule admission/memory-profile work before full capsule composition.
7. Added allocation-safe BootInfo writer requirement for a no-heap capsule.
8. Recorded actual HybridCPU SHA drift and current external gate states.
9. Mapped P15 external gates onto the existing `PlatformExternalGateTable` where possible.
10. Preserved host-debug `SingPlus.Boot` as a separate existing path.

## GO/NO-GO

### GO for roadmap-driven SingNextOS refactor

**GO**, with `P15-00` mandatory as the first PR. The v3 plan has one owner per authority responsibility, a non-cyclic project graph, explicit integration seams, explicit external gates, and a revertible sequence.

### GO for ISE Direct SingNext implementation

**NO-GO on the currently recorded external qualification baseline** until all of the following are closed:

- current HybridCPU master is requalified or an explicitly selected qualified SHA is used;
- `HC-AOT-IMAGE`, `HC-ENTRY`, `HC-BOOTSTRAP`, `HC-RESET-ROM`, and `HC-ISE-CXL` are satisfied for the chosen profile;
- production fresh-discovery and aperture-retirement adapters are wired;
- capsule admission/memory profile and BootInfo writer are qualified;
- P15-13 executes the end-to-end path.

### GO for real hardware Direct SingNext

**NO-GO** until P15-14 provides direct named-platform evidence for PCI/CXL/HDM, DMA/IOMMU, reset, ordering/cache visibility, retirement and protected-state durability. Model/adapter/ISE evidence cannot substitute for these gates.
