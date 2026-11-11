# QEMU lab и runtime/physical backend

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

Прежние утверждения «QEMU не установлен» и «направление исключено» больше
не действуют для текущей лаборатории. В tools/qemu-cxl и tools/SingNext.CxlLab
есть Windows QEMU/EDK2 orchestration и native EFI PCI/ACPI/mailbox/HDM/capsule/RAM
pipeline. Полные параметры и сохранённые live результаты ведутся в
[каноническом QEMU разделе](../../QEMU_CXL/README.md).

Это отдельный диагностический профиль, не adoption runtime ICxlMemoryProvider.
Для принятого NativeWindowsRam cold persistence не требуется. Нельзя переносить
model tests на QEMU или EFI HPA read/write на production coherent memory.

Остаётся установить actual runtime provider composition, original reset/DMA/closure
sources и hardware qualification выбранной платформы. QEMU x86-64 не исполняет
HybridCPU ISA. Не пересобирать firmware и не устанавливать OS ради старой Phase 8.

## Действующий код

* [Test-QemuCxl.ps1](../../../tools/qemu-cxl/Test-QemuCxl.ps1)
* [Start-QemuCxlPersistent.ps1](../../../tools/qemu-cxl/Start-QemuCxlPersistent.ps1)
* [PciDiscovery.c](../../../tools/SingNext.CxlLab/efi/PciDiscovery.c)
* [BootCapsuleEntry.cs](../../../src/Kernel/Boot/SingNext.Boot.Capsule/BootCapsuleEntry.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [HybridBootProductionAdapterTests.cs](../../../tests/SingPlus.Tests/Runtime/HybridBootProductionAdapterTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
