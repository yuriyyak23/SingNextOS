# Native mailbox: Identify Memory Device

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
./tools/qemu-cxl/Test-CxlNativeMailbox.ps1
```

Отдельный CXLMBX.EFI находит CXL Device Register block (Register Locator Block ID=3),
проверяет device capability array, primary mailbox и memory status. Выполняется
единственная разрешённая команда Identify Memory Device, **полный opcode 0x4000**,
с пустым input payload. Media effects отсутствуют, но command/doorbell MMIO
registers записываются — это не полностью read-only register access.

Не выполняются Set LSA, Set Partition, poison, sanitize, FW update или reset.
Нет raw-opcode CLI. Polling ограничен 2000 попытками со Stall 1 ms; автоматических
повторных команд, recovery resets или изменений Windows нет.

## Discovery и bounds

Поддерживается текущий QEMU single-Type-3 profile: 4 KiB device-register aperture,
HDM_Count=1 и Memory_Info_Valid=1. Array version=1; не более 32 headers;
проверяются bounds/alignment, duplicates и overlap. Размер mailbox payload должен
вписываться в capability length. Native parser selftests: **8 negative cases**.
Апертура ограничена проверенным QEMU profile, generic BAR sizing не реализован.

Capability array и payload читаются выровненными 64-bit MMIO loads. Начальные
byte-read attempts не дали корректного count в этой локальной сборке; нормальные
QMP snapshots сохранились. Native code не ослабляет array bounds/versions.

Return code берётся из status bits[47:32], не из всего status value. В данной
сборке status=1, RC=0: bit0 — отдельное поле background operation. Это не причина
считать Identify отказом и не доказательство конкретной фоновой media operation.
Проверка raw register snapshot в Pester независимо подтверждает RC=0.

## Реальные результаты

Установленная Windows QEMU 11.1.0, TCG, installed EDK2, RAM backend:

| Поле Identify | Значение |
|---|---|
| Total usable capacity | 268435456 bytes |
| Volatile-only capacity | 0 |
| Persistent-only capacity | 268435456 bytes |
| LSA size | 1048576 bytes |
| Response length | 69 bytes |

Это объявленная моделью persistent-memdev capacity. Она **не** делает RAM backend
file-persistent и не доказывает сохранение после завершения процесса.

Два fresh VM runs прошли с одним EFI binary:

* baseline device=0x81210000, mailbox=0x81210088;
* 6144 MiB RAM / bus 24 +4 EDU: device=0x81610000, mailbox=0x81610088.

EFI SHA256: `9A4681DE8ABF9839FCA146828DF6B6C7641E9166D9E1EEBBE3859C8F6378A08A`.
Native Identify raw bytes совпали с QMP payload, host декодировал capacity/LSA
независимо от native textual fields. Device base сверяется с managed DVSEC/BAR
resolver. Перед/после сохранены полные device registers.

Evidence: CXL/logs/native-mailbox-final и CXL/logs/native-mailbox-relocated.
Pester 3.4 / Windows PowerShell 5.1: **3/3**, assertions сохранённых live runs,
не дополнительные VM executions. Summary: CXL/logs/native-mailbox-pester-summary.json.
NativeMailbox.Tests.ps1 без параметров запускает новые baseline/relocated VM;
BaselineResult/RelocatedResult явно включают existing-evidence assertions.

Read-only CXLDISC.EFI regression прошёл отдельно:
CXL/logs/efi-discovery-after-mailbox. Mailbox module включается только явным
Build-CxlEfiDiscovery.ps1 -NativeMailbox. NativeHdmLab одновременно запрещён.

## Предыдущие ошибочные прогоны

Они не считаются qualification success и сохранены:

* native-mailbox-first: selftest positive sample имел overlap, исправлен sample;
* native-mailbox-second/register-audit/debug: byte MMIO header reads не прошли;
* native-mailbox-qword/completion-audit: Identify ответил, но первоначальная
  host-independent native проверка ошибочно требовала status==0; исправлен разбор
  return-code field. Final/relocated выше — результаты после исправлений.

## Стык с production transport

Локальные IBootCxlTransport.Execute и IHybridCpuBootHardware.ExecuteCxlMailbox
до QCXL-02 принимали byte opcode. После согласованной migration они принимают
ushort opcode; direct passthrough 0x4000 представим без потери, но всё ещё требует
current deny-by-default DMA isolation provider.
Token mapping не создавался, byte compatibility-overload отсутствует, opcode не
усекается. Native C module использует 16 bits.

Standalone path не production HybridCpuBootSession. Добавлен отдельный opt-in
[integrated capacity gate](NATIVE-CAPACITY-GATE-RU.md) native capsule pipeline.
В standalone mailbox run GuestCxlMemoryAccessible=null
(mailbox через CXL.io не является тестом CXL.mem). BootIntegrationReady=false,
GuestDmaIsolationQualified=false, RuntimeKernelExecuted=false, BootHandoffPerformed=false.
Timeout/busy/reset-interruption hardware tests и concurrent mailbox ownership пока
не выполнены. Использование полей CXL 3.1 не означает полной CXL 3.x поддержки.

## Источник register layout

Использован уже имеющийся официальный PDF CXL/dependencies/CXL-3.1-Specification.pdf,
sections 8.1.3.8, 8.2.8, 8.2.9.9.1.1 (страницы 501, 602-610, 693, 696-697).
SHA256: `032511D30125AC59E4C426D1C1819CB6F199C13C55567A395F24399C843B80D6`.
Установленные firmware и original SingBoot/RuntimeKernel не изменялись.
