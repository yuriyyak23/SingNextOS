# DirectBootCapsule под managed HDM mapping

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
./tools/qemu-cxl/Test-CxlCapsuleAccess.ps1 -WithManagedHdm -WithEfiRamCopy `
  -WithEfiPciDiscovery -TimeoutSeconds 240
```

Опциональный профиль включает host HDM decoder (`hdm_for_passthrough=on`).
В transfer VM ручной map.nsh **не исполняется**: используется существующий,
неизменённый `TransactionalHdmMapping` через лабораторный `LabHdmHardware`.
Обычный режим без флага сохраняет прежний сценарий.

Последовательность в одном transfer процессе QEMU:

1. Native EFI PCI/ACPI discovery и сравнение с QMP/managed parser.
2. Managed Validate/Stage/Commit/Readback EP -> RP -> Host.
3. Гостевая запись и чтение DirectBootCapsule в CXL.mem.
4. Гостевые SHA-384/ECDSA-P384, KAT и отрицательные проверки.
5. Копирование authenticated fixture payload в EFI-owned System RAM,
   проверка hash/BootInfo и очистка RAM в ошибочных сценариях.
6. Bounded host admission из гостевых CPU reads; замена тестовой капсулы.
7. Managed Retire Host -> RP -> EP и проверка отсутствия alias.
8. Контрольная CPU запись после Retire; remap должен показать прежний sentinel
   и неизменённый полный образ replacement capsule. Mapping generation растёт.
9. Финальный Retire; VM завершается.

Discovery и cold-restart — отдельные процессы с тем же host HDM option.
Cold-restart по-прежнему использует map.nsh и проверяет нулевую RAM/отказ от
отсутствующей капсулы. Это не доказательство сохранения между процессами:
RAM backend теряется, как принято для данного лабораторного профиля.

Независимые проверки RAM allocation выполняются, пока EFI держит страницы,
затем приложение очищает и освобождает их до этапа Retire. Этот опыт **не**
показывает сохранённый handoff buffer после revoke: runtime не стартует,
ExitBootServices не вызывается. BootInfo минимальный; production trust,
CFMWS BI policy, DMA/cache/reset isolation не квалифицируются.

Профиль ограничен whole-device mapping 256 МиБ с DPA offset=0; положение самой
капсулы регулируется OffsetMiB. Не создаётся новая production архитектура.
HDM orchestration выполняется host .NET кодом через EFI Shell MMIO, не native
SingBoot transport. Флаг требует WithEfiRamCopy, который включает guest crypto.

Тест `ManagedCapsule.Tests.ps1` по умолчанию запускает новые VM. Параметр
ResultPath явно переключает его на assertions сохранённого live evidence.
Первый объединённый прогон: `CXL/logs/capsule-managed-hdm-integrated/result.json`.
Inputs, EFI build metadata, managed mapping evidence, операции decoder hops,
гостевые transcript и mtree сохраняются рядом.

Integrated managed HDM capsule run passed:
CXL/logs/capsule-managed-hdm-integrated/result.json. Native PCI/ACPI discovery,
guest CXL.mem write/read, guest SHA384/ECDSA, EFI-owned System RAM preparation,
managed Retire/remap/capsule preservation and final Retire all verified.
Pester assertions against this saved live run: 3/3; not a second VM run.
Inputs use the default non-executable fixture (256 payload bytes), MEM at offset 0.
This result does not extend earlier max-payload tests to the new managed profile.
Original production boot contracts and firmware remain unchanged.
[Integrated commands and scope](MANAGED-CAPSULE-RU.md).
