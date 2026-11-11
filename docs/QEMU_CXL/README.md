# QEMU / CXL: QCXL activation plan

Актуально на **26.10.2026** по local source, сохранённым terminal evidence и решениям пользователя. Эта актуализация — docs-only, без нового build/VM pass.

Цель: разрешённый native startup → CXL discovery/mapping → verified capsule → owned NormalRam/HBI1 → intended runtime consumer с current authority и корректным lifetime. Diagnostic EFI return-to-Shell эту цель не закрывает.

## Рабочие документы

1. [SingNextOS blockers](blockers-singnextos/README-RU.md): **SNO-02 → SNO-01 → SNO-03**; SNO-04 implementation FutureGated.
2. [Canonical QCXL queue](modernize/README-RU.md): единственная consumer queue после классификации blockers.
3. [ISE/platform dependencies](blockers-hybridcpu-v2-ise/README-RU.md).
4. [Current state](CURRENT-STATE-RU.md), [source map](SOURCE-MAP.md), [environment/profiles](ENVIRONMENT-RU.md).
5. [Evidence index](evidence/README-RU.md): scopes, input epochs, failed history. Исторические отчёты не задают новую очередь.
6. [Continuation prompt](CONTINUATION-PROMPT-RU.md).

## Решения и текущая остановка

- **A разрешён и реализован в bounded lab:** external supervisor, отдельный COM2, whole-VM termination. Existing NativePreparationOwner adopted до PCI/CCI и через mapping/snapshot/crypto/RAM/cleanup. Повторно создавать owner или спрашивать выбор reset policy не нужно. [Контракт A](blockers-singnextos/QCXL-03-NATIVE-PROVIDER-SCOPE-RU.md).
- **N1 разрешён только как design и завершён:** [target/ABI draft](blockers-singnextos/SNO-01-N1-TARGET-ABI-RU.md). Draft не реализован и не разрешает EBS/RuntimeKernel execution.
- **Implementation goal blocked:** реального freestanding x86-64 guest runtime/PAL/bootstrap нет; пользователь это подтвердил. Также отсутствуют concrete kernel/platform producers и original aperture proof для positive runtime admission. Нужен отдельно разрешённый substrate scope либо real artifact/owner.
- SNO-03: independent provisioning и authenticated durable monotonic provider отсутствуют; provider не выбран.

HCI-01B high CXL HPA route, pending EDU DMA lifetime и EFI callback reset windows проверены в отдельных bounded workloads. Они не снимают startup/trust blockers. BootCapsuleEntry.Run остаётся Unsupported; kernel handoff и ProductionQualified не подтверждены.

Разрешён RAM-backed profile с потерей RAM после QEMU exit. Cold persistence/Variant C не обязательны. Firmware, OS security settings и ISE arch/core/ISA не менять.

[Legacy plan](MODERNIZATION-PLAN-RU.md), аудиты и [TEST-RESULTS](TEST-RESULTS.md) — только history/inbound links. Команды и текущие параметры: [qemu-cxl scripts](../../tools/qemu-cxl/README-RU.md), [lab](../../tools/SingNext.CxlLab/README.md). Applicable [global v6 backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md) и [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md) не дублируются здесь.
