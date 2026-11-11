# SNO-01: принятое решение N1

**26.10.2026: пользователь разрешил N1 — target/ABI design only.** A lifecycle lab остаётся разрешённым отдельно; повторный выбор A/N1 не требуется. N1 design завершён в [target/ABI assessment](SNO-01-N1-TARGET-ABI-RU.md).

## Основания решения

- KernelEntryPoint/SingPlus.Boot — host-debug User/ManagedGc, не native UEFI receiver.
- Existing HybridBootInfoEntryAdapter использует GC.AllocateUninitializedArray; необходим реальный guest runtime/heap.
- Concrete kernel NormalRam map/physical-copy/current-epoch producers отсутствуют.
- RamStage после observer `c` очищает/free payload и HBI1. Printed INFO address не retained kernel ownership.
- HBI1 magic уже совпадает с shared codec; HBV1 — volume. Новый parser/format не нужен.

[Caller audit/source anchors](../QCXL-05-CALLER-AUDIT-RU.md). N1 определил post-EBS x64 receiver contract, retained immutable read loan до exact VM terminal, provider/currentness/aperture gates, minimal connected diff и acceptance. Это draft, не implemented ABI или новый behavior pass.

## Остановка implementation

Supported freestanding guest runtime/PAL/bootstrap отсутствует; пользователь подтвердил отсутствие artifact вне проверенных paths. Windows NativeAOT host binary и HybridCPU ISA output не substitute. Concrete kernel/platform producers и original aperture proof также Missing/ExternalBlocked.

Требуется real artifact/owner либо новый отдельно разрешённый runtime-substrate scope. N1 не разрешает новый runtime port, original SingBoot/DirectBootCapsule/BootCapsuleEntry.Run changes, RuntimeKernel execution, ExitBootServices, production BOOTX64.EFI, firmware/OS changes или independent trust provider.

Минимальная ownership/entry design граница SNO-01 рассмотрена без автоматического снятия FutureGated SNO-04 implementation. Existing writer/importer/authority admission переиспользуются. [Canonical queue](../modernize/README-RU.md), [separate trust/state request](SNO-03-PROVIDER-REQUEST-RU.md).
