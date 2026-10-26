# Отдельный тестовый EFI reader

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

Пользователь разрешил отдельный EFI verifier 24 октября 2026. Разрешение не
включает изменение установленной EDK2 или исполнение RuntimeKernel. Первый
исполняемый этап — bounded reader и проверка полной byte identity, **не проверка
ECDSA подписи внутри гостя**. Это не готовый SingBoot.

```powershell
./tools/qemu-cxl/Test-CxlCapsuleAccess.ps1 -WithEfiReader -TimeoutSeconds 240
```

После обнаружения HPA создаётся новый каталог сборки. Установленный MSVC x64
собирает `tools/SingNext.CxlLab/efi/CxlRead.c` без CRT и библиотек Windows.
Проверяются PE32+, machine x64, subsystem EFI application, entry и отсутствие
import table. `expected.h` содержит HPA/offset, длину и все ожидаемые байты
лабораторной капсулы. Это временный oracle для интеграции, не production формат
provisioning ключей. `build.json` сохраняет хеши исходника/образа/EFI и compiler path.

Приложение называется CXLREAD.EFI, запускается явно установленной EDK2 Shell с
отдельного FAT-диска. Оно читает CXL CPU loads в статический RAM snapshot,
проверяет границу 256 МиБ и ограничение 72 КиБ, сравнивает байты и очищает snapshot.
Не программирует декодеры, не изменяет firmware/NVRAM template, не вызывает
ExitBootServices и не передаёт управление payload. HDM настраивается гостевой Shell.

Ожидаются markers `CXL_EFI_READER_ENTER`, `CXL_EFI_IMAGE_MATCH`.
Затем Shell меняет один бит payload; повторный запуск обязан выдать
`CXL_EFI_IMAGE_MISMATCH`. Исходное слово восстанавливается в finally.
`CXL_EFI_SIGNATURE_NOT_VERIFIED` печатается явно. Поля результата:
GuestEfiReaderExecuted, GuestEfiByteIdentityVerified, GuestEfiDamagedImageRejected.
GuestSignatureVerification остаётся false.

Этот reader-only epoch не проверял подпись. Поздний guest SHA384/ECDSA verifier описан в [crypto report](EFI-CRYPTO-RU.md); production trust/startup blockers — только в [актуальной очереди](modernize/README-RU.md).

Живой тест `CXL/logs/efi-reader-live-second` прошёл: приложение было действительно
загружено и исполнилось под установленной EDK2; первое чтение дало IMAGE_MATCH,
после изменения бита payload — IMAGE_MISMATCH. Затем слово восстановлено,
полный round-trip и cold restart завершились с Passed=true.
GuestSignatureVerification=false; GuestEfiReaderExecuted=true;
GuestEfiByteIdentityVerified=true; GuestEfiDamagedImageRejected=true.
Первый запуск остановился при сборке из-за унаследованного PSModulePath между
PowerShell 7/5.1; исправлен явный импорт штатного Utility модуля из PSHOME.

Новый исходник: `tools/SingNext.CxlLab/efi/CxlRead.c`.
Сборка: `tools/qemu-cxl/Build-CxlEfiReader.ps1`.
Регрессия: `tools/qemu-cxl/EfiReader.Tests.ps1`.
Гостевой bounded source: `tools/SingNext.CxlLab/ShellCapsuleSource.cs`.
В Qualification/Program/Test-CxlCapsuleAccess добавлен явный opt-in WithEfiReader.
Обычный режим не создаёт EFI. Установленные firmware-файлы не менялись.

Pester EfiReader.Tests.ps1: 1/1 passed, 0 failed, 65.63 s.
`CXL/logs/efi-reader-pester-summary.json`; полный прогон:
`CXL/logs/capsule-qualification-8d9df8eed67747b28a61b9838028a1c7`.
CapsuleAccess.Tests.ps1 с bounded Shell source: 2/2 passed, 65.57 s
(`CXL/logs/shell-admission-pester-summary.json`). В конце recorded run QEMU процессов не оставалось.
