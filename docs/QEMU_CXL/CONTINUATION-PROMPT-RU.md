# Продолжение QCXL activation work

Current scope/state: **26.10.2026**. Это инструкция для будущей разрешённой работы, не команда возобновить blocked implementation. Docs-only запрос выполняется без build/restore/VM.

## Цель и источник очереди

Senior systems/.NET runtime work: QEMU CXL Type-3, x86-64 UEFI, capability/lifetime security. Цель — actual native startup до intended runtime consumer с current authority/reset/ownership, а не return-to-Shell или fixtures.

Основной код `C:\Users\Yuriy\Desktop\SingNextOS`; связанный ISE `C:\Users\Yuriy\Desktop\HybridCPU ISE`. GitHub не source текущего code. Сначала applicable AGENTS, user scope и resume authorization; затем:

1. [Primary blockers](blockers-singnextos/README-RU.md): SNO-02 → SNO-01 → SNO-03; SNO-04 implementation FutureGated.
2. [Canonical QCXL queue](modernize/README-RU.md), [current state](CURRENT-STATE-RU.md), [source map](SOURCE-MAP.md), [ISE dependencies](blockers-hybridcpu-v2-ise/README-RU.md).
3. [Evidence](evidence/README-RU.md), [environment](ENVIRONMENT-RU.md), selected consumer/contract. Исторические reports читать по конкретной необходимости, не как новую очередь.
4. Applicable [global backlog](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md), [order](../SingNextOS-v6-modernize/02-IMPLEMENTATION-ORDER.md), [dev policy](../SingNextOS-v6-modernize/03-DEV-VALIDATION.md), relevant owners/invariants.

## Уже решено: не реализовывать повторно

- CXLHDM.EFI: discovery/Identify0x4000/capacity gate/HDM, FAT→fresh CXL RAM, guest crypto, authenticated snapshot/RAM/HBI1 preparation/cleanup.
- QCXL-01: HBI1 160-byte development Required/EvidenceOnly Security record → existing strict importer → ProviderRefused. HBV1 — volume, не BootInfo.
- QCXL-02: ushort shared mailbox contracts/callers; не возвращать byte alias/cast.
- **A явно разрешён/implemented:** external CxlLifecycleLab, private COM2/per-VM pin, whole-VM termination. Existing NativePreparationOwner adopted до PCI/CCI и до snapshot/RAM. [Contract](blockers-singnextos/QCXL-03-NATIVE-PROVIDER-SCOPE-RU.md). Не создавать второй native owner и не спрашивать снова reset policy A.
- V3 native RAM, actual EFI callback reset windows, pending EDU DMA reset/provider loss/independent VM peer и HCI-01B exact high-HPA read/write/revoke/fault проверены в отдельных input epochs. Counts/scopes только из evidence index; не превращать их в end-to-end runtime pass.
- **N1 design явно разрешён/завершён:** [target/ABI draft](blockers-singnextos/SNO-01-N1-TARGET-ABI-RU.md). Это proposed ABI, не executable implementation или EBS permission.

## Текущий blocker / разрешённый следующий шаг

Implementation goal blocked: actual freestanding x64 guest runtime/PAL/bootstrap отсутствует; пользователь подтвердил отсутствие external artifact. Concrete kernel memory/platform producers и original aperture proof также Missing. Для дальнейшей implementation нужен real artifact/owner либо новый отдельно разрешённый substrate scope. N1 не разрешает runtime port, original boot edits, EBS/RuntimeKernel execution. Не подставлять Windows NativeAOT/host-debug binary, HybridCPU ISA image, C/model importer или host tokens.

SNO-01 reuse Create/Import → HybridBootAuthorityAdmission после появления четырёх real inputs: actual HBI1 physical tuple; authoritative kernel NormalRam copy/current epoch/bounds/image-overlap denial; fresh endpoint provider; original aperture retire/quarantine owner. Lab ACK сейчас clear/free, не kernel transfer.

SNO-03 reuse manifest/keyset/role/epoch/revocation и ProtectedBootStateMachine/IBootProtectedState. Independent provisioning и authenticated durable monotonic provider отсутствуют. TPM2 NV/trusted storage — research candidates, не принятое решение. Development key/static floor/HMAC/VARS/warm RAM не substitute.

Только после actual prerequisites выбирать dependency-ready authorized consumer, называя producer, consumer, admission, effect, publication и closure/reclaim. Unknown/stale/copy/foreign/session/address/reset/partial/lost ambiguity запрещает read/free/consume/reuse; original pins сохраняются. Не запускать ещё одну проверенную lab серию вместо отсутствующего producer.

## Scope и среда

Разрешены SingNextOS lab adapters/test EFI, external ISE tools/controlled DMA workloads в рамках A и N1 design. Не разрешены ISE arch/core/ISA, original SingBoot/DirectBootCapsule/BootCapsuleEntry.Run/RuntimeKernel changes/execution, implementation нового guest ABI без отдельного решения. Firmware replacement/rebuild, Linux/WSL install, собственный CXL driver, BCD/Group Policy/security/global ExecutionPolicy changes запрещены. Production BOOTX64.EFI не создавать; upstream messages/publications не отправлять. Subagents только по явному требованию пользователя/applicable instructions.

Windows11/PowerShell7, QEMU11.1.0 q35/TCG/4vCPU; accepted RAM4GiB/CXL256MiB/LSA1MiB/CFMW4GiB, RAM loss после VM exit разрешён. Installed EDK2/private VARS/Secure Boot off; WHPX/OS settings не менять. Native/combined и VT-d relocation profiles не смешивать. Exact launch и toolchain inputs — environment/report, не общая default command line.

## Verification discipline

Перед edit читать actual file; dirty changes не откатывать, git reset/clean/restore/commit не делать. Не менять shared inputs живого чужого runner и не завершать чужие processes. До dotnet — fresh live command lines/output ownership, refs/locks/assets/global drift; restore только missing assets/dependency delta. Далее --no-restore -m:1 -nr:false -p:UseSharedCompilation=false, absolute isolated outputs. Observation timeout не terminal/restart; проверять тот же original handle. Shared runtime delta требует applicable graph verification.

Executable delta: affected compile, meaningful positive/negative/reset/lifetime и fresh owned VM на exact source+dirty inputs; EFI/capsule/adopted binary/profile hashes, commands/exits/effect counts/cleanup/failed/pending/limits. Старые JSON/Pester не новый live pass; failed history сохранять. Cleanup только exact owned VM, scratch вне real DirectBootCapsule. Docs-only — semantic/diff/local-link review без executable runs/hash campaign.

DecoderRegistersDisabled ≠ CpuRouteClosed ≠ DmaAndOtherEffectsClosed. Local unmap не cache/DMA/remote closure; VT-d не закрывает CPU HPA route; snapshot не coherent live memory. Различать ConfigurationValid, QemuDeviceCreated, GuestCxlMemoryAccessible, guest crypto/RAM/HBI1 compatibility, runtime authority admission и kernel handoff. Software evidence не ProductionQualified.

Результат по-русски: изменённые source/docs/evidence, actual consumers/inputs/commands/exits, verification failures/pending, cleanup, limits и concrete prerequisite. Обе очереди менять по фактам; docs correction явно без нового behavior closure.
