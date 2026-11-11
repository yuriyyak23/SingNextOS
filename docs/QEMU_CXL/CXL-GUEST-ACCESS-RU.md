# Гостевой доступ к CXL и лабораторный DirectBootCapsule

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

Этап продолжает NativeWindowsRam. Сохранение между процессами не требуется. Новый инструмент находится в `tools/SingNext.CxlLab`; исходники SingBoot/Core/Capsule и установленная прошивка не менялись.

## Запуск

Из корня локального SingNextOS:

```powershell
./tools/qemu-cxl/Test-CxlCapsuleAccess.ps1 -Json -Verbose
# Свой компонент размером 1..65536 байт:
./tools/qemu-cxl/Test-CxlCapsuleAccess.ps1 -PayloadPath 'D:\Images\component.bin' -Json
```

Скрипт собирает новый проект закреплённым в global.json SDK .NET 11, запускает selftest и полный тест. Параметры: WorkDir, OutputDirectory (только новый каталог), PayloadPath, TimeoutSeconds (на одну VM), SkipBuild, Json. По умолчанию отчёт сохраняется в новом `CXL/logs/capsule-qualification-<GUID>/result.json`.

Квалифицируется исходная топология: 4 ГиБ RAM, 4 vCPU, CXL 256 МиБ, LSA 1 МиБ, окно 4 ГиБ, TCG. Скрипт не подключается к существующей VM, не перезаписывает её память и не открывает реальные PMEM/LSA файлы для записи. Создаются три отдельных экземпляра QEMU с новыми VARS и FAT-дисками. Очистка использует только собственные Process handles; штатно выполняется QMP quit.

## Что делает тест

1. В первой VM читает реальную PCI-топологию, ECAM, extended capabilities/DVSEC, Register Locator, BAR и component/HDM capabilities. QTest используется только в этой одноразовой VM обнаружения. Адрес CFMW берётся из фактической memory tree; адреса и исходные регистры фиксируются.
2. Создаёт отдельный FAT16-диск с capsule.bin, startup.nsh, map.nsh и qualify.nsh. Это диск данных, без нового BOOTX64.EFI или другого EFI-компонента.
3. Во второй VM, уже **без QTest**, повторно проверяет топологию и запускает скрипты встроенной UEFI Shell. Команды `mm -MMIO` программируют HDM endpoint/root port; состояние проверяется чтением base/size/enable/COMMITTED/error/interleave. Host Bridge без объявленного HDM capability явно отмечается как таковой.
4. После commit проверяет нулевую целевую область. Shell записывает непустые 64-битные слова образа и выполняет `dmem` всей области. Это перенос данных через сгенерированные MM-команды, а не готовая реализация бинарного копирования EFI-файла в память.
5. По **байтам гостевого dmem**, а не исходного файла, вычисляет SHA-384, проверяет подпись manifest и хеш payload. Независимое чтение QMP pmemsave по полному HPA должно совпасть побайтно.
6. Отдельно проверяет диагностический sentinel при снятом COMMITTED. Результат сохраняется честно, включая отрицательный исход этой проверки на локальной сборке.
7. В третьем новом процессе выполняет только mapping, читает обнулённую область и отвергает отсутствующий образ. Это cold process restart; поведение warm reset не квалифицируется.

Shell этой сборки печатает в строках dmem только младшие 32 бита адреса, хотя команда получает полный 64-битный HPA. Парсер восстанавливает старшие биты только внутри заданного диапазона, проверяет полное покрытие без повторов и сравнивает независимые чтения по полному HPA. Скрипты и сырые serial-логи позволяют проверить этот путь.

## Формат и доверие

DirectBootCapsule в текущих исходниках — API подготовки образа, не файловый кодек. Инструмент использует существующие HBV1/SNB1 кодеки и BootManifestVerifier. Лабораторная упаковка: HBV1 по смещению 0, manifest по 4096, detached ECDSA-P384 подпись по 6144, payload по 8192. Опциональный EvidenceOnly TLV 0x7f01 содержит смещение/размер/SHA-384 payload. Это не утверждённый production component-table ABI.

На каждый образ создаётся временный development-ключ; приватная часть не сохраняется, публичная PEM остаётся явным входом доверия для проверки. Проверка выполняется в unlocked Development policy, epoch 1. Production trust не настраивается.

Без PayloadPath используются 256 байт явно помеченных неисполняемых тестовых данных. Переданный файл считается opaque-компонентом: инструмент не делает его исполняемым ядром и не запускает его. После аутентификации гостевого readback вызывается существующий DirectBootCapsule.PrepareKernel в памяти **хостового** процесса для проверки контракта копирования и boot-info, без guest handoff.

Для отдельного создания образа:

```powershell
dotnet tools/SingNext.CxlLab/bin/Release/net11.0/SingNext.CxlLab.dll create --output CXL/capsule-example
dotnet tools/SingNext.CxlLab/bin/Release/net11.0/SingNext.CxlLab.dll verify --capsule CXL/capsule-example/capsule.bin --public-key CXL/capsule-example/development-public.pem
```

Selftest включает валидный образ и отказы при повреждении HBV1, manifest, подписи, payload, padding, чужом ключе и усечении. Отдельные тесты проверяют короткие/полные адреса dmem, неполное покрытие, повтор адреса и выход за диапазон.

## Интерпретация результата

GuestCxlMemoryAccessible=true означает выполненные гостем запись и полное чтение через CXL HPA с независимой сверкой. HdmConfiguredByGuest и CapsuleWrittenByGuest относятся к командам существующей UEFI Shell. CapsuleSha384VerifiedFromGuestReadback и SignatureVerifiedOnHostFromGuestReadback относятся к проверке на хосте по гостевым байтам.

`Passed` имеет область `Scope=GuestCapsuleRoundTrip`. Это не полная проверка CXL: FullCxlConformanceVerified=false. GuestSignatureVerification=false, RuntimeKernelExecuted=false, BootHandoffPerformed=false. Результат decoder gate diagnostic нельзя подменять успешным round-trip.

На локальной сборке обнаружено ограничение: после снятия COMMITTED endpoint запись sentinel всё равно достигает backing RAM. Это повторилось и без подключённого QTest. DecoderGateNegativeTestPassed=false и подробные значения сохраняются в отчёте. Поэтому запрет доступа через uncommit нельзя считать проверенной гарантией изоляции/ретайра mapping.

В этом early epoch endpoint объявлял CXL Device DVSEC ID0 revision3. Старый bounded v1 parser и wire parser не одна abstraction; сырые DVSEC сохранены. Поздние wire discovery/guest crypto/native RAM реализованы отдельно; это не qualification actual runtime provider.

Сохраняются source-config, capsule.json, capsule.bin, capsule-from-guest.bin, public PEM, discovery.json, result.json и per-VM logs: argv/PID/start time, точная версия из QMP greeting, JSON-ответы, физические readback-файлы, PCI, raw/plain serial и снимок экрана. Ранние неуспешные прогоны также оставляются для диагностики.

## Измеренные результаты 24.10.2026

Полный прогон прошёл в PowerShell 7 и Windows PowerShell 5.1, в том числе из рабочего каталога с кириллицей, пробелами и запятой. Pester 3.4.0: 2 теста прошли, 0 неуспешных (41,96 с). Внутри проверены 7 отказов для capsule и 3 отрицательных случая парсера dump. Diagnostic decoder gate имеет отдельный отрицательный результат, как описано выше, и не включается в утверждение полной conformance.

Успешный прогон: `CXL/logs/capsule-qualification-25d26536a94f4e31b985a885d190d4b1/result.json`. Источник и guest readback имеют одинаковый SHA-384:

```text
BB979B6C82448B1EB075D0540944B92A5D5E035F67011DE530D1B89A2ECE20409EA5598396B14CB0020BDB190C0BD057
```

Размер 8448 байт; payload 256 байт; HPA `0x190000000`; endpoint `0000:0d:00.0`. Host-side проверка исходных PMEM/LSA файлов после испытаний подтвердила прежний baseline. Тестовые процессы QEMU завершены. Существующие пользовательские изменения загрузочных исходников не затрагивались.

Официальные справочники: [CXL QEMU](https://www.qemu.org/docs/master/system/devices/cxl.html), [QTest](https://www.qemu.org/docs/master/devel/testing/qtest.html), локальные share/doc и [определения component registers](https://gitlab.com/qemu-project/qemu/-/blob/master/include/hw/cxl/cxl_component.h). Фактические возможности определяются локальными чтениями, не примерами master.
