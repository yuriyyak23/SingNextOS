# HDM Decoder Gate: локальная квалификация 24 октября 2026

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

Проверена установленная Windows-сборка QEMU 11.1.0
`v11.1.0-12130-ge470268ff4`, TCG, установленная EDK2, NativeWindowsRam.
Это исследование инфраструктуры. SingBoot не запущен, RuntimeKernel не исполнялся.

## Наблюдение

До первого commit гостевая запись не изменяет backing. После commit появляется
`cxl-direct-mapping-alias-0`. После endpoint uncommit управление меняется с
`0x1600` на `0x1000`, но alias и FlatView `nv-ram cxlmem` сохраняются:
гостевая запись по прежнему HPA проходит. CPU `-MEM` и RootBridge `-MMIO`
дают одинаковый результат. Снятие global enable и uncommit RP также не обеспечили
отзыв отображения. Изменение HPA/DPA не прошло ожидаемые проверки переназначения.

Это воспроизводимая проблема жизненного цикла отображения данной сборки.
Точная ошибочная строка исходного кода не установлена; утверждения внешнего аудита
о других версиях QEMU не являются результатом локального тестирования.
Доступных HDM trace events в этой сборке не обнаружено.

`DecoderIsolationQualified=false`, `TransactionalHdmMappingRetirementQualified=false`.
Снятие COMMITTED нельзя использовать как доказательство изоляции, освобождения
или передачи памяти другому владельцу. Каждый независимый эксперимент использует
новый процесс QEMU; окончание эксперимента завершает именно этот процесс.
Даже удаление alias само по себе не доказывает DMA quiescence и корректность кэшей.

## Воспроизведение без капсулы

Из корня локального репозитория:

```powershell
./tools/qemu-cxl/Reproduce-QemuHdmAlias.ps1
# Полная матрица: 11 сценариев, два пути доступа, свежая VM на случай
./tools/qemu-cxl/Investigate-CxlHdmDecoderGate.ps1
```

Первый wrapper проверяет before-first-commit, committed-control и endpoint-uncommit
через CPU MEM, без капсулы, криптографии и qtest. Использует установленную EDK2
Shell, не создаёт EFI-программ. `InvestigationCompleted=true` означает завершение
наблюдений, а не успешную изоляцию.

В новом каталоге `CXL/logs/hdm-gate-*` сохраняются `inputs.json` с хешами
бинарников/прошивки/конфигурации, `source-config.json`, `trace-capabilities.txt`,
`result.json`. Каталоги сценариев содержат argv, serial с точными MMIO-командами,
QMP jsonl, сырые PCI snapshots и снимки регистров/mtree/FlatView до и после.
Для сравнения используйте `logs/*.mtree.txt`, `logs/*.flatview.txt` и `logs/*.json`.
Отправка upstream issue и установка другой QEMU не выполнялись.

## Результаты и данные

20 случаев основной матрицы завершены: `CXL/logs/hdm-gate-a8aca22866c844a794a7d0bcdbb3f24c`.
Ещё два boundary-случая MEM/MMIO завершены:
`CXL/logs/hdm-boundary-72756cd4505242999058977b8a37655d`.
Последнее 8-байтовое слово 256-МиБ backing записывается. Запись за пределами
не изменила проверенные соседние данные и не отразилась как записанное значение;
это ограниченное наблюдение, не доказательство универсального отказа всех OOB обращений.

Капсула 65 536 байт (payload 57 344) на смещении 64 МиБ прошла round-trip.
Максимальный payload 65 536, образ 73 728 байт на смещении 240 МиБ также прошёл:
`CXL/logs/capsule-max-payload-8d53650707ef4e0489f5341791c1c0e5/result.json`.
В обоих случаях проверены вторая капсула, явная запись нулевых слов и потеря RAM
после нового процесса. Смещение 240 МиБ не является тестом конца backing.

Диапазон проверяется до гостевых записей: `offset <= capacity` и
`imageBytes <= capacity-offset`, что исключает переполнение суммы.
HPA вычисляется в checked-контексте. Selftest включает точное окончание диапазона,
выход на байт за пределы, нулевую длину и UInt64 overflow-входы.

## PCI и доверие

Новый bounded wire parser `CxlPciDvsecParser` разбирает реальные Device DVSEC и
Register Locator отдельно, проверяет BAR и поддерживаемые ревизии. Старый
`PciCxlDiscovery` не заменён автоматически. Неиспользуемый FlexBus DVSEC в snapshot
объявляет длину 0x20 с началом 0x17c, следующий cap начинается 0x190: конец 0x19c
пересекает следующий заголовок. Payload не интерпретируется и не считается валидным.
`UsedDvsecWireContractsValidated=true`, `FullPciLayoutConsistent=false`.
Это явная лабораторная политика; production SingBoot требует отдельной политики.

SHA-384, ECDSA P-384 и PrepareKernel проверяются на хосте по гостевому readback.
Ключи лабораторные, свежие для образа; независимый гостевой trust root,
anti-rollback и гостевая проверка подписи отсутствуют. `BootIntegrationReady=false`.
Поздние bounded EFI reader/crypto/native RAM/lifecycle этапы перечислены в [evidence index](evidence/README-RU.md). Этот HDM investigation не задаёт незавершённую задачу повторно реализовать их.

## Финальная регрессия

Windows PowerShell 5.1, Pester 3.4: CapsuleAccess + HdmDecoderGate — 3/3 passed,
0 failed, 97.71 s. Машиночитаемый итог: `CXL/logs/final-pester-summary.json`.
Сборка Release: 0 предупреждений, 0 ошибок. Selftest: капсула + 7 отказов,
dmem 3 положительных/16 отказов, DVSEC профили + 15 отказов,
диапазон капсулы + 5 отказов/переполнений — успешно.

Сырые bytes FlexBus 0x17c..0x19f сохранены в `CXL/logs/flexbus-raw-17c-19f.txt`:
`23 00 01 19 98 1e 02 02 07 00 26 00 02 00 26 00 ef 00 00 00 2e 00 01 20 01 00 00 00 00 00 00 00 00 00 00 00`.
Extended header задаёт next=0x190; DVSEC header задаёт length=0x20.
Именно эти объявленные поля дают пересечение, независимо от декодирования payload.

Infrastructure.Tests.ps1: 2/2 passed, 0 failed, 25.61 s
(`CXL/logs/final-infrastructure-pester-summary.json`). Всего три набора Pester:
5/5 passed. Проверки файлового хранения не означают, что RAM-профиль сохраняется
в эти файлы: в установленной QEMU memory-backend-file отсутствует.

Изменения этапа: CxlPciDvsecParser.cs; в SingNext.CxlLab —
HdmInvestigation.cs, DiscoveryCompatibility.cs, GuestMemoryDump.cs, InputEvidence.cs,
Qualification.cs, LabVm.cs, Program.cs; wrappers Investigate-CxlHdmDecoderGate.ps1,
Reproduce-QemuHdmAlias.ps1 и Test-CxlCapsuleAccess.ps1; Pester-наборы
HdmDecoderGate.Tests.ps1 и CapsuleAccess.Tests.ps1; README и данный отчёт.
Исходные контракты SingBoot, DirectBootCapsule и RuntimeKernel этим этапом не изменялись.

Новый минимальный wrapper также исполнен: `CXL/logs/hdm-minimal-final`.
Все 3 случая завершены; до commit запись блокируется, после commit проходит,
после endpoint uncommit продолжает проходить. InvestigationCompleted=true,
DecoderIsolationQualified=false. Итог wrapper: `CXL/logs/minimal-reproducer-summary.json`.

## Открытый этап: admission перед гостевой интеграцией

Добавлен `tools/SingNext.CxlLab/CapsuleAdmission.cs`: ограниченный источник с
capacity и reset/mapping generation, чтение блоками до 4096 байт в отдельный
snapshot, проверка внешнего SHA-256 SPKI pin для P-384 ключа, затем существующая
проверка капсулы. Snapshot очищается при успехе и исключении. Ключ и pin должны
поступать из доверенного provisioning, а не из CXL-образа. Это host-qualified seam,
не готовый гостевой verifier и не production ABI. Generation counters должны
предоставляться доверенным адаптером; они сами не обеспечивают изоляцию памяти.
Изменение источника без изменения generation не объявляется невозможным: подпись
проверяется только по захваченным байтам, после чего исходная CXL память не потребляется.

Release build: 0 warnings/errors. Selftest: положительный admission и 7 отказов
(short read, reset, mapping change, corruption, read exception, wrong pin, overflow)
прошли. Гостевой CXL-адаптер к этому интерфейсу пока не подключён.

На момент этого host-admission epoch native receiver/runtime startup не выполнялся. Поздние EFI reader/crypto/RAM/A этапы — отдельные reports; actual runtime/trust prerequisites см. [primary blockers](blockers-singnextos/README-RU.md).

## Shell source admission

`ShellCapsuleSource` подключает admission к живой VM: каждый блок до 4096 байт
читается гостевой `dmem` (CPU loads), затем проверяется host verifier. QMP RESET/
SHUTDOWN увеличивают наблюдаемый reset sequence; выборочные HDM register snapshots
сверяются перед/после чтения. Это не исключает transient remap, DMA и события,
которые внешний процесс не наблюдал; production generation authority не заявляется.
Файл `shell-source-admission.json` фиксирует evidence, число чтений и development
SPKI pin. `HostAdmissionFromBoundedGuestReadsVerified` не означает guest crypto.
SingPlus.Boot в этом experiment — host application, не UEFI entry. Последующий test EFI разрешён и реализован (следующий раздел); original runtime/EBS execution не выполнялись.

Живой прогон `CXL/logs/shell-admission-final` завершён успешно:
`Passed=true`, `HostAdmissionFromBoundedGuestReadsVerified=true`,
`GuestSignatureVerification=false`. Капсула 8448 байт прочитана 3 блоками через
EDK2 dmem; verifier и PrepareKernel исполнялись на хосте. Холодная RAM потеряла
данные, отсутствующая капсула отвергнута. В Pester добавлены отдельные assertions
успеха bounded admission и отсутствия заявленной guest signature verification.

## Разрешённый отдельный EFI этап

После явного разрешения пользователя добавлен opt-in `-WithEfiReader`.
[Тестовый EFI reader](EFI-READER-RU.md) исполнился в установленной EDK2 и проверил
byte identity CXL-капсулы с отрицательным тестом. Это отдельный новый компонент,
не изменение firmware, не production SingBoot и не гостевая проверка подписи.
Предыдущий запрет на создание отдельного тестового EFI снят пользователем;
запрет изменения EDK2/исполнения RuntimeKernel остаётся.


Дополнение к baseline: [optional host HDM gate и reset lifecycle](HOST-HDM-GATE-RU.md). Явный host uncommit в отдельном профиле закрывает контрольный CPU/MMIO маршрут; reset сам по себе этого не делает. Production retirement/isolation не квалифицированы.
