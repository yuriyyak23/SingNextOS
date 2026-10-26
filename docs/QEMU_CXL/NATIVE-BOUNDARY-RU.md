# Native maximum capsule / exact backing-end qualification

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

Новый native wrapper поддерживает PayloadPath, CapsuleOffset (bytes) и
AtBackingEnd. Последний вычисляет offset как capacity - actual image length.
Нельзя одновременно задавать AtBackingEnd и ненулевой явный CapsuleOffset.
Общий mapping остаётся whole-device 256 MiB; меняется положение самой капсулы.

## Выполненные проверки

| Значение | Факт |
|---|---|
| Payload | 65536 bytes, opaque non-executed test data |
| Полный HBV1/SNB1 образ | 73728 bytes |
| Backing capacity | 268435456 bytes |
| Capsule offset | 268361728 = 0x0ffee000 |
| Offset + image bytes | ровно 268435456, без выхода за предел |
| Baseline capsule HPA | 0x19ffee000 |
| Последний byte HPA | 0x19fffffff — последний byte 256 MiB mapping |
| System RAM allocation | 17 pages / 69632 bytes, включая BootInfo page |

Три fresh VM runs native RAM wrapper прошли: valid, damaged-signature rejection,
relocated topology (тот же EFI binary, 6144 MiB RAM / bus 24 +4 EDU).
RAM payload/BootInfo проверены, пока allocation ещё принадлежал EFI и CXL source
уже retired. Затем pages очищены/освобождены. Boot handoff не выполнялся.

Четвёртая VM использовала тот же EFI/capsule без RamCopy invocation: host QMP
прочитал полный активный CXL диапазон и сохранил native-cxl-observed.bin.
SHA256 и все bytes совпали с source capsule, включая конец backing. Это наблюдение
той же QEMU physical address space, не независимый CXL controller/flush proof.

Builder и native runner отдельно отвергли offset +1 byte за допустимый конец и
UInt64.MaxValue. Обе проверки выполняются до запуска QEMU; target VM/build output
не создан. Bounds проверяются вычитанием capacity-offset, без переполнения суммы.
Это host-side rejection, не тест произвольных out-of-range CPU stores в госте.

Evidence:

* CXL/logs/native-exact-end-ram/summary.json — три native RAM runs.
* CXL/logs/native-exact-end-observed/result.json — active CXL observer.
* CXL/logs/native-boundary-bounds-repro/result.json — четыре preflight rejections.
* CXL/logs/native-boundary-pester-summary.json — **4/4 Pester assertions** этих
  сохранённых результатов, не дополнительные VM runs.

Все четыре VM использовали EFI SHA256
`F9830203A68291CDE08524309ABBBAA497FAE70AE8488D9EF6C4869709D8E28C`.
Исходные hashes, exact launch, raw PCI/ACPI, serial/QMP, owned RAM и BootInfo
сохранены в evidence directories. Тестовые VM завершены.

## Воспроизведение

Создайте новый 65536-byte opaque payload file. Например, в **новом** lab каталоге:

```powershell
$lab=Join-Path $PWD ('CXL/logs/native-boundary-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $lab | Out-Null
$payload=New-Object byte[] 65536
for($i=0;$i -lt $payload.Length;$i++){$payload[$i]=[byte](($i*37+11) -band 255)}
$payloadPath=Join-Path $lab 'payload.bin'
[IO.File]::WriteAllBytes($payloadPath,$payload)
$ram=& ./tools/qemu-cxl/Test-CxlNativeCapsule.ps1 -PayloadPath $payloadPath `
  -AtBackingEnd -WithRamPreparation -WithRelocation -OutputDirectory (Join-Path $lab 'ram')
$physical=& ./tools/qemu-cxl/Test-CxlNativeHdm.ps1 `
  -EfiImage (Join-Path $lab 'ram/efi-build/CXLHDM.EFI') `
  -CapsulePath (Join-Path $lab 'ram/fixture/capsule.bin') `
  -CapsuleOffset $ram.CapsuleOffsetBytes -OutputDirectory (Join-Path $lab 'physical')
$bounds=& ./tools/qemu-cxl/Test-CxlNativeBounds.ps1 `
  -CapsuleRunDirectory (Join-Path $lab 'ram') -OutputDirectory (Join-Path $lab 'bounds')
Invoke-Pester -Script @{Path='./tools/qemu-cxl/NativeBoundary.Tests.ps1';Parameters=@{
  RamSummary=(Join-Path $lab 'ram/summary.json')
  PhysicalResult=(Join-Path $lab 'physical/result.json')
  BoundsResult=(Join-Path $lab 'bounds/result.json')}}
```

NativeBoundary.Tests.ps1 требует явные evidence paths; самостоятельно VM не
запускает. В отличие от него три Test-* scripts выше выполняют live/preflight work.

## Границы вывода

Это exact-end case и maximum lab payload, не exhaustive address-space testing,
production capacity mailbox qualification или persistence. Small/offset-zero cases
проверены ранее. File failures при max payload не объединялись с этим прогоном.
RuntimeKernelExecuted=false, BootHandoffPerformed=false; DMA/cache/reset-aware
ownership, production trust и полный HybridBootInfo handoff остаются открытыми.
