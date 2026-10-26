# Native HDM + DirectBootCapsule guest verifier

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
./tools/qemu-cxl/Test-CxlNativeCapsule.ps1 -WithRelocation
```

Скрипт создаёт **новую** non-executable fixture DirectBootCapsule через существующие
HBV1/SNB1 codecs, development P384 signer/public root, отдельный EFI build и fresh
VM для каждого case. Существующие данные и установленная EDK2 не изменяются.
Без WithRelocation выполняются positive и damaged-signature cases.

Один CXLHDM.EFI выполняет:

1. Native PCI/ACPI discovery, включая checksummed CEDT/CHBS/CFMW.
2. Native HDM Stage/Commit/Readback трёх hops EP -> RP -> Host.
3. Открытие `capsule.bin` на том же FAT device, с которого загружен EFI image,
   через LoadedImage.DeviceHandle, SimpleFileSystem и File.Read. Read-only mode;
   буфер 4096 bytes, ограничение полного образа 73728 bytes, проверка EOF и Close.
4. Provisioning файла в **нулевую** fresh CXL RAM область; отказ от overwrite
   ненулевого содержимого. Частичный/некорректный файл не считается капсулой.
5. Снимок из CXL CPU loads; SHA384, ECDSA-P384 и строгие lab-format checks
   существующего guest verifier. Public key compiled отдельно; образ не embedded
   как expected-byte array. Длина образа и offset остаются параметрами сборки.
6. Host QMP observer во время bounded handshake сравнивает CXL с FAT input,
   проверяет scratch sentinel, реальные HDM registers и alias.
7. Native retire/remap scratch gate; final retire и host-проверка отсутствия alias.

Файл FAT служит только лабораторным источником инициализации RAM-backed CXL.
Это не persistent firmware boot source. RuntimeKernel не исполняется,
ExitBootServices/boot handoff не выполняются. Payload fixture имеет 256 bytes.

Адрес чтения CXL берётся из native CFMW discovery. Встроенный verifier использует
runtime NativeMappedBase, а не фиксированный CxlBase. Один EFI binary применим
к baseline и relocated topology; TrustRoot/image length не меняются между cases.

## Отрицательный сценарий

В новом FAT input меняется бит P1363 signature по offset 6144; исходный capsule
и EFI остаются неизменными. Guest verifier обязан вывести signature/hash rejection,
не выдать AUTHENTICATED/MAPPED success, а native HDM path — снять mapping.
Host подтверждает cleared commit/status всех hops и отсутствие alias.
`Passed=true` отрицательного case означает **ожидаемый отказ**, не успешную подпись:
`ExpectedCapsuleRejection=true`, `GuestSignatureVerification=false`.

## Evidence и проверки

Первые отдельные live runs: `CXL/logs/native-capsule-first` и
`CXL/logs/native-capsule-bad-signature`. Единый reproducible wrapper run:
`CXL/logs/native-capsule-complete/summary.json` с valid, bad-signature и relocated
подкаталогами. EFI copies, source capsule, input/build hashes, FAT image,
native PCI/ACPI raw snapshots, serial/QMP, mapped/retired mtree сохранены рядом.

`NativeCapsule.Tests.ps1` без параметров запускает новый полный wrapper run.
SummaryPath явно переключает на assertions уже сохранённого live evidence.

## Оставшиеся ограничения

Это отдельное EFI verifier application, а не production SingBoot/driver.
Development trust не independent provisioning; generation floor статический.
Без WithRamPreparation поле GuestAuthenticatedPayloadCopiedToSystemRam=false.
Добавлен отдельный opt-in [native RAM preparation](NATIVE-RAM-RU.md):
NativeHdmCapsule + RamCopy поддерживаются, включая наблюдение RAM после Retire.
Это не ExitBootServices или передача управления ядру.

Этот capsule-only epoch не квалифицировал DMA/cache/reset authority, BI/coherency или runtime handoff. Поздние capacity/RAM/file/lifetime runs имеют отдельные [evidence scopes](evidence/README-RU.md), а не расширяют данный pass.

EFI file protocol declarations сверены с официальной
[UEFI 2.10, sections 13.4/13.5](https://uefi.org/specs/UEFI/2.10/13_Protocols_Media_Access.html).
Новые firmware или исходники GitHub не использовались.

Native EFI capsule integration passed the reproducible wrapper with three fresh
VM cases: valid signed fixture, damaged-signature rejection, same-image relocated
topology. Native guest reads capsule.bin via read-only EFI file protocol, stages
fresh CXL RAM, snapshots CXL bytes and verifies SHA384/ECDSA. QMP compared CXL
with input while mapped; final retire removed alias. No Shell MMIO writes/qtest.
Pester 3.4 assertions on saved live wrapper evidence: 3/3, not extra VM runs.
Evidence: CXL/logs/native-capsule-complete/summary.json and pester-evidence-summary.json.
Default fixture payload 256 bytes. Этот ранний capsule-only run предшествует [native RAM integration](NATIVE-RAM-RU.md);
RuntimeKernelExecuted=false and BootHandoffPerformed=false. Full isolation unqualified.
[Reproduction and scope](NATIVE-CAPSULE-RU.md).
