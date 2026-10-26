# Identify capacity gate перед native capsule pipeline

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
./tools/qemu-cxl/Test-CxlNativeCapsule.ps1 -WithRamPreparation -WithMailboxGate -WithRelocation
```

Opt-in WithMailboxGate объединяет discovery -> native Identify 0x4000 -> capacity
policy -> HDM -> FAT provisioning в fresh CXL -> guest SHA384/ECDSA -> System RAM
preparation -> source Retire в одном CXLHDM.EFI. Default без флага остаётся прежним.

Native gate проверяет, что total и persistent-only capacity равны ожидаемым
256 MiB, volatile-only=0 и LSA=1 MiB. Он выполняется до NativeHdmExercise:
ошибка mailbox или policy mismatch блокирует вход в HDM/provisioning path.
Original boot interfaces, SingBoot, RuntimeKernel и installed EDK2 не меняются.

Host переиспользует общий NativeMailboxDiagnostic.Verify для независимого
декодирования raw response, сравнения с QMP payload/registers и managed BAR/DVSEC
resolver. В RAM mode это выполняется во время held allocation, после source
Retire, до возможного reset; старые mailbox results не перечитываются после reset.

## Реальные результаты

CXL/logs/native-capacity-gated-capsule/summary.json: четыре fresh VM cases passed:

1. Valid capsule, Identify gate passed, native HDM/crypto/RAM preparation passed.
2. Damaged signature rejected после successful capacity gate.
3. Same-binary relocation: 6144 MiB RAM / bus 24 +4 EDU, gate/crypto/RAM passed.
4. Capacity policy mismatch: compiled expectation 512 MiB против actual 256 MiB.

Для policy mismatch **явно собирается другой EFI binary** с ExpectedPmemMiB=512,
но той же source capsule/public key. Это не изменение actual QEMU capacity и не
инъекция аппаратного отказа. Native transcript сохраняет expected/actual sizes
и CXL_NATIVE_MAILBOX_FAILED STAGE=7. HDM enter, capsule provisioning и RAM READY
отсутствуют. Host подтвердил нули global/base/size/control/target/skip всех трёх
decoders и отсутствие direct alias. Passed=true означает ожидаемый отказ.

Evidence: valid, bad-signature, relocated, capacity-mismatch внутри указанного
каталога; efi-build и mismatch-efi-build имеют разные policy hashes. В positive
cases сохранены identify-response.bin, device-registers.bin, native-ram-ready.txt,
owned RAM/BootInfo, полный QMP/serial. В mismatch — mailbox-capacity-rejected.txt,
mailbox-rejected-registers.json и retired-mtree.txt.

Pester 3.4 / Windows PowerShell 5.1: **3/3**, assertions сохранённого live run,
не дополнительные VM executions. Summary: pester-evidence-summary.json.
NativeCapacityGate.Tests.ps1 без параметров запускает новый полный wrapper run;
SummaryPath явно включает existing-evidence assertions.

## Границы

Wire opcode остаётся 16-bit; прежнее указание на `IBootCxlTransport byte API`
superseded QCXL-02: shared contract теперь принимает `ushort` без token adapter
или truncation. Native gate всё так же не является guarded shared transport.
Нет production DMA isolation authority,
concurrent mailbox owner protocol или guest reset authority. RAM pages после
наблюдения очищаются/освобождаются; handoff buffer для kernel не сохраняется.

Текущий combined run использует fixture payload 256 bytes. Max-payload/exact-end
были квалифицированы отдельно до включения capacity gate, а не этим прогоном.
RuntimeKernelExecuted=false, BootHandoffPerformed=false. Identify metadata не
доказывают file persistence RAM backend и не означают полную CXL 3.x поддержку.
