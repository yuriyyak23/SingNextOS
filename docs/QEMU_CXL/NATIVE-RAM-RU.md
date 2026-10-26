# Native authenticated capsule -> System RAM после CXL Retire

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
./tools/qemu-cxl/Test-CxlNativeCapsule.ps1 -WithRamPreparation -WithRelocation
```

Опциональный режим объединяет native PCI/ACPI discovery, native HDM,
read-only FAT provisioning свежего CXL backend, guest SHA384/ECDSA-P384 и
существующий EFI RAM preparation stage в одном CXLHDM.EFI.
Production SingBoot, RuntimeKernel и установленная EDK2 не изменены.

## Что доказано текущим прогоном

После проверки подписи EFI выделяет LoaderData pages для payload и минимального
HybridBootInfoV1, проверяет memory-map принадлежность, копирует authenticated
snapshot и проверяет destination hash/BootInfo CRC. Затем native callback снимает
HDM Host -> RP -> EP, обнуляет runtime source base и повторно проверяет hash RAM
и BootInfo CRC. Только после этого выводится CXL_RAM_READY.

Пока EFI держит выделение, host QMP observer подтверждает:

* COMMIT/COMMITTED сняты у всех трёх hops; CXL direct alias отсутствует;
* весь диапазон allocation находится в CPU FlatView pc.ram;
* payload совпадает с исходным подписанным fixture;
* historical run BootInfo разбирался existing codec и побайтово совпадал с
  canonical 120-byte минимальным представлением, включая hash/CRC;
* allocation padding остаётся нулевым.

После observer ACK приложение очищает и освобождает pages, затем возвращается
в установленную EFI Shell. Timeout не считается успехом. Payload не исполняется.
Это наблюдение RAM после закрытия CXL route; оно не доказывает полную cache/DMA
изоляцию и не означает, что очищенный/освобождённый буфер готов к handoff.

## Реальные результаты

`CXL/logs/native-capsule-ram-complete/summary.json`: passed.
Три fresh VM cases:

1. Valid fixture, 256-byte payload, 4096 MiB RAM / bus 12.
2. Damaged signature: rejected до RAM allocation/READY; mapping снят.
3. Та же valid capsule и тот же EFI binary при 6144 MiB RAM / bus 24 +4 EDU.

У positive cases выделено две страницы: payload page + BootInfo page.
EFI SHA256: `A6091C28B90E999C0765CBC97BF134FDA5AC71205D296AA882941CAA9552D1CC`.
Pester 3.4 / Windows PowerShell 5.1: **3/3**, assertions сохранённого live wrapper
evidence, не дополнительный VM run. Summary: pester-evidence-summary.json.

В valid/relocated: native-ram-ready.txt, native-owned-ram.bin,
native-hybrid-boot-info.bin, native-ram-retired-mtree.txt, native-ram-flatview.txt,
native-ram-release.txt, raw PCI/ACPI snapshots, build/input hashes, serial/QMP.

`NativeRam.Tests.ps1` без параметров запускает новый полный wrapper run;
SummaryPath явно включает проверку существующего evidence.

## Открытые gates

Reset-aware copy и production reset authority, DMA/cache retirement, independent
trust provisioning, durable antirollback и BI/access policy не квалифицированы.
Этот historical run использовал минимальный lab профиль с нулём records и fixture
reset fields; он superseded для importer seam отдельным QCXL-01 run: current
producer emits 160 bytes and one development `Security: Required|EvidenceOnly`
record. Ни один из двух профилей не является полным списком resources/ownership
для kernel handoff.

Native allocation/copy/BootInfo fault cleanup теперь проверен отдельными
[live fault cases](NATIVE-RAM-FAULTS-RU.md). Максимальный payload и exact-end
boundary теперь прошли [native qualification](NATIVE-BOUNDARY-RU.md), файловые
отказы — [file matrix](NATIVE-FILE-FAILURES-RU.md). Это не exhaustive fault/address
coverage. Full SingBoot boot не объявляется успешным.

RuntimeKernelExecuted=false, BootHandoffPerformed=false,
GuestResetAwareCopyQualified=false, GuestBootInfoFullHandoffQualified=false.
