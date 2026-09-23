# v6 Non-Goals and Open Questions

## Explicit non-goals

- CHERI/tagged-pointer conversion of SingNextOS;
- OS capabilities or Region IDs in ISA registers/instructions;
- a second capability, Region, budget or publication ledger;
- universal coherent zero-copy guarantee;
- universal hard-realtime theorem;
- compiler facts as runtime authority;
- attestation tokens as capabilities;
- durable serialization of live authority;
- distributed consensus on every local authority operation;
- topology IDs in application/SIP authority ABI;
- claiming ProductionSecure from model/emulator evidence;
- replacing ordinary SIP/staged paths before the optimized contour is qualified.

## Open questions to resolve during P00/P01

1. Which exact HybridCPU memory-ordering semantics are executable today for ordinary CPU, MatrixTile, DSC and L7 paths, and where do they differ?
2. What atomic widths/scopes can each selected provider enforce, including CXL/device paths?
3. Which persistence primitive can produce a meaningful durable confirmation on the first target platform/model?
4. What IOMMU/SVA-like backend is the first executable translation contour?
5. Which timing quantities are enforceable vs measured only on HybridCPU ISE/runtime?
6. What safe points already exist in MatrixTile/DSC/L7, and which require runtime changes?
7. What minimum IFC lattice gives useful value without infecting every API?
8. Which attestation producer classes can be distinguished honestly: model, software, firmware-rooted, hardware-rooted?
9. How should poison/subrange damage interact with existing Region subrange/borrow semantics?
10. What lease expiry model avoids depending on synchronized wall clocks for multi-host safety?
11. Which energy measurements are deterministic/available enough to support accounting?
12. What smallest proof language can validate alias/footprint/order properties without making the compiler a TCB authority?

## First hardware claim boundary

No v6 hardware claim should exceed the specifically named CPU/IOMMU/CXL/device/firmware platform tested. Model/ISE/adapters can qualify semantics and failure handling but do not substitute for physical ordering, persistence, DMA isolation, attestation or RAS evidence.
