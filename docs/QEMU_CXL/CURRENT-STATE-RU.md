# Текущее состояние CXL boot

**26.10.2026.** Source/classification review, без нового executable pass. Каждый сохранённый результат относится только к своему input epoch; текущий dirty tree не получает qualification автоматически.

## Достигнутые границы

| Участок | Проверено | Предел |
|---|---|---|
| Existing CXLHDM.EFI | Discovery, Identify0x4000/capacity gate, HDM, CPU CXL.mem, guest SHA384/ECDSA, FAT→fresh CXL RAM, authenticated snapshot/RAM/HBI1 | Lab payload preparation; payload/kernel не исполнялись |
| QCXL-01 | 160-byte HBI1 с одной development Security Required/EvidenceOnly record → existing strict importer → ProviderRefused | Import compatibility, не runtime admission; HBV1 — volume magic |
| QCXL-02 | Shared mailbox ushort representation/callers | Byte alias не возвращать; managed hardware provider отдельно |
| A lifecycle/native owner | External supervisor/COM2, V1 refusal, V2 effect-backed native PCI/CCI, V3 original mapping → crypto/snapshot/RAM/cleanup | Бounded whole-VM lifetime; managed runtime consumer не adopted |
| EFI FreePages windows | Entry, one instruction inside callback, successful return до native receipt; exact terminal/read/reuse denial | Не arbitrary torn allocator или same-VM recovery |
| Pending EDU command | Real RUN/TE-on grant, busy retire/dispatch deny, reset/provider loss, independent VM peer → completion → native RAM | Pre-native-admission pending timer command, не mid-transaction physical DMA |
| HCI-01B | IOVA0x2000 → CXL HPA0x190200000 при EDU mask0x0fffffff: read/write integrity, retire/invalidation, fresh faults, old-grant ABA deny | Native admission permanently withdrawn до diagnostic grant; после него native snapshot/RAM0 |
| N1 | Target assessment, post-EBS x64 receiver/retained-read-loan ABI draft | Design only; supported executable runtime substrate не найден |

Reports, counts, binaries/hashes, failed attempts и отдельные epochs находятся в [evidence index](evidence/README-RU.md). Их нельзя суммировать в end-to-end pass.

## Оставшиеся blockers

- **SNO-02 Partial:** bounded native lab producer/consumer работает; intended managed/runtime adoption отсутствует.
- **SNO-01/QCXL-04/05/06 ExternalBlocked:** нет guest runtime/PAL/bootstrap (подтверждено пользователем), concrete kernel map/copy/epoch и fresh platform providers, original aperture proof. Host-debug entry/Windows NativeAOT/HybridCPU ISA artifact не substitute. [N1 assessment](blockers-singnextos/SNO-01-N1-TARGET-ABI-RU.md).
- **SNO-03/QCXL-07 Missing / scope-blocked:** independent key/revocation provisioning и authenticated durable monotonic storage не выбраны/не implemented.
- **SNO-04/QCXL-08/09 implementation FutureGated:** N1 разрешил design, а не retained handoff/EBS/RuntimeKernel execution.
- **QCXL-10 Conditional:** cold persistence не требуется принятому RAM profile.

Goal implementation blocked; документационная работа не возобновляет её. [Primary blockers](blockers-singnextos/README-RU.md), [canonical queue](modernize/README-RU.md).

## Инварианты интерпретации

DecoderRegistersDisabled, CpuRouteClosed и DmaAndOtherEffectsClosed — разные факты. Historical local decoder clear не закрыл CPU alias; host uncommit закрывал sentinel route в своём profile. Это не general physical/cache/remote closure и не атрибуция exact installed QEMU source.

A сохраняет original obligations/pins до exact owned VM terminal, old firmware record после reset не читается. Это не cross-reset сохранение/reuse внутри той же VM. RESET_AUTHORITY=0 относится к default/historical owner, не отменяет отдельный opt-in A contract.

EDU SID не Type-3 SID. Старые guard/peer subranges одного allocation не independent owner; поздние provider-loss pairs используют независимые VM. QMP sequence, pointer, nonce, counter, readback и host grant reference сами не authority. Snapshot не coherent live memory; development signer/static floor не independent antirollback.

ConfigurationValid, QemuDeviceCreated, GuestCxlMemoryAccessible, guest crypto/RAM/HBI1 compatibility, runtime authority admission и kernel handoff не взаимозаменяемы. BootCapsuleEntry.Run Unsupported — текущий fail-closed behavior, не build failure. ProductionQualified/full native startup не заявляются.
