# Managed transactional mapping в лабораторной QEMU

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

`Test-CxlTransactionalMapping.ps1` вызывает существующий, неизменённый
`TransactionalHdmMapping` через новый `LabHdmHardware : IHdmDecoderHardware`.
Это host-managed orchestration: MMIO-записи выполняет установленная EFI Shell,
регистры наблюдаются QMP. Это не native SingBoot HDM transport и не новый драйвер.

```powershell
./tools/qemu-cxl/Test-CxlTransactionalMapping.ps1
```

Каждый MEM/MMIO case использует отдельную VM с `hdm_for_passthrough=on`:

1. Validate/Stage/Commit/Readback всех hops EP -> RP -> Host.
2. Запись scratch sentinel через гостя.
3. Настоящий Retire, который вызывает Disable в порядке Host -> RP -> EP.
4. Снимок register/alias; попытка гостевой записи после Retire.
5. Повторный Map, проверка неизменности sentinel и роста mapping generation.
6. QMP system_reset, Retire со старым snapshot: ожидается Quarantined.
7. Новый Map с актуальным reset snapshot также отвергается из-за quarantine.

Адаптер поддерживает только полное устройство 256 МиБ, SourceOffset=0,
SourceBytes=HpaBytes=capacity, один target, отсутствие interleave и locks.
DPA range в Readback выводится из этого ограниченного профиля, размера декодера
и нулевых target/skip регистров; это **не независимое чтение общей DPA translation**.
Reset sequence наблюдается через QMP до/после операций; это лабораторная эпоха,
не защищённая production reset authority. Процесс теста завершается после опыта.

Даже успешный Retire здесь не доказывает DMA quiescence/cache retirement.
DecoderIsolationQualified и TransactionalHdmMappingRetirementQualified остаются false.
Quarantine после reset не удаляет stale alias: после него память не переиспользуется,
VM целиком завершается. RuntimeKernel не исполняется, boot handoff не выполняется.

Первый live run: `CXL/logs/hdm-managed-map-retire/result.json`;
точные input hashes, launch arguments, serial/QMP и FlatView сохранены рядом.

## Отказы после фактического commit

`FaultInjectedHdmHardware` оборачивает настоящий лабораторный транспорт. После
успешного host commit, его readback и проверенной гостевой записи sentinel он
либо возвращает искусственный PartialCommit, либо выполняет реальный QMP reset
и возвращает ResetObserved. Это **инъекция отказа транспорта**, не обнаруженный
аппаратный отказ QEMU. Алгоритм TransactionalHdmMapping не изменён.

```powershell
./tools/qemu-cxl/Investigate-CxlHdmDecoderGate.ps1 -HostBridgeHdm `
  -Scenario transactional-commit-fault,transactional-reset-fault -Access MEM `
  -TimeoutSeconds 240

# Новые VM + assertions (Windows PowerShell с установленным Pester):
Invoke-Pester ./tools/qemu-cxl/TransactionalHdmFaults.Tests.ps1
```

Live MEM cases: **2/2 expectations met**, evidence
`CXL/logs/hdm-managed-faults/result.json`. PartialCommit не выдал mapping evidence;
компенсация Host -> RP -> EP убрала alias. Запись до нового Map блокировалась;
retry сохранил sentinel и завершился успешным Retire.

Reset внутри Map привёл к Quarantined, повторный Map отвергнут. Hardware adapter
не выполнил Disable-записей со старой эпохой. Это не удаление stale alias: VM
завершается целиком, область не передаётся новому владельцу.

Pester 3.4 / Windows PowerShell 5.1: **2/2**, проверены сохранённые результаты
этого live-прогона через явный `ResultPath`, а не второй независимый VM run:

```powershell
Invoke-Pester -Script @{Path='./tools/qemu-cxl/TransactionalHdmFaults.Tests.ps1';
  Parameters=@{ResultPath='CXL/logs/hdm-managed-faults/result.json'}}
```

Summary: `CXL/logs/hdm-managed-faults/pester-evidence-summary.json`.
Fault-матрица пока выполнена только в MEM; MMIO happy path проверен отдельно.
Ошибки Stage/Disable, reset между отдельными MMIO writes и DMA/cache concurrency
этой матрицей не покрыты. Production isolation flags остаются false.

Managed transaction experiment passed both live MEM/MMIO cases: existing Map/Retire executed, reverse disable order HostBridge/RootPort/Endpoint confirmed, alias absent after Retire, sentinel unchanged after remap, generation advanced, stale reset Retire and subsequent Map quarantined. Result and independent post-run assertions: CXL/logs/hdm-managed-map-retire/{result,assertions}.json. Build: 0 warnings/errors; PowerShell parse passed. Not a new Pester/full-suite run. Production isolation remains false. See [contract adapter scope](TRANSACTIONAL-HDM-RU.md).
