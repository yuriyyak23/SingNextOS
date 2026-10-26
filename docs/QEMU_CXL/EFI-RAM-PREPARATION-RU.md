# Гостевая подготовка System RAM и минимального HybridBootInfoV1

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
./tools/qemu-cxl/Test-CxlCapsuleAccess.ps1 -WithEfiRamCopy -TimeoutSeconds 300
```

Режим подразумевает guest crypto. Это следующий инфраструктурный этап после
верификации SNB1, **не исполнение RuntimeKernel и не полный SingBoot handoff**.
Исходные SingBoot/DirectBootCapsule/RuntimeKernel и установленная EDK2 не меняются.

## Порядок и ownership

EFI сначала читает CXL в отдельный snapshot и проверяет SHA-384/ECDSA-P384.
Только после успеха явный аргумент `--ram-copy` разрешает подготовку RAM.
Через штатную EFI_BOOT_SERVICES.AllocatePages(AllocateAnyPages,EfiLoaderData)
выделяется rounded payload region и отдельная страница для BootInfo.
GetMemoryMap подтверждает принадлежность выделенного диапазона LoaderData.
Payload копируется из аутентифицированного snapshot, затем destination SHA-384
сверяется с подписанным descriptor. Данные не читаются повторно из mutable CXL.

Минимальный BootInfo содержит header 120 байт, платформу лаборатории, ABI 1/1,
ColdPowerOn, reset sequence 1 и ноль evidence records. SHA-384 и CRC32C вычисляются
в госте. Это wire fixture; reset sequence не является доказательством реального
reset authority, а отсутствие records не даёт полноценный boot context ядра.

EFI публикует `CXL_RAM_READY` с адресами, длиной, страницами и состоянием, затем
до 30 секунд ждёт observer acknowledgement через ConIn. Пока страницы принадлежат
приложению, host читает их QMP pmemsave, сохраняет dump и CPU FlatView:

* весь диапазон обязан входить в `pc.ram` normal CPU address space;
* payload обязан побайтово совпасть с подписанным исходником;
* padding остаётся нулевым;
* BootInfo разбирается оригинальным HybridBootInfoCodec и сравнивается с
  canonical encoding существующего контракта.

Host посылает `c`; EFI очищает все выделенные страницы, проверяет очистку и
вызывает FreePages только для собственного allocation. При timeout очистка и
освобождение также выполняются, а тест не считается успешным. Нет вызова payload,
ExitBootServices, отключения host-защит или изменения firmware.

QMP — наблюдатель того же эмулируемого physical address space, не независимый
аппаратный CXL контроллер. Однако destination здесь отдельно подтверждается как
pc.ram, а данные читаются до освобождения, а не из уже освобождённых страниц.

## Отказы

Тестовый EFI поддерживает явные fault modes (не production интерфейс):

* `--ram-copy-fail-copy`: меняется бит уже скопированного destination; SHA-384
  обязан отвергнуть копию, все страницы очищаются до observer read;
* `--ram-copy-fail-info`: повреждается CRC BootInfo; подготовка отвергается,
  payload и BootInfo очищаются до observer read;
* `--ram-copy-fail-alloc`: инъекция отказа перед AllocatePages, без destination.
  Это не проверка реального исчерпания памяти firmware.

Host независимо читает нули в удерживаемом allocation для двух первых случаев.
Все режимы проходят аутентификацию source; failed crypto не входит в RAM stage.

## Доказательства и границы

Новые файлы: `efi/UefiLab.h`, `efi/RamStage.c`, `RamCopyQualification.cs`,
`EfiRamCopy.Tests.ps1`. Build metadata включает хеши EFI ABI header и RAM stage.
Остальной controller/Build-CxlEfiReader поддерживает явный opt-in.

Артефакты каждого run: `ram-*-ready.txt`, `ram-*-release.txt`, `ram-*-owned.bin`,
`ram-*-flatview.txt`, `guest-hybrid-boot-info.bin`, `guest-ram-preparation.json`.
Нулевые failed buffers проверяются до FreePages. Release marker подтверждает
дополнительную гостевую проверку очистки непосредственно перед FreePages.

GuestAuthenticatedPayloadCopiedToSystemRam, GuestDestinationSha384Verified,
GuestHybridBootInfoWireCompatible — отдельные поля, не синонимы BootHandoffPerformed.
GuestResetAwareCopyQualified=false, GuestBootInfoFullHandoffQualified=false;
BootIntegrationReady, RuntimeKernelExecuted и HDM retirement остаются false.

Этот ранний RAM-copy epoch не доказывал platform reset/isolation authority или kernel handoff. Поздний A native lifecycle проверен отдельно; current dependencies — [primary blockers](blockers-singnextos/README-RU.md).

EFI ABI сверены с официальными разделами
[UEFI Boot Services](https://uefi.org/specs/UEFI/2.10/07_Services_Boot_Services.html)
и [Loaded Image](https://uefi.org/specs/UEFI/2.10/09_Protocols_EFI_Loaded_Image.html).

## Результаты input epoch

Первый live run CXL/logs/efi-ram-copy-first: Passed=true.
Для fixture 256 байт выделены две LoaderData страницы; payload и BootInfo
прочитаны host observer до FreePages. Copy hash fault и BootInfo CRC fault
отвергнуты; observer прочитал полностью нулевые allocations до освобождения.

Pester EfiRamCopy.Tests.ps1: 1/1 passed, 0 failed, 93.88 s.
Полный run CXL/logs/capsule-qualification-ae2baaea90d94176b83c36f3b68065b5;
summary CXL/logs/efi-ram-copy-pester-summary.json.
Максимальный payload проверяется отдельно, не подменяется маленьким fixture.

Максимальный live run CXL/logs/efi-ram-copy-max-payload завершился Passed=true:
65536-byte payload на CXL offset 240 MiB, destination 17 страниц (69632 байта).
16 страниц payload + одна отдельная BootInfo page. Все три owned dumps имеют
69632 байта; два error dumps полностью нулевые. Финальный независимый audit
сравнил SHA384 RAM payload с исходным payload-65536.bin, проверил нулевые dumps
и BootInfo размер 120 байт: CXL/logs/efi-ram-copy-summary.json.
В конце recorded run QEMU процессов не оставалось; selftests host contracts также прошли.
Timeout/FreePages-error branches проверены кодом, но отдельная runtime injection
для них на этом этапе не выполнялась. При сбое controller finally завершает
только принадлежащую тесту VM, не затрагивая другие QEMU процессы.
