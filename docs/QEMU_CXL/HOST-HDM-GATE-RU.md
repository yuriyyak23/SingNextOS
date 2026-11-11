# Optional host HDM gate и reset lifecycle

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

Этот этап исследует оставшийся HDM blocker на установленной Windows QEMU,
не меняя default VM profile, EDK2 или production boot contracts.

```powershell
# Профиль с дополнительным host HDM decoder
./tools/qemu-cxl/Investigate-CxlHdmDecoderGate.ps1 -HostBridgeHdm `
  -Scenario before-first-commit,committed-control,host-first-uncommit -Access MEM,MMIO

# Реальный in-process QMP system_reset, не новый QEMU процесс
./tools/qemu-cxl/Investigate-CxlHdmDecoderGate.ps1 -Scenario warm-reset -Access MEM -TimeoutSeconds 240
```

Флаг добавляет только обнаруженный в локальной сборке параметр:

```text
-device pxb-cxl,bus_nr=12,bus=pcie.0,id=cxl.1,hdm_for_passthrough=on
```

Это виртуальный host bridge decoder, **не физический PCI passthrough/VFIO**.
В реальном register discovery появляется третий HDM capability по адресу
0x180001128. Live QOM подтверждает host option=true, endpoint hdm-db=false.
Каждая строка матрицы использует новую private VM/backends/VARS.

## Наблюдения

| Состояние | CPU MEM | RootBridge MMIO |
|---|---|---|
| До первого commit | контрольная запись блокируется | блокируется |
| Все hops committed | запись проходит | проходит |
| Только host COMMITTED снят | alias удалён, запись блокируется | аналогично |
| Host -> RP -> EP uncommit | alias удалён, запись блокируется | аналогично |
| EP -> RP -> Host uncommit | alias удалён, запись блокируется | аналогично |
| Только host global enable снят | запись продолжает проходить | продолжает проходить |

Отдельные endpoint/RP uncommit при committed host по-прежнему не закрыли маршрут.
Это контролируемое наблюдение root gate, не доказательство полной CXL isolation.
Контрольный адрес находится в scratch RAM нового CXL backend, не в реальной
DirectBootCapsule. После возврата mapping проверяется сохранение исходного слова;
одного readback COMMITTED для успеха недостаточно.

Порядок Host -> RP -> EP соответствует reverse-hop compensation существующего
TransactionalHdmMapping при hops EP/RP/Host. Однако production hardware adapter
к нему не подключён, и сам managed Map/Retire этим тестом не вызывался.
TransactionalHdmMappingRetirementQualified и DecoderIsolationQualified остаются false.
Не квалифицированы DMA quiescence, CPU caches, конкурентный доступ и полный диапазон:
операционная запись проверяется на контрольном 8-byte sentinel, дополнительно
сохраняются регистры, mtree и FlatView всего окна.

## Reset — отдельный gate

Сценарий warm-reset означает именно QMP system_reset внутри того же процесса,
а не аппаратную классификацию warm/cold reset и не power-loss/persistence тест.
Проверяются свежий вход в установленную EDK2 Shell и изменение наблюдаемого QMP
reset sequence с 1 на 2. Все команды относятся только к принадлежащей тесту VM.

В baseline **и** host-HDM профиле после такого reset:

* HDM registers сброшены и firmware снова запущена;
* прежний direct mapping alias остаётся;
* гостевое чтение до нового commit видит старое слово;
* гостевая запись достигает backend до нового commit;
* отдельный нетронутый 8-byte witness сохраняется после нового commit.

Поэтому reset registers=0 не доказывает закрытие HPA. Нельзя считать QMP guest
reset эквивалентом уничтожения отображения, выдавать старый mapping новому
владельцу или объявлять guest reset authority проверенной. Cold termination
процесса остаётся прежней границей независимых экспериментов; RAM-backend при
новом процессе теряет данные. Sentinel retention после in-process reset не
означает file-backed persistence всей памяти или LSA.

## Реальные результаты

HostHdmGate.Tests.ps1: **2/2 passed**, 480.23 s. Это regression assertions
наблюдений, не зелёная production retirement qualification.

* 12 MEM/MMIO cases: CXL/logs/hdm-gate-f90f85ef90e84f41b153ef8634babf3e.
* Host HDM reset: CXL/logs/hdm-gate-fc19f8b6c22142b89b0153112ac6605e.
* Baseline reset: CXL/logs/hdm-warm-reset-baseline.
* Предварительная host-HDM матрица: CXL/logs/hdm-host-bridge-profile.
* Pester summary: CXL/logs/host-hdm-gate-pester-summary.json.

В каждом case: live-cxl-options.json, discovery.json, точный launch.json, serial,
QMP log, snapshots registers/mtree/FlatView и хеши. Исходники конкретного бинарника
не сопоставлены с точной ревизией; SourceLevelRootCauseVerified=false.

## Проверенные QEMU control surfaces

Device help/QOM подтверждают hdm_for_passthrough и выключенный endpoint hdm-db.
Снимки QMP schema/root properties и CXL window options сохранены:
CXL/logs/qemu-cxl-live-schema.jsonl и CXL/logs/qemu-cxl-fmw-options.jsonl.
В просмотренной поверхности не найден отдельный advertised alias/BI-off control;
это не доказательство отсутствия всех недокументированных CLI полей. cxl-fmw
getter возвращает заданные options, а не полную схему optional полей.

Этот root-gate experiment не квалифицирует runtime authority/BI/coherency или kernel handoff. Поздний [transactional lab adapter](TRANSACTIONAL-HDM-RU.md) реализован отдельно; historical next-step request закрыт этим последующим этапом.

## Host HDM и сброс: дополнительный результат

Профиль `-HostBridgeHdm` включает обнаруженный локально `hdm_for_passthrough=on`.
Явный host uncommit удаляет alias и блокирует контрольную запись в MEM и MMIO.
Один QMP system_reset оставляет alias доступным, хотя HDM registers уже нулевые.
Сценарий `warm-reset-host-uncommit` подтвердил: явный host uncommit после reset
удаляет alias (1 -> 0); запись блокируется; после нового commit нетронутый witness
сохраняется. Это ограниченное наблюдение CPU route, не DMA/cache/reset isolation.
Production TransactionalHdmMapping Map/Retire в этом эксперименте не вызывался.

Команда: `./tools/qemu-cxl/Investigate-CxlHdmDecoderGate.ps1 -HostBridgeHdm -Scenario warm-reset-host-uncommit -Access MEM,MMIO -TimeoutSeconds 240`.

Результат: `CXL/logs/hdm-warm-host-explicit-uncommit/result.json` (2/2 live cases).
Предыдущие два Pester теста: 2/2, 480.23 s; дополнительная regression выделена
в Describe `Post-reset explicit host HDM uncommit` в HostHdmGate.Tests.ps1.
Подробности: [Host HDM gate](HOST-HDM-GATE-RU.md). Full retirement остаётся false.

After the report fix, the additional Pester regression passed: 1/1, 97.92 s (Windows PowerShell 5.1 / Pester 3.4). Both MEM/MMIO cases completed. Evidence: CXL/logs/hdm-gate-3e7e2358dab54a73836d3bbca7df0172/result.json; summary CXL/logs/host-hdm-post-reset-pester-summary.json. Earlier two tests passed separately; no claim of a new combined full-suite run.


[Managed Map/Retire laboratory adapter](TRANSACTIONAL-HDM-RU.md) invokes the unchanged production transaction algorithm against guest Shell MMIO, with explicit host-first disable and stale reset quarantine. This is not production isolation qualification.
