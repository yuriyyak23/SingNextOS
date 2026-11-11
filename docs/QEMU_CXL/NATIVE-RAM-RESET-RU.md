# Reset при удержании native prepared RAM

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
./tools/qemu-cxl/Test-CxlNativeCapsule.ps1 -WithRamPreparation -WithRamReset
```

Wrapper выполняет три fresh VM cases: normal preparation/release, bad-signature
rejection и reset во время удержания проверенного allocation. Используется один
EFI image и исходная signed fixture. Firmware и production boot contracts не меняются.

## Проверяемый lifecycle

В reset case native EFI уже проверил SHA384/ECDSA, подготовил payload/минимальный
BootInfo, снял CXL route и вывел RAM READY, но ещё не освободил LoaderData pages.
Host observer подтверждает owned pc.ram, payload/BootInfo/padding и отсутствие
alias. Reset sequence sampled до чтения и проверяется после него.

`LabRamPreparationWitness` связывает **host observation** с конкретным LabVm object,
наблюдаемым QMP reset sequence и SHA256 проверенного snapshot. Это не guest
capability, не production mapping generation, не разрешение запускать ядро и
не реализация IBootResetControl. Guid witness служит идентификатором evidence.

После успешной проверки witness в исходной эпохе host:

1. Не отправляет ACK приложению.
2. Выполняет реальный QMP system_reset, ждёт RESET event и новый startup/Shell.
3. Проверяет старый witness: ResetObserved. Повторная проверка: Quarantined.
4. Заново читает PCI/component topology и проверяет закрытый CXL route.
5. Завершает принадлежащую ему VM. Старый allocation не передаётся новому владельцу.

Ни старый payload/BootInfo, ни old allocation range после reset не читаются.
Не выполняются ACK/FreePages через контекст прерванного приложения. Поэтому
GuestRamReleaseCleared=false: очистка старых страниц после reset **не доказана**.
Firmware может переиспользовать System RAM; исторический snapshot не ownership.

## Реальные результаты

`CXL/logs/native-ram-reset-held/summary.json`: три cases passed.
Reset sequence 1 -> 2, fresh CXL_LAB_READY/Shell, старый witness rejected и
permanently quarantined. NativeRamPreparationCurrent=false.

Pester 3.4 / Windows PowerShell 5.1: **3/3**, assertions сохранённого live run,
не дополнительные VM executions. Проверка QMP journal после system_reset не
нашла pmemsave, пересекающих диапазон старого allocation.

Evidence в reset-held:

* pre-reset-ram-witness.json, native-owned-ram.bin и native-hybrid-boot-info.bin;
* native-ram-ready.txt, reset-firmware.txt, post-reset-discovery.json;
* native-ram-final-mtree.txt, полный serial и QMP journal;
* private launch/config/input hashes; native-ram-release.txt намеренно отсутствует.

NativeRamReset.Tests.ps1 без параметров выполняет новый wrapper run;
SummaryPath явно включает assertions существующего evidence.

## Границы результата

Source alias был удалён **до** reset. Этот опыт не исправляет прежнее наблюдение,
что reset активного mapping оставляет stale alias в данной QEMU сборке.
QMP event observation не является защищённой production reset authority и не
устраняет races между последней проверкой и потреблением данных.

В этом held-RAM epoch GuestResetAwareCopyQualified=false и GuestBootInfoFullHandoffQualified=false. Production handoff/authority не выполнялись. Поздние default-owner/A reset/callback/pending-DMA runs описаны отдельно в [evidence index](evidence/README-RU.md), не расширяют данный результат.

[Existing PrepareKernelResetAware qualification](CORE-RESET-CONTRACT-RU.md) distinguishes host destination cleanup from guest ownership and production DMA/reset authority.
