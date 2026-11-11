# Реальный PrepareKernelResetAware и границы handoff

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
./tools/qemu-cxl/Test-CxlNativeCapsule.ps1 -WithRamPreparation -WithRamReset
```

Reset-held scenario теперь вызывает **неизменённый** managed
DirectBootCapsule.PrepareKernelResetAware. Источник — payload и минимальный
BootInfo, реально прочитанные из выделенной guest System RAM до reset.
Destinations дополнительного опыта — отдельные **host .NET arrays**.
Это не исполнение managed метода внутри EFI и не передача управления ядру.

## Проверенные исходы

1. Stable epoch: успешная verified copy и BootInfo, совпадающий с guest output.
2. На втором Observe QMP observer выполняет реальный system_reset и ждёт новый
   firmware startup/Shell. Reset происходит после host copy/BootInfo emission,
   перед заключительной проверкой существующего метода. Результат ResetObserved;
   оба host destination spans полностью обнулены.
3. Уже устаревший expectedReset отвергается на первом Observe, до копирования.
   Контрольное содержимое caller buffers остаётся нетронутым: API не обещает
   обнуление произвольных caller-owned buffers при precondition refusal.
4. Старый RAM witness затем rejected/quarantined. После reset old guest allocation
   не читается, ACK/FreePages через старый контекст не выполняются.

Очистка host arrays не доказывает очистку guest allocation после reset.

## Fixture и evidence

Native diagnostic не предоставляет production mapping ownership generation.
Component test использует явно synthetic BootCapsule/Image/MappingGeneration=1,
аналогично предыдущему host LabCapsule verifier. Они не выдаются гостю как
authority. SyntheticGenerationFixture=true сохраняется в evidence и проверяется.

CXL/logs/native-core-reset-contract/summary.json: three fresh VM cases passed
(normal, bad signature, reset-held). В reset-held сохранены core-reset-contract.json,
core-reset-host-destination.bin и core-reset-host-boot-info.bin, pre-reset RAM,
полные QMP/serial. Host destination files после late reset — непустые нулевые buffers.

Pester 3.4 / Windows PowerShell 5.1: **6/6**, NativeRamReset.Tests.ps1 и
CoreResetContract.Tests.ps1 на сохранённых live results, не дополнительные VM runs.
Summary: CXL/logs/native-core-reset-contract/pester-evidence-summary.json.

## Production gates: source review

По локальным неизменённым contracts:

* HybridCpuBootSession требует EstablishDenyByDefault и current ненулевую DMA
  isolation generation до PCI/mailbox. UnsupportedHybridCpuBootHardware отказывает.
  Native lab diagnostic не устанавливает такую isolation authority.
* PrepareKernelResetAware проверяет reset вокруг copy, но не устанавливает DMA
  policy/mapping ownership: получает trusted inputs и generations от вызывающего
  кода. Ненулевые fixture numbers не доказывают authority.
* HybridBootInfoBoundedWriter допускает пустой Records list. Encoding envelope
  не доказывает полный resource/ownership handoff.

Это source review требований, не выполненный VM DMA isolation test. Нужны реальный
session-bound transport/reset/DMA contracts, обоснованный BootInfo resource set,
kernel executable ABI и отдельно разрешённый переход управления. Production
interfaces не ослаблялись ради lab success. HostDebugBootPipeline не запускается.

GuestResetAwareCopyQualified=false, GuestBootInfoFullHandoffQualified=false,
ProductionBootAuthorityEstablished=false, RuntimeKernelExecuted=false,
BootHandoffPerformed=false. Полная загрузка SingBoot остаётся открытой целью.
