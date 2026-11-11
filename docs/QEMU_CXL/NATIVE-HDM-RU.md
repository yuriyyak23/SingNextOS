# Native EFI HDM scratch diagnostic

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

Отдельный `CXLHDM.EFI` выполняет PCI/ACPI discovery и MMIO программирование HDM
внутри гостевой VM. Shell запускает приложение; host не исполняет Shell `mm`
для записи декодеров. Установленная EDK2 и production SingBoot не изменены.
Это тестовое EFI application, не firmware, не CXL driver и не BOOTX64.EFI.

```powershell
./tools/qemu-cxl/Test-CxlNativeHdm.ps1

# Сборка отдельного приложения явно opt-in:
./tools/qemu-cxl/Build-CxlEfiDiscovery.ps1 -NativeHdmLab -OutputDirectory CXL/logs/NEW-build
```

Без NativeHdmLab builder по-прежнему собирает read-only CXLDISC.EFI, без модуля
NativeHdm.c и без HDM-записей. В build.json отмечены scope, define profile,
исходные input hashes, compiler/linker hashes и SHA256 EFI image.

## Проверяемый путь

* Адреса endpoint/RP component получаются через EFI PCI I/O и bounded DVSEC parser.
* CHBS/CFMW получаются из checksummed ACPI RSDP/XSDT/CEDT с memory-map bounds.
* HDM capability каждого из трёх hops ищется в component capability array:
  bounded count, aligned bounded offset, ровно один HDM entry.
* До записей проверяется отсутствие active/locked/error state. Требуется host HDM.
* Native Stage/Commit/Readback EP -> RP -> Host; HPA и размеры проверяются.
* CPU volatile write/read 8-byte sentinel в scratch offset 1 МиБ нового backend.
* Observer handshake: EFI ждёт `c` максимум 30 s; host останавливает гостя,
  проверяет все HDM addresses/geometry/commit, alias и sentinel через QMP,
  продолжает VM и отправляет `c`.
* Native retire Host -> RP -> EP; CPU попытка записи в снятое отображение;
  remap должен показать исходное слово. Затем final retire.
* Host проверяет cleared commit/status всех hops и отсутствие alias после выхода.

На ошибке после начала Stage приложение пытается retire всех hops, сообщает
failure и возвращается в firmware. Host runner не считает timeout успехом и
завершает только принадлежащую ему VM. Это не доказательство rollback при каждом
аппаратном отказе: fault injection в native пути пока не выполнен.

## Реальные результаты

Два fresh VM runs прошли с **одним EFI бинарником**:

| Профиль | EP bus | EP component BAR | Host component | CFMW |
|---|---|---|---|---|
| 4096 MiB RAM, bus 12 | 0x0d | 0x81200000 | 0x180000000 | 0x190000000 |
| 6144 MiB RAM, bus 24, четыре EDU BAR consumers | 0x19 | 0x81600000 | 0x200000000 | 0x210000000 |

EFI SHA256: `BB1B98E4D629125C7F8E9E72F83C7621DDE3BA89A1D56B3735F15689A0815F08`.
Evidence: `CXL/logs/native-hdm-first` и `CXL/logs/native-hdm-relocated`.
В них: discovery.json, serial/QMP, native PCI/ACPI raw snapshots, mapped/retired
mtree, mapped-witness.bin и exact launch arguments. Native parser selftests:
13 PCI negatives и 10 ACPI negatives. Они не подменяют HDM fault matrix.

Pester 3.4 / Windows PowerShell 5.1: **3/3**, assertions сохранённых live результатов,
не дополнительный VM run. Summary: `CXL/logs/native-hdm-pester-evidence-summary.json`.
`NativeHdm.Tests.ps1` без параметров запускает fresh baseline и relocation;
BaselineResult/RelocatedResult явно включают проверку существующего evidence.

## Границы

Это диагностическая native последовательность, а не исполнение .NET класса
TransactionalHdmMapping внутри EFI. Проверенный managed контракт остаётся
отдельным предыдущим сценарием. Здесь не создаются production mapping generations.

Требуется лабораторный QEMU профиль 256 MiB RAM-backed Type-3, FMW 4 GiB,
единственный target, DPA offset=0, известные QEMU PCI identities, TCG и
hdm_for_passthrough=on. Native diagnostic не проверяет полную DPA capacity через
mailbox, не квалифицирует hardware и не предназначен для физического CXL host.

CFMWS restrictions/BI advertisement сохраняются с POLICY_QUALIFIED=0.
DMA quiescence, caches, reset lifecycle и production access policy не доказаны.
Scratch sentinel не DirectBootCapsule. Этот scratch-only epoch не выполнял crypto/RAM; поздняя [native capsule integration](NATIVE-CAPSULE-RU.md) имеет собственный report.
RuntimeKernelExecuted=false, BootHandoffPerformed=false.

Read-only regression после этих изменений прошла: CXL/logs/efi-discovery-after-native-hdm; GuestHdmProgrammingByEfiVerified=false, GuestCxlMemoryAccessible=null. Все тестовые VM завершены.
