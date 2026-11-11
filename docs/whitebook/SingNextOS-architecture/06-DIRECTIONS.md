# Направления развития и ограничения

Карта ниже — архитектурные target semantics, не второй backlog. Current consumer выбирается из modernize/01 и actual source. Phase numbers сохраняют traceability, dependency order не строго числовой.

| Направление | Семантика | Предел claim |
|---|---|---|
| P01 memory | Ownership/order/visibility/use/reclaim | General shared/unknown отдельно от bounded cleanup |
| P02 durability | Persistence/crash/durable output | Model не physical durability; B1 открыт |
| P03 temporal/resources | Upper bound/reserve/settle | Accounting не hard RT/minimum service |
| P04 translation/DMA | Mapping/grant/generation/effect | Software adoption не physical qualification |
| P05 refinement | Compatibility/semantic trace checker | Proof не permission/legality |
| P06 IFC | Protected label flow | Named protected contour |
| P07 locality | Advisory placement/data motion | Topology/cost не authority |
| P08 preemption | Capture/resume/restart | Нужны actual staged predecessor/acquisition |
| P09 trust | Attestation/freshness/device identity | ProductionSecure отсутствует |
| P10 RAS | Failure domains/containment/reclaim | Generic failure не machine fault-kind |
| P11 multi-host | Remote lease/closure | Positive remote isolation Missing/ExternalBlocked |
| P12 energy | Measurement/enforceable budget | Measurement не reservation/PowerAuthority |
| PCL | Proof-carrying lowering | Optimization, не permission |

P01/C0 предшествуют translation/DMA; P03 и applicable P04 нужны P08. P05 cross-cutting, не монолитный барьер. P02 применяется к persistent contour. P06/P07/P09/P12 условны по actual need. P11 требует single-host closure/failure semantics и собственных remote contracts.

## Remaining properties

- B1 Missing/ExternalBlocked: trusted cold original continuation после all-fact-loss/reboot.
- B2 Partial: unknown/malformed histories, exact provider/source/acceptance/no-effect/closure и independent users.
- B3 Partial: artifact/backing/independent users; metadata не снимает pins.
- B4 Partial: concrete cross-owner consequences; general atomicity/crash durability не заявляются.
- B5 Partial: remaining producing client/adoption/backing needs.
- B6: later restart/settlement/pool/coherent consumers требуют actual acquisition need.

DestroyAsync original domain/close → invocation correlation — current Partial consumer. Уже implemented original request/result custody, nested/control correlation и model Revoke не переделываются без drift. P08 DMA restart пока не ready: cross-owner copy не required staged predecessor.

## Claim vocabulary

ModelOnly, StaticAdmission, RuntimeEnforced, ExecutableAdapter, EnforcedUpperBound, GuaranteedReservation, ProductionQualified и FutureGated относятся к отдельным свойствам/contours. Это не автоматическая лестница promotion. Reservation не гарантирует minimum service; compiler proof не legality; emulator не physical evidence.

Gates OFF, no ProductionQualified, Java-only skipped, ISA NONE. Whole-v6 completion не выводится из bounded pass.

## Нормативная навигация

- [Phase DAG](../../SingNextOS-v6-roadmap-reworked-2026-09-23/03-PHASE-DAG-AND-MIGRATION.md)
- [Gates/claims](../../SingNextOS-v6-roadmap-reworked-2026-09-23/05-FEATURE-GATES-AND-CLAIMS.md)
- [Memory](../../SingNextOS-v6-roadmap-reworked-2026-09-23/10-P01-UNIFIED-MEMORY-SEMANTICS.md)
- [Durability](../../SingNextOS-v6-roadmap-reworked-2026-09-23/11-P02-DURABILITY-PERSISTENT-MEMORY.md)
- [Temporal](../../SingNextOS-v6-roadmap-reworked-2026-09-23/12-P03-TEMPORAL-EXECUTION-CONTRACTS.md)
- [DMA](../../SingNextOS-v6-roadmap-reworked-2026-09-23/13-P04-TRANSLATION-DMA-AUTHORITY.md)
- [Refinement](../../SingNextOS-v6-roadmap-reworked-2026-09-23/14-P05-MACHINE-CHECKABLE-REFINEMENT.md)
- [IFC](../../SingNextOS-v6-roadmap-reworked-2026-09-23/15-P06-INFORMATION-FLOW-CONTROL.md)
- [Locality](../../SingNextOS-v6-roadmap-reworked-2026-09-23/16-P07-LOCALITY-DATA-MOTION.md)
- [Preemption](../../SingNextOS-v6-roadmap-reworked-2026-09-23/17-P08-PREEMPTIBLE-HETEROGENEOUS-EXECUTION.md)
- [Trust](../../SingNextOS-v6-roadmap-reworked-2026-09-23/18-P09-DEVICE-TRUST-ATTESTATION.md)
- [RAS](../../SingNextOS-v6-roadmap-reworked-2026-09-23/19-P10-RAS-FAILURE-DOMAINS.md)
- [Multi-host](../../SingNextOS-v6-roadmap-reworked-2026-09-23/20-P11-MULTIHOST-AUTHORITY-LEASES.md)
- [Energy](../../SingNextOS-v6-roadmap-reworked-2026-09-23/21-P12-ENERGY-THERMAL-RESOURCES.md)
- [Lowering](../../SingNextOS-v6-roadmap-reworked-2026-09-23/22-PROOF-CARRYING-LOWERING.md)
