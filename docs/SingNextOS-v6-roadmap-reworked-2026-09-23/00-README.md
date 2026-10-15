# SingNextOS v6 — normative plan и proof/evidence

Навигация актуализирована 2026-10-15. Дата каталога — происхождение плана, не current frozen tuple.

**Для реализации начать с [modernize/refactoring](../SingNextOS-v6-modernize/00-README.md).** Там единственная текущая очередь, порядок и lean dev validation. Этот пакет сохраняет нормативные requirements, contracts, ADR и [qualification/evidence navigation](45-PROOF-EVIDENCE-INDEX.md); читать лишь применимые файлы.

Owners/invariants: [02](02-AUTHORITY-OWNER-MAP.md), [04](04-NORMATIVE-INVARIANTS.md). Gates [05](05-FEATURE-GATES-AND-CLAIMS.md), contracts [07](07-GLOBAL-CONTRACT-EVOLUTION.md)/[30](30-CROSS-PROJECT-CONTRACTS.md), DAG [03](03-PHASE-DAG-AND-MIGRATION.md). Phase10–23 — target scope/exit criteria; исторические structured verdicts вынесены в artifacts/v6/iteration-20261007-documentation-context-cleanup/history и не являются текущей очередью. Current state [41](41-CURRENT-STATE-AND-OPEN-SLICES.md) перенаправляет в modernize.

Один fact — один owner. SingNext authorization/provider admission/refinement/current HybridCPU legality независимы. Receipt/trace/identity/proof не permission. Completion/visibility/publication/persistence/durability/release различны. V1 additive; gates OFF; SingPlus.Boot/Samples исключены (не automatically distinct SingNext.Boot); Java-only skipped; ISA NONE.

История отделена в artifacts/v6/iteration-20261004-roadmap-dev-workflow/history и artifacts/v6/iteration-20261006-roadmap-modernize-split/history; original reports/failed runs сохранены. MANIFEST/SHA256SUMS — superseded snapshot, не dev requirement и не current integrity attestation. Strict freeze требует explicit refresh и сохраняет честные exits.

[Audit disposition](../SingNextOS-v6-modernize/05-EXTERNAL-AUDIT-DISPOSITION.md) отделяет actual implementation gaps от устаревших missing-code assertions и proof-only рекомендаций. Они не вводят вторую очередь или обязательную formal/hardware campaign для каждого managed dev fix.

Выборочные additive consumer contracts: [ADR-032](ADR-032-ORIGINAL-NATIVE-DMA-COPY.md) — original software DMA; [ADR-035](ADR-035-ORIGINAL-VIRTUAL-IO-BACKING.md) — original Virtual-I/O backing; [ADR-052](ADR-052-ORIGINAL-COMPLETED-PROGRAM-RESULT.md) — original Completed result; [ADR-054](ADR-054-ORIGINAL-SOFTWARE-DSC1-BACKING.md) — software DSC1 backing/adoption. Current package tuple и remaining consumers находятся только в modernize/01; версии в прежних ADR/report относятся к их recorded inputs, не являются current Version requirement. Внешнее исполнение guest HCEXE не означает перенос всей SingNextOS в HybridCPU binary.


Dev continuation: root routing/local publication/pins/owner-interlock consumers во многом реализованы; remaining status только в modernize/01. Не использовать phase targets или historical verdicts как absent-code checklist. Mandatory full formal/model-check/proof matrix/manifest/hardware campaigns относятся к applicable qualification claim, не к каждой dev итерации; changed-consumer security/canonical/legality и necessary race checks сохраняются.
