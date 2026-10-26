# SingNextOS blockers

**26.10.2026.** Приоритет **SNO-02 → SNO-01 → SNO-03**. SNO-04 implementation FutureGated. После точной классификации внешних/scope зависимостей — [canonical QCXL queue](../modernize/README-RU.md), без очереди из исторических отчётов.

## SNO-02 — lifetime authority, P0

**Partial.** Existing NativePreparationOwner adopted до discovery и через mailbox/HDM, authenticated snapshot, RAM/HBI1 preparation и cleanup. [A contract](QCXL-03-NATIVE-PROVIDER-SCOPE-RU.md): external supervisor, COM2, whole-VM termination разрешены и implemented в bounded lab.

| Requirement | Классификация |
|---|---|
| Native discovery/mailbox/mapping/snapshot/RAM/cleanup consumer | VerifiedExisting в diagnostic scope |
| VM/currentness/isolation/mapping provenance | Verified для opt-in A native path; intended managed/runtime adoption Missing |
| Partial commit/retire, copy/clear failure, missing ACK | Verified quarantine в exact runs, не general closure |
| EFI callback result ambiguity / reset | Entry/inside/return-before-receipt windows verified с pins до exact terminal; не same-VM recovery |
| Pending EDU DMA reset/provider loss | Verified timer-command window; independent VM peer сохранён, затем complete → native RAM |
| Old grants/records после fault/reset | Verified denial в tested windows; original reset firmware record не читается |
| Local decoder/alias removal как DMA/cache/remote closure | Contradicted: эти факты не эквивалентны |

[Evidence scopes/epochs](../evidence/README-RU.md). Default19-page record после EFI return доказывает только unchanged-firmware retention. A удерживает external original obligations до VM terminal, а не сохраняет старую firmware через warm reset. Source/copy/foreign/session/address/epoch/lost/partial ambiguity не разрешает read/free/consume/reuse.

Remaining consumer — real managed/runtime adoption и original aperture proof; повторять native composition или создавать второй lifetime manager нельзя.

## SNO-01 — native startup → runtime handoff

**ExternalBlocked.** Existing KernelOwnedPhysicalBootInfoReader, HybridBootInfoEntryAdapter, HybridBootRuntimeHandoff и HybridBootAuthorityAdmission не имеют actual native startup caller. BootCapsuleEntry.Run остаётся Unsupported.

Четыре input owners:
1. Actual loader: HBI1 physical address и exact byte length.
2. Kernel-owned NormalRam copy: current epoch, whole-range bounds и kernel-image overlap denial.
3. Fresh current endpoint/platform provider; firmware identities остаются evidence.
4. Original aperture retirement/quarantine owner и revalidation перед publication.

[N1 design](SNO-01-N1-TARGET-ABI-RU.md) завершён: post-EBS x64 receiver contract и retained immutable read loan до VM terminal. Но actual freestanding managed runtime/PAL/bootstrap отсутствует; пользователь подтвердил отсутствие external artifact. Concrete kernel map/copy/epoch/platform producers и original aperture proof Missing. Host-debug/Windows NativeAOT/C stub не substitute. Lab observer ACK очищает/free payload и HBI1, не передаёт kernel ownership.

Нужен separately authorized substrate scope либо real artifact/owner; N1 не разрешил implementation/EBS/RuntimeKernel execution. [Caller audit](../QCXL-05-CALLER-AUDIT-RU.md), [N1 authorization](SNO-01-STARTUP-DECISION-RU.md).

## SNO-03 — independent trust / durable state

**Missing / scope-blocked.** Existing manifest/keyset/role/epoch/revocation и ProtectedBootStateMachine/IBootProtectedState переиспользуются. Concrete protected store в src/tools отсутствует; development signer/static floor/HMAC/warm RAM/VARS не independent durable monotonic authority.

[Producer request](SNO-03-PROVIDER-REQUEST-RU.md): назначить provisioning/revocation owner и authenticated monotonic storage с durable flush/readback, atomic commit/recovery; затем actual rollback/torn-write/reset/revocation checks. TPM2 NV/trusted storage не выбраны, только research candidates.

## SNO-04 — retained ownership / EBS / kernel ABI

**Implementation FutureGated.** N1 разрешил только design необходимой SNO-01 ownership/entry границы, а не весь executable SNO-04. Original SingBoot/DirectBootCapsule/BootCapsuleEntry.Run/RuntimeKernel edits, EBS и execution требуют отдельного разрешения после real prerequisites. Не создавать циклическое требование сначала закрыть весь SNO-04 ради SNO-01 и не расширять scope молча.

## Решение о продолжении

Implementation goal **blocked**, не complete. A и N1 повторно не запрашиваются. Доступного intended runtime consumer в текущем scope нет; новые green fixtures/EDU/RAM-copy runs не устраняют отсутствие substrate/provider. Документальные исправления не новый behavior pass и не resume implementation.
