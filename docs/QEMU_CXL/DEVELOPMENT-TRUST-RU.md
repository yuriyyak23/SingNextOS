# Повторно используемый development signer и статическая политика EFI

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

```powershell
# Один раз: новый каталог; существующие ключи никогда не заменяются
./tools/qemu-cxl/Initialize-CxlDevelopmentIdentity.ps1

# Тот же signer в независимых QEMU-процессах
./tools/qemu-cxl/Test-CxlCapsuleAccess.ps1 -WithEfiCrypto `
  -IdentityDirectory ./CXL/trust/development `
  -ImageGeneration 3 -MinImageGeneration 2 -TimeoutSeconds 600

./tools/qemu-cxl/Test-CxlCapsuleAccess.ps1 -WithEfiCrypto `
  -IdentityDirectory ./CXL/trust/development `
  -ImageGeneration 4 -MinImageGeneration 2 -TimeoutSeconds 600
```

Initializer создаёт P-384 PKCS#8 development-private.pem, публичный SPKI PEM и
identity.json. Это development key, сохранённый как незашифрованный PEM, не
production enrollment или аппаратный trust root. Не переносите private PEM
на guest FAT и в CXL. Каталог по умолчанию CXL/trust/development исключён из Git
вместе с остальными локальными CXL artifacts. Вывод содержит только public key ID.

Загрузка identity сверяет кривую NIST P-384, наличие private key, соответствие
public/private SPKI и ID metadata. Несогласованная identity отвергается до запуска VM.
Initializer никогда не перезаписывает существующий каталог или ключи.

Образ подписывается сохранённым ключом. Public key читается из отдельной identity,
включается в EFI trust.h и не берётся из капсулы. ProductionProvisioning и
GuestTrustRootIndependentlyProvisioned остаются false: host/compiler/EFI остаются
доверенными участниками lab. Secure Boot цепочка и защищённое обновление ключей
не реализованы.

MinImageGeneration встраивается в EFI как uint64 constant. Образ с поколением
ниже неё отвергается. Это static minimum policy, **не durable anti-rollback**:
нет монотонного счётчика, сохранения принятого поколения, защищённого состояния
или запрета повторной загрузки ранее принятого образа выше floor. Другой EFI с
меньшим floor также не предотвращается данной лабораторной схемой.

## Испытания политики

При явной identity qualification записывает целые корректно подписанные образы:

* foreign — другое ключевое семейство подписанта; корректная подпись для его
  собственного ключа, но guest отвергает его относительно provisioned public key;
* stale — подпись доверенного signer, поколение MinImageGeneration-1; создаётся
  только при floor > 1, обязан быть отвергнут;
* floor — тот же signer, поколение точно MinImageGeneration; при текущем образе
  с более высоким поколением отдельно должен быть принят.

Host независимо проверяет challenge signatures до VM. Guest Shell пишет каждый
образ в CXL, полный CPU readback проверяется перед EFI. После challenge исходная
капсула восстанавливается, readback восстановления проверяется. Private key не
копируется в output qualification; сохраняются только public artifacts и key ID.

Pester `EfiTrustPolicy.Tests.ps1` выполняет два независимых round-trip с одним
сохранённым identity, проверяет одинаковый public ID и неизменность private PEM,
отказ повторной инициализации, три guest challenge и отсутствие заявления
production anti-rollback / kernel execution.

Статический development floor/signature policy не independent provisioning или durable antirollback. Поздние native RAM/lifecycle tests — отдельные epochs; current trust/startup dependencies см. [primary blockers](blockers-singnextos/README-RU.md).

Host-side checks сохранены в CXL/logs/identity-host-checks-summary.json:
* сохранённый signer создал generation 3;
* проверка того же корректно подписанного образа с floor 4 отвергнута;
* несовпадение public/private/metadata отвергнуто до создания output capsule.
Все эти проверки выполнены отдельно от guest assertions.

Для создания/проверки капсулы этим signer без VM:

```powershell
$dll='./tools/SingNext.CxlLab/bin/Release/net11.0/SingNext.CxlLab.dll'
dotnet $dll create --identity ./CXL/trust/development --image-generation 3 --output ./CXL/new-signed-capsule
dotnet $dll verify --capsule ./CXL/new-signed-capsule/capsule.bin --public-key ./CXL/trust/development/development-public.pem --min-image-generation 2
```

Generation policy сейчас глобальная и статически скомпилированная; это не
per-domain protected counter. Generated lab rollback-domain UUID не является
источником защищённого состояния или доказательством monotonic updates.

## Реальные результаты 24 октября 2026

EfiTrustPolicy.Tests.ps1: 1/1 passed, 0 failed, 307.95 s.
Pester summary: CXL/logs/efi-trust-policy-pester-summary.json.
Сводка двух запусков: CXL/logs/development-trust-summary.json.

* Generation 3: CXL/logs/capsule-qualification-f73b6cb2e9294f33bb49a02e8b27fcd9.
* Generation 4: CXL/logs/capsule-qualification-cfef0c11dfbb414d89d1d612ac71e2ea.
* Public SPKI SHA256 в обоих:
  29909476064DDA1A78455526BB01DBDE1D5616938E7BFEFCB5E0330A581E9DFF.
* Оба Passed=true; foreign/stale rejected, exact floor accepted.
* Pester проверил одинаковый key ID, неизменность private PEM и отказ reinitialize.
* Final Release build: 0 warnings/errors; selftest включая generation floor и
  8 host admission rejection cases — passed. Private key/D buffers, экспортируемые
  для валидации identity, очищаются после использования.
* В конце recorded run QEMU процессов не оставалось. Firmware/SingBoot/RuntimeKernel source не менялись.

Изменения этапа: новый DevelopmentIdentity.cs, Initialize-CxlDevelopmentIdentity.ps1,
EfiTrustPolicy.Tests.ps1 и этот отчёт; дополнены LabCapsule/CapsuleAdmission,
Qualification/Program, Test-CxlCapsuleAccess, Build-CxlEfiReader и efi/Crypto.c.
Поздние additions identity-public-inputs.json и передачи host admission floor
проверены финальной сборкой/selftest; перечисленные VM runs предшествовали этим
небольшим host-side additions. Они не заявляются повторным гостевым испытанием.
