# Proof/evidence и qualification

Этот каталог хранит normative architecture/contracts/phase exits и qualification navigation. Рабочая очередь implementation находится в [modernize](../SingNextOS-v6-modernize/00-README.md); dev verification — [единая policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md).

- Owners/invariants: [02](02-AUTHORITY-OWNER-MAP.md), [04](04-NORMATIVE-INVARIANTS.md).
- Contracts/compatibility: [07](07-GLOBAL-CONTRACT-EVOLUTION.md), [30](30-CROSS-PROJECT-CONTRACTS.md), relevant ADR.
- Claims/gates: [05](05-FEATURE-GATES-AND-CLAIMS.md).
- Qualification: [31 matrix](31-QUALIFICATION-AND-TEST-MATRIX.md), [32 traceability](32-TRACEABILITY-MATRIX.md), [QV1](23-FIRST-QUALIFICATION-VERTICAL.md).
- Strict baseline: [06](06-BASELINE-FREEZE.md). MANIFEST/SHA256SUMS — historical, требуют explicit refresh перед strict freeze; не dev checklist.
- Phase10–23: target scope и stronger exit requirements, не live missing-code inventory.

Full campaigns выбираются для applicable qualification/promotion/claim или concrete unresolved risk; security/dependency/toolchain change требует affected integrity/consumer проверки, а не всех campaigns автоматически. Hardware/model/perf набор выбирается по заявляемому property; Java-only исключённые checks явно skipped и не считаются passed. Strict scripts/exit semantics не ослаблены.

Один substantive runtime блок — один короткий report, без дублирования журналов в phase files. Отдельный audit JSON/collector/proof matrix нужен только потребляющему validator или applicable qualification workflow, не каждой dev правке. Source attribution сохраняет relevant dirty inputs и actual tuple; canonical/security digests не сокращать. Ordinary doc SHA не нужен в dev. Models/fakes/emulator/compiler/unit tests не physical proof. Gates OFF; ProductionQualified только requirement-by-requirement exact deployment evidence.

Wave1 — исторически интегрирована на своём tuple: [report](../../artifacts/v6/iteration-20261004-wave1-integration-type2-release-receipt/report.md). [43](43-PARALLEL-WAVE-1.md) — archived pool protocol, не active dispatch.
