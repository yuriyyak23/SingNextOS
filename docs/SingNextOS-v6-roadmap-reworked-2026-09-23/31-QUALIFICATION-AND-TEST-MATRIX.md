# Qualification and test matrix

Для research/dev сначала применять [42](42-RESEARCH-DEV-VERIFICATION.md). Phase exit и ProductionQualified — отдельные checkpoints, не обязательный полный campaign после каждой правки.

| Contour | Applicable behavioral evidence | Дополнительный qualification exit |
|---|---|---|
| P01/P04 memory/DMA | access/publication, mutable generations, revoke/ABA, alias/refusal, fault/closure/retry | Named provider conformance и relevant memory/translation litmus; physical guarantees требуют hardware |
| P05 refinement | accepted/rejected semantic traces, canonical/mandatory semantics, actual owner-writer projection, mutation negatives | Exact obligations/guarantees/dependency tuple; formal method для конкретного доказуемого свойства |
| P03/P10 accounting/failure | conservation, settlement/reclaim, quarantine, overlap, reset/transport-loss consequences | Contention/fault evidence для enforced bounds; reservation не minimum service |
| P08 restart | fresh admission/legality, duplicate effects, provider reset, budget continuity | Per-provider restart qualification; stateful resume отдельный gate |
| P02/P09/P06 | persistence/crash, trust freshness, protected-flow semantics соответственно | Named physical persistence/security contour; software tests не physical proof |
| P07/P12 | actual advisory/measurement consumer, stale input, attribution | Perf/energy campaigns только для заявленных properties; enforcement отдельно |
| P11 | leases/partition/ownership/refinement/fault models | Relevant single-host exits и real fabric qualification; FutureGated |
| PCL / static verifier compatibility | verifier/parser/forwarding/canonical source compatibility и malformed proof negatives | Static evidence never execution permission; no runtime/provider authority dependency |
| A0 architecture policy | actual project classification/reference consumers and forbidden-edge negatives | Full replacement policy is separate from non-Boot verifier compatibility; preserve user-deleted guard and Boot exclusion |

Race set выбирается по consumed mutable owners: capability revoke, process/session close, Region ABA, budget drift, provider restart/reset, translation invalidation, duplicate/lost receipt, cancel/completion, visibility/publication, settlement/reclaim. N/A объясняется реальными dependencies; не запускать нерелевантный matrix ради полноты строки.

Claim levels не линейная лестница. ModelOnly доказывает модель; StaticAdmission — exact static acceptance; RuntimeEnforced — live owner transitions/rejections; ExecutableAdapter — concrete adapter behavior, не physical properties. EnforcedUpperBound и GuaranteedReservation требуют собственных enforcement/service доказательств. ProductionQualified требует named deployment tuple, relevant hardware/fault/security/performance campaign, reproducible package/toolchain integrity и recovery plan. FutureGated сохраняет unsupported contour выключенным. Всё новое v6 остаётся OFF до qualification exact deployment.
