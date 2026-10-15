# v6 Non-Goals and Open Questions

## Explicit non-goals

- universal new authority ledger;
- TemporalAuthority, PowerAuthority, ProofAuthority, TopologyAuthority, TranslationAuthority;
- distributed capability database;
- OS capabilities or Region handles in ISA instructions/register semantics;
- compiler facts as runtime legality or permission;
- topology IDs in application authority ABI;
- generic shared-coherent mutable memory claim before qualification;
- generic hard-real-time/WCET claim;
- generic PMem/CXL durability claim independent of persistence domain;
- ProductionSecure based on emulator/model evidence;
- treating provider loss/cancel/checkpoint as closure/authority persistence;
- theorem prover as a prerequisite for the first vertical;
- ISA extension without a separate enforcement-gap ADR.

## Contour-selection questions

These are design/qualification questions to apply only when selecting that contour. They do not assert missing code or require reopening completed cuts; current tasks are in [modernize backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md).

1. What exact memory-order/atomic guarantees are executable today for CPU, MatrixTile, DSC and L7, including visibility at retire/commit?
2. Which direct coherent output modes, if any, have an executable fence/visibility/publication chain?
3. Does the selected consumer bind the actual ProcessRegistry/address-space incarnation and final checks? Reuse established owners.
4. What provider evidence proves translation invalidation completion for each selected adapter?
5. Which page-fault paths can occur after device submit and how do they revalidate RegionUse?
6. Which temporal quantities are enforceable vs measured only?
7. Which provider contours can preempt, drain, capture and resume independently?
8. Does the selected provider damage/closure consumer preserve existing RegionAuthority subrange consequences and unclosed-writer pins?
9. Which physical trust producer/device stack is available for P09 qualification?
10. What persistence domains are available for P02 hardware qualification?
11. Which energy counters can be attributed and enforced per operation vs only observed globally?
12. What minimal PCL proof classes reduce runtime cost enough to justify verifier TCB?
13. What remote lease expiry mechanism avoids unsafe wall-clock dependence for P11?
14. Which facts truly require consensus during multi-host ownership transfer?

## First hardware claim boundary

No v6 hardware claim exceeds the named CPU/IOMMU/CXL/device/firmware platform actually tested. Emulator and adapter evidence can qualify software semantics and failure handling; it cannot substitute for physical ordering, DMA isolation, persistence, attestation, ECC/poison behavior or production RAS evidence.
