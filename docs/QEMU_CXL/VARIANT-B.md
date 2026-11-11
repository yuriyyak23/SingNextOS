# Вариант B: результат внешнего аудита и локальная квалификация

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

Дата: 24.10.2026. Пользователь подтвердил допустимость потери CXL RAM при завершении QEMU. Поэтому критерием этого этапа является работа Type-3 с `persistent-memdev` на native Windows, а не долговременное файловое хранение.

## Вывод

**Вариант B работает.** Добавлен явный профиль `NativeWindowsRam`. Вариант C не запускается: условие перехода к нему не наступило. Установленный QEMU, EDK2, SingBoot и архитектура SingNextOS не изменены; WSL/Linux не устанавливались.

| Проверка | Результат |
|---|---|
| Локальный QEMU | 11.1.0, v11.1.0-12130-ge470268ff4 |
| memory-backend-ram → persistent-memdev | Принят при обычном запуске, не только -S/help |
| CXL топология и CFMW | q35,cxl=on; pxb-cxl; cxl-rp; cxl-type3; окно 4096 МиБ |
| Backend устройства | QMP persistent-memdev = `/objects/cxlmem` |
| Тип backend | QMP qom-list: `child<memory-backend-ram>` |
| Размер CXL | QMP qom-get: 268435456 байт |
| LSA | `/objects/cxllsa`, RAM, 1 МиБ |
| Основная RAM / vCPU | 4096 МиБ / 4 |
| EDK2 | Локальная CODE/VARS pflash-пара, UEFI Shell; снимок после 15 секунд |
| Повторный запуск | Два холодных старта с отдельными PID и штатной QMP остановкой |
| Защита экземпляра | Повторный Start при работающей VM отклонён |
| Файлы PMEM/LSA | В тестовом окружении отсутствуют; аргументов mem-path нет |
| ConfigurationValid | true для NativeWindowsRam |
| QemuDeviceCreated | true для NativeWindowsRam |
| GuestCxlMemoryAccessible | null, гостевой HDM/CXL.mem тест не выполнялся |
| BackingFilesAttached / ColdRestartPersistence | false / false |
| Windows PowerShell 5.1 | Два запуска/остановки успешно, включая QOM проверки |
| Pester 3.4.0 / PowerShell 5.1 | 2/2 теста: исходные 16 инфраструктурных проверок и новый RAM lifecycle |

## Уточнение аудита

Пример без LSA не подходит локальной сборке. Он был фактически проверен и отвергнут QEMU с `lsa property must be set for persistent devices`. Скрипты теперь заранее требуют ненулевую LSA для квалифицированного persistent Type-3 профиля; 1 МиБ достаточно. При этом LSA также размещается в RAM и не требует file-backend.

Вариант B уже частично проверялся старым smoke test. Теперь это полноценный явный профиль обычных Start/Status/Stop с сохранением эффективной конфигурации, проверкой QOM-ссылок и отдельным автоматизированным тестом. Автоматической подмены `NativeWindowsFile` на RAM нет.

Тезис аудита о POSIX-зависимой реализации file-backend объясняет возможную причину Windows-ограничения; исходники удалённых сборок не использовались. Локально достоверно подтверждено отсутствие объекта и отказ его создания. Версия 11.1.2 из аудита не установлена и не квалифицирована. Замена версии или использование другого нативного бинарника сами по себе не считаются решением.

## Использование

Из `C:\Users\Yuriy\Desktop\SingNextOS`:

```powershell
./tools/qemu-cxl/Start-QemuCxlPersistent.ps1 -Profile NativeWindowsRam -DryRun
./tools/qemu-cxl/Start-QemuCxlPersistent.ps1 -Profile NativeWindowsRam -Accel tcg
./tools/qemu-cxl/Get-QemuCxlStatus.ps1 -Json
./tools/qemu-cxl/Stop-QemuCxl.ps1

./tools/qemu-cxl/Test-QemuCxl.ps1 -Profile NativeWindowsRam -SmokeTest -Json
./tools/qemu-cxl/Test-NativeWindowsRam.ps1
Invoke-Pester ./tools/qemu-cxl/Infrastructure.Tests.ps1
```

Start без Profile использует Profile из конфигурации; старая конфигурация без поля остаётся NativeWindowsFile. Test-QemuCxl без параметра проверяет исходный файловый профиль. Backing-файлы реального окружения не изменялись. Сохраняемая UEFI VARS независима от volatile CXL и может сохранять настройки прошивки.

Backup/Restore относятся только к файлам; они не снимают содержимое CXL RAM. Файлы не загружаются в RAM при старте. Проверка сохранности файлов не является тестом RAM-профиля. Доступ к CXL.mem из гостевого кода, HDM commit/readback и загрузка capsule не проверены.

## Воспроизводимые свидетельства

- `CXL/logs/variant-b-result.json`: сводный успешный прогон PowerShell 7, два запуска.
- `CXL/logs/variant-b-smoke.json`: изолированный RAM smoke с QOM-ссылками.
- `CXL/logs/variant-b-*/logs/variant-b-result.json`: полные результаты отдельных запусков, включая PowerShell 5.1; ранние неуспешные прогоны без LSA оставлены как диагностика.
- `CXL/logs/variant-b-*/logs/status-run-*.json`: QMP status, qtree, PCI, memory tree и backend-ссылки.
- `CXL/logs/variant-b-edk2.png`: просмотренный снимок UEFI Shell с обычного RAM-профиля.

`Test-NativeWindowsRam.ps1` использует уникальное окружение без PMEM/LSA файлов. В конце останавливает только собственный процесс; на ошибке есть finally cleanup. Тест дополнительно проверяет ранний отказ от LsaMiB=0. Ограничение BootSeconds действует на ожидание UEFI, не на гостевой код.

## Когда вернуться к варианту C

Только если позднее понадобится настоящее файловое сохранение CXL между холодными запусками и native Windows останется обязательным требованием. Тогда потребуется отдельная ветка/сборка QEMU и реализация Windows file-backed RAM с проверкой allocation granularity, file sharing/locking, lifetime RAMBlock, dirty tracking, migration и явной flush/crash-consistency семантики. Простой вызов CreateFileMapping/MapViewOfFile сам по себе не квалифицирует persistence. На текущем этапе этот патч не требуется и не создавался.
