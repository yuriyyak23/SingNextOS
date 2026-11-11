# Native RAM fault injection

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
./tools/qemu-cxl/Test-CxlNativeCapsule.ps1 -WithRamPreparation -WithRamFaults
```

Wrapper выполняет пять fresh VM cases: valid capsule, bad signature и три
RAM failures. Во всех RAM cases используется один EFI binary и один неизменённый
подписанный вход. Меняется только EFI command-line mode. Firmware не изменяется.

| Injection | Наблюдаемое поведение |
|---|---|
| --ram-copy-fail-copy | После authenticated copy меняется byte RAM payload; destination hash отклонён; весь allocation обнулён до observer/FreePages |
| --ram-copy-fail-info | Меняется BootInfo CRC; отказ; payload, BootInfo и padding обнулены до observer/FreePages |
| --ram-copy-fail-alloc | Искусственный отказ до AllocatePages; нет RAM READY; native cleanup снимает HDM mapping |

Это **контролируемые программные инъекции**, не реальные OOM, ECC failure или
физическое повреждение CXL. SHA384/ECDSA исходной капсулы проходят в RAM failure
cases: подпись и destination preparation имеют разные результаты.

## Evidence

`CXL/logs/native-ram-fault-matrix/summary.json`: все пять cases passed.
Здесь Passed=true для fault case означает ожидаемый отказ и подтверждённый cleanup:
ExpectedRamFailure=true, GuestRamFailureRejected=true,
GuestAuthenticatedPayloadCopiedToSystemRam=false.

Для copy/CRC failures host остановил VM, пока allocation оставался выделенным,
проверил pc.ram range, снятые COMMIT/COMMITTED всех hops, отсутствие alias и
нулевой полный native-owned-ram.bin. После ACK EFI подтвердил clear/FreePages;
после возврата host повторно проверил закрытое отображение. Содержимое после
FreePages не объявляется стабильным или принадлежащим приложению.

При allocation injection не создаётся фиктивный RAM buffer. Source retirement
проверяется после возврата приложения. Generic PCI_UNSUPPORTED_OR_INVALID marker
в этом случае отражает общий ненулевой EFI status после отказа RAM stage,
а не отдельное доказательство некорректных PCI capabilities.

Pester 3.4 / Windows PowerShell 5.1: **4/4**, assertions сохранённого live run,
не дополнительные VM executions. Summary: pester-evidence-summary.json.
`NativeRamFaults.Tests.ps1` без параметров запускает новый wrapper run;
SummaryPath явно выбирает существующий evidence.

Single-case reproduction с уже созданными EFI/capsule:

```powershell
./tools/qemu-cxl/Test-CxlNativeHdm.ps1 -NativeRamCopy -NativeRamMode CopyFailure `
  -EfiImage CXL/logs/native-ram-fault-matrix/efi-build/CXLHDM.EFI `
  -CapsulePath CXL/logs/native-ram-fault-matrix/fixture/capsule.bin
```

Другие NativeRamMode: Valid, InfoFailure, AllocationFailure.
NativeRamCopy обязателен для failure modes. Скрипты создают только новые output
каталоги и управляют только своими VM. Все VM текущего прогона завершены.

## Открытые проверки

Перечисленные fault cases этого раннего report не квалифицировали arbitrary allocator errors, DMA/cache concurrency или runtime handoff. Поздние boundary/file/default-owner/A callback/reset/loss tests перечислены в [evidence index](evidence/README-RU.md), каждый со своими limits. Current backlog здесь не задаётся.