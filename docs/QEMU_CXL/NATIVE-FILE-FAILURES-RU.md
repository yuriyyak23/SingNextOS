# Native capsule file failure matrix

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
./tools/qemu-cxl/Test-CxlNativeCapsule.ps1 -WithRamPreparation -WithFileFailures
```

Матрица выполняет пять fresh VM runs с одним EFI binary: valid/RAM preparation,
bad signature и три файловых отказа. Существующие файлы не перезаписываются.

| Input | Native reason | Bytes staged before rejection |
|---|---|---|
| Missing capsule.bin | 5: File.Open failure | 0 |
| Truncated by one byte | 6: read/early EOF | 8447 |
| One extra byte | 7: EOF check failure | 8448 |

Ожидаемый полный образ — 8448 bytes (fixture payload 256 bytes). Missing case
действительно не включает capsule.bin в FAT. Truncated/ExtraByte inputs сохранены
отдельно, исходная подписанная капсула остаётся неизменной. Native reason/read
markers проверяются host runner, а не только общий EFI error status.

Каждый отказ подтверждён до CXL_EFI_READER_ENTER, аутентификации и RAM READY.
Host после возврата EFI проверяет COMMIT/COMMITTED всех hops и отсутствие alias.
Passed=true такого case означает ожидаемый fail-closed результат:
GuestFileInputRejected=true, GuestSignatureVerification=false,
GuestAuthenticatedPayloadCopiedToSystemRam=false.

При усечённом/лишнем файле часть input уже записана в fresh CXL RAM до проверки
EOF. Этот тест **не** доказывает очистку частичных данных backing. Mapping закрыт,
данные не передаются verifier/kernel; вся тестовая VM завершается. Production
file-backed PMEM recovery/atomic provisioning этими опытами не квалифицируются.

## Реальные результаты

`CXL/logs/native-file-failure-matrix/summary.json`: пять cases passed.
Pester 3.4 / Windows PowerShell 5.1: **4/4**, assertions этого сохранённого live
evidence, не дополнительные VM runs. Summary: pester-evidence-summary.json.

`NativeFileFailures.Tests.ps1` без параметров запускает новый wrapper run;
SummaryPath явно включает проверку существующего evidence. В подкаталогах
Missing/Truncated/ExtraByte сохранены serial/QMP, reason/read transcript,
retired-mtree, source config/input/build hashes и private FAT image.

Single-case с уже собранным EFI:

```powershell
./tools/qemu-cxl/Test-CxlNativeHdm.ps1 -ExpectedFileFailure Missing `
  -EfiImage CXL/logs/native-file-failure-matrix/efi-build/CXLHDM.EFI
```

Для Truncated/ExtraByte нужно также задать CapsulePath соответствующего input.
Нельзя одновременно задавать ExpectedFileFailure и ExpectCapsuleRejection:
файловый отказ и криптографический отказ имеют разные checkpoints.

Это проверки логических input failures, не реальные media loss/DeviceError,
short-read transport races или Close failure. Native large-payload/boundary,
reset-aware/DMA/cache isolation и full SingBoot handoff остаются открытыми.
RuntimeKernelExecuted=false, BootHandoffPerformed=false.
