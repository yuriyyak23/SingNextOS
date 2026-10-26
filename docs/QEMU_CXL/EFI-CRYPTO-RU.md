# Гостевой SHA-384 / ECDSA-P384 verifier

> Исторический input epoch: команды/counts/ограничения/cleanup ниже относятся только к recorded run. Это не current backlog или инструкция повторить тест. Команды — из корня repo; текущие решения: [state](CURRENT-STATE-RU.md), последующие результаты: [evidence](evidence/README-RU.md).

Режим включается явно, из корня локального SingNextOS:

```powershell
./tools/qemu-cxl/Test-CxlCapsuleAccess.ps1 -WithEfiCrypto -TimeoutSeconds 300
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -Command 'Invoke-Pester ./tools/qemu-cxl/EfiCrypto.Tests.ps1'
```

Создаётся отдельный CXLREAD.EFI, который запускается установленной EDK2 Shell.
Прошивка не изменяется. Это лабораторный verifier, не полный SingBoot, не загрузка
RuntimeKernel и не production qualification. Режим -WithEfiReader сохраняет
прежний тест сравнения байтов; -WithEfiCrypto выполняет именно криптографию.

## Исполняемая цепочка

1. Host создаёт подписанный SNB1/HBV1 образ и настраивает лабораторную VM.
2. Guest Shell программирует HDM и записывает капсулу CPU stores в CXL.
3. EFI читает её CPU loads в отдельный ограниченный RAM snapshot (до 73728 байт).
4. EFI выполняет SHA-384 known-answer test для `abc`.
5. Strict lab parser проверяет HBV1 CRC32C, UUID связи с SNB1, фиксированную
   лабораторную геометрию, длины, алгоритмы, платформу, ABI и optional lab TLV.
6. SHA-384 манифеста и ECDSA-P384 P1363 signature проверяются BearSSL внутри VM.
7. SHA-384 payload сравнивается с подписанным descriptor; padding проверяется.
8. Snapshot очищается. EFI возвращается в Shell, без ExitBootServices/handoff.

Ожидаемый маркер успеха — `CXL_EFI_SIGNATURE_AND_HASH_VERIFIED`. Host затем
изменяет payload, signature, header, manifest length и key ID в CXL; каждый
отрицательный запуск должен дать `CXL_EFI_SIGNATURE_OR_HASH_REJECTED`.
Изменённое слово восстанавливается в finally.

Гостевой verifier не сравнивает образ с встроенной эталонной капсулой. В режиме
crypto expected.h содержит только HPA/offset/length; доверенный public key и key ID
находятся в отдельном trust.h. Подпись проверяется по реально прочитанным байтам.

## Криптографическая зависимость

Используется [BearSSL 0.6](https://bearssl.org/) под MIT, загруженный только с
официального URL `https://bearssl.org/bearssl-0.6.tar.gz`.
SHA-256 архива:
`6705BBA1714961B41A728DFC5DEBBE348D2966C117649392F8C8139EFC83FF14`.

В `efi/vendor/bearssl-0.6` сохранён неизменённый subset: headers, i31 arithmetic,
prime EC, raw ECDSA verify, SHA384/512, codec. Лицензия сохранена. Хеш каждого
файла — в `efi/vendor/bearssl-provenance.json`; build проверяет их до компиляции.
Никакие TLS/network/RNG или OS services не компонуются. Сборка MSVC использует
скалярный C backend, отключает OS-specific intrinsics и CRT. Собственные минимальные
headers/shims ограничены типами и memcpy/memmove/memset/memcmp. /Gs999999 исключает
зависимость от Windows stack-probe runtime; stack usage отдельно для production
не квалифицирован. Проверяется отсутствие PE import table и EFI subsystem.
BearSSL автором обозначена beta; это не внешняя криптографическая сертификация.

## Trust и ограничения

Публичный P-384 ключ и SHA-256 SPKI key ID передаются build через отдельный 129-byte
development-trust-root.bin, не извлекаются из CXL. Private key в EFI не включается.
Однако signing key создаётся host fixture для данного прогона: независимое
production provisioning доверия, Secure Boot chain и anti-rollback отсутствуют.
Host, compiler и EFI binary находятся в доверенной части лабораторного эксперимента.

GuestSignatureVerification=true означает проверку подписи внутри EFI в данном
профиле. Это не равнозначно BootIntegrationReady, production policy, DMA isolation
или безопасному отзыву HDM. Оригинальные managed BootManifestVerifier и
DirectBootCapsule не изменялись; C parser — отдельный ограниченный lab profile,
который требует последующего сравнения с production contract и fuzzing.

HDM retirement остаётся false из-за сохраняющегося direct alias на локальной QEMU.
Один mapping используется на время процесса; uncommit не трактуется как revoke.
Поздние fixed P384 vectors и development signer выполнены в последующих разделах этого отчёта/[trust report](DEVELOPMENT-TRUST-RU.md); native RAM/lifecycle появились в более поздних epochs. Незавершённые production trust/startup dependencies перечислены только в [primary blockers](blockers-singnextos/README-RU.md).

## Доказательства

Первый успешный guest crypto run:
`CXL/logs/efi-crypto-live-second/result.json` и `efi-reader-*.txt`.
Точная сборка и ключ — в `efi-build/build.json`, `development-trust-root.bin`.
QEMU/firmware/config hashes — `inputs.json`, полная serial/QMP трасса — в каталогах VM.
Финальная регрессия strict parser сохраняется отдельно; не подменяйте её результатом
первого прогона, который предшествовал дополнительным проверкам HBV1.

При добавлении strict-parser negatives первый Pester прогон отказал на
manifest-length: тест пытался писать 8 байт по невыровненному адресу +12,
EDK2 Shell не применил изменение. Исправлено: запись по +8 с маской bit32.
Для всех guest mutations добавлен readback факта изменения перед запуском EFI,
чтобы неподтверждённая инъекция не выдавалась за испытание отказа криптографии.
Старый неуспешный прогон сохранён: CXL/logs/capsule-qualification-f6f8d33799ea43699fede9addddd77f9.

Strict parser regression after aligned-mutation fix:
`CXL/logs/capsule-qualification-2999fd55c19b4bcd996240559535e3a5`, Passed=true.
Pester EfiCrypto.Tests.ps1: 1/1 passed, 76.36 s
(`CXL/logs/efi-crypto-pester-summary.json`).

Добавлены независимые verification vectors RFC 6979 A.2.6 (`sample`, `test`,
P-384/SHA-384) в P384Vectors.c. Оригинал:
https://www.rfc-editor.org/rfc/rfc6979.html#appendix-A.2.6
Используются только public point и signatures, без private key/nonce.
Перед admission EFI теперь обязан проверить два положительных вектора и девять
отказов primitive: bad digest, short signature, r=0, s=0, r=n, s=n,
(0,0) off-curve point, short point, invalid point prefix.
Эти selftests не используют development capsule signing key.
Финальный прогон с ними сохраняется в отдельный efi-crypto-rfc-pester-summary.json.

Для воспроизводимости compiler/linker, generated headers, Crypto.c/Crt.c/
P384Vectors.c и freestanding headers теперь включены в build metadata. Компиляция
vendor C файлов идёт только по зафиксированному manifest, а не по произвольным
дополнительным .c в каталоге.

При параллельном большом прогоне стандартный output DLL был занят .NET Host.
Проверка новой ревизии собрана отдельно командой:
`dotnet build tools/SingNext.CxlLab/SingNext.CxlLab.csproj -c Release -o CXL/logs/host-rfc-build`.
Это 0 warnings/errors; активный большой прогон не останавливался. Отдельный итог
RFC qualification: `CXL/logs/efi-crypto-rfc-final`. Хеши точного host binary и EFI
сохранены в его inputs.json и efi-build/build.json. Логи failed build не означают
отказа гостевой криптографии.

## Итоги текущего этапа

* Maximum payload run: CXL/logs/efi-crypto-max-payload/result.json — Passed=true,
  payload 65536, image 73728, offset 240 MiB, HPA 0x19f000000;
  GuestSignatureVerification=true, GuestPayloadSha384Verification=true;
  все transport/signature/payload negatives пройдены. Этот образ EFI собран до
  добавления RFC KAT; его точный исходник идентифицирован в efi-build/build.json.
* RFC revision: CXL/logs/efi-crypto-rfc-final/result.json — Passed=true,
  два RFC known answers, девять primitive negative cases и пять повреждений
  капсулы прошли внутри EFI. RuntimeKernelExecuted/BootHandoffPerformed=false.
* Independent production trust root, anti-rollback и HDM retirement остаются false.
  Fixed P384 vectors проверены этим RFC epoch; криптографическая сертификация/production readiness не заявляются.

Финальный Pester input epoch этого отчёта: EfiCrypto.Tests.ps1 — 1/1 passed, 0 failed,
74.95 s. `CXL/logs/efi-crypto-rfc-pester-summary.json`; полный прогон
`CXL/logs/capsule-qualification-b8667f151cd14aa3988307379480b5f8`.
Он включает два RFC P384 positive vectors и девять primitive negatives,
а также 5 malformed/corrupt capsule cases. CLI отклоняет неопределённый crypto
mode и включение crypto без явного EFI builder до запуска VM.

## Reusable development trust

[Development identity and static generation policy](DEVELOPMENT-TRUST-RU.md)
adds an explicit persistent signer outside the capsule fixture. It qualifies
validly signed foreign/stale images and the exact floor boundary. The default
mode still generates a per-run development key. Neither mode claims production
provisioning, protected rollback state or a working RuntimeKernel handoff.
