# Native read-only PCI/CXL и ACPI discovery

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
$a=./tools/qemu-cxl/Test-CxlEfiDiscovery.ps1
$b=./tools/qemu-cxl/Test-CxlEfiDiscovery.ps1 -SkipBuild -EfiImage $a.EfiImagePath `
  -RamMiB 6144 -CxlBus 24 -EduCount 4

# Совместная лабораторная композиция с guest crypto и RAM preparation
./tools/qemu-cxl/Test-CxlCapsuleAccess.ps1 -WithEfiPciDiscovery -WithEfiRamCopy -TimeoutSeconds 300
```

CXLDISC.EFI явно запускается установленной EDK2 Shell. Он использует существующий
firmware EFI_PCI_IO_PROTOCOL, не создаёт драйвер, не вызывает Pci.Write,
Attributes.Set или DMA Map. Firmware до этого выполняет своё обычное PCI resource
assignment; неизменность всех PCI registers в течение всего firmware boot не заявляется.

## Что подтверждается

* До 256 PCI handles: GetLocation, header reads, выбор единственного memory-only
  CXL endpoint и прямого CXL root port.
* Native DVSEC parser: chain до 64 capabilities, visited offsets, 4-КиБ bounds,
  Device DVSEC и Register Locator отдельно, revisions/BIR/BAR/upper-half,
  reserved bits, duplicates и overflow. Внутри EFI — 13 отрицательных cases.
* Полные EP/RP snapshots сравниваются с QMP ECAM и существующим managed
  CxlPciDvsecParser. FlexBus overlap остаётся opaque warning, payload не используется.
* EFI ACPI2 GUID -> checksummed RSDP/XSDT -> CEDT. Чтения ограничены EFI ACPI
  Reclaim/NVS descriptors, 64-КиБ tables, 128 XSDT entries и 256 subtables.
* Single-target modulo CFMWS и соответствующий CXL2 CHBS: байты и геометрия
  сравниваются с QMP и host discovery. Внутри EFI — 10 отрицательных ACPI cases.

Operational BDF/BAR/CFMW не передаются бинарнику из host config. Constants в
синтетических parser selftests — только входные тест-векторы. Реальное discovery
читает firmware, что дополнительно проверено переносом адресов тем же бинарником.

## Обнаруженное ограничение CFMWS

Реальная CEDT локальной QEMU содержит restrictions **0x002f**: объявлен BI bit 5
одновременно с host-only bit 1. Значение BI сверено с CXL 3.1 Table 9-22. Это
объявление не доказывает работающие Back-Invalidate flows и не квалифицирует
любое сочетание decoder/coherency mode. Новые компоненты только читают эти поля.

Первый CXL2-only parser отверг 0x20; сырая CEDT сохранена в
CXL/logs/efi-acpi-debug-run/native-pci-discovery.txt. После проверки определения
разделены read-only geometry и access policy. Таблица не переписывалась и не
нормализовалась до 0x000f; reserved bits 6..15 по-прежнему отвергаются.

GuestCfmwAccessPolicyQualified=false, GuestCxlBiOperationVerified=false.
HostOnlyAndBiBothAdvertised записывается отдельно, без объявления полной поддержки
CXL 3.x или универсального дефекта всех версий QEMU.

Источники: [UEFI PCI Bus Support](https://uefi.org/specs/UEFI/2.10/14_Protocols_PCI_Bus_Support.html),
[ACPI table architecture](https://uefi.org/specs/ACPI/6.5/05_ACPI_Software_Programming_Model.html),
[CXL 3.1 Table 9-22](https://computeexpresslink.org/wp-content/uploads/2024/02/CXL-3.1-Specification.pdf).
CXL specification copyright belongs to Compute Express Link Consortium, Inc.

## Реальные результаты 24 октября 2026

Один бинарник SHA256 C951D2645D2A5FFD315DE16F10B44091EF631EDE1D9A32B3CCD30406B2BFCF64:

| Поле | 4 GiB, bus 12 | 6 GiB, bus 24, 4 EDU |
|---|---|---|
| EP BDF | 0000:0d:00.0 | 0000:19:00.0 |
| EP component BAR | 0x81200000 | 0x81600000 |
| RP BDF | 0000:0c:00.0 | 0000:18:00.0 |
| CHBS host base | 0x180000000 | 0x200000000 |
| CFMW base | 0x190000000 | 0x210000000 |
| CFMW size | 4 GiB | 4 GiB |
| Target UID | 12 | 24 |

Артефакты: CXL/logs/efi-acpi-geometry-final и CXL/logs/efi-acpi-relocated-bars.
Промежуточный случай с одним EDU прошёл, но BAR не сместился; он не используется
как доказательство BAR relocation. EDU не запускает DMA в тесте, только занимает
BAR resources; wrapper предварительно проверяет его наличие в локальной QEMU.

EfiDiscovery.Tests.ps1: **2/2 passed, 0 failed, 125.79 s**. Первый тест — same-binary
relocation, второй — совместный guest PCI/ACPI + crypto + owned RAM/BootInfo flow.
Summary: CXL/logs/efi-discovery-pester-summary.json.

## Диагностическая lane и открытые gates

Source config сохраняется как baseline; actual RAM/bus/EDU overrides записываются
в result.json и launch.json. Используются TCG и volatile memory-backend-ram.
Read-only lane не программирует HDM, не записывает капсулу и не проверяет CXL.mem
data access: GuestCxlMemoryAccessible=null. Нет qtest.

HybridCpuBootSession/IHybridCpuBootHardware не изменены. Их требование current
deny-by-default DMA isolation перед рабочим PCI/mailbox access не снято;
диагностический firmware read не считается production session.
GuestHdmProgrammingByEfiVerified=false, GuestDmaIsolationQualified=false.
В полной композиции HDM всё ещё программирует прежний Shell lab script.

Read-only discovery lane не квалифицирует runtime startup/authority, coherency или HDM retirement. Поздние native mailbox/HDM/RAM/A paths проверены отдельно; current blockers см. [primary queue](blockers-singnextos/README-RU.md).

Файлы: efi/PciDiscovery.c, efi/AcpiDiscovery.c, NativePciQualification.cs,
NativeAcpiQualification.cs, PciDiagnostic.cs; Build-CxlEfiDiscovery.ps1,
Test-CxlEfiDiscovery.ps1 и EfiDiscovery.Tests.ps1. UefiLab.h дополнен стандартными
System Table fields в конце; offsets ранее использованных полей не изменены.

Финальные Pester artifacts:
* baseline: CXL/logs/efi-discovery-89628c417e5642aeb30d70b5c965e3ba;
* relocation: CXL/logs/efi-discovery-840c4130bd414b57bd6586e0642d895c;
* оба используют один EFI SHA256
  8F51C487A109EDCEED7E3A0BA31A605FF293E46FAF1013E3107A397FCFB7FA72.
Это отдельная сборка того же исходника, не утверждение bit-identical rebuild
между независимыми link invocations. Точные input/output hashes сохранены.
Сводка, включая полный crypto/RAM run: CXL/logs/efi-discovery-summary.json.
В конце recorded run QEMU процессов не оставалось; host selftest также прошёл.

Diagnostic VM использует фиксированные 4 vCPU, 256 MiB CXL, 1 MiB LSA,
4 GiB CFMW. RAM/bus/EDU меняются явно; исходный config остаётся baseline,
точная командная строка каждого экземпляра сохранена в launch.json.
