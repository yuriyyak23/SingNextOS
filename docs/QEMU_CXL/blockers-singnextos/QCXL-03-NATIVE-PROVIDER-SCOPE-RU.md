# SNO-02 / QCXL-03: принятый bounded lifecycle contract A

**26.10.2026. A явно разрешён и implemented в native lab.** External CxlLifecycleLab supervisor, отдельный COM2, per-VM pinned public key и whole-VM termination. Это не нерешённый reset-policy request. Intended managed/runtime adoption остаётся Partial/ExternalBlocked.

## Producer и existing consumer

[External supervisor](<../../../../HybridCPU ISE/Tools/CxlLifecycleLab/README-RU.md>) → [NativeLifecycleClient](../../../tools/SingNext.CxlLab/efi/NativeLifecycleClient.c) → [NativePreparationOwner](../../../tools/SingNext.CxlLab/efi/NativePreparationOwner.c) → existing discovery/mailbox/HDM/authenticated snapshot/RAM/cleanup. Второго native owner/EFI composition нет.

Supervisor владеет exact original QEMU Process handle/Job, private channels и outstanding obligations вне EFI lifetime. Native owner владеет original allocation/snapshot/effect metadata. Windows Job kill-on-close, private COM1/COM2 pipes с server PID/DACL checks, private QMP stdio; reset в accepted profile ведёт к terminal exit, не firmware reentry. Per-VM key/sequence/pointers связывают запрос, но сами не mint authority.

| Domain | Current producer / effect |
|---|---|
| VM incarnation / reset boundary | Exact original VM, private per-VM key/channel, terminal process completion; no reattach/migrate/restore adoption |
| Isolation generation | Existing VtdGrantOwner actual same-VM TE-on allow → retire/context+IOTLB completion → deny faults; exact endpoint/requester scope |
| Mapping operation/generation | Original intent до effect; actual HDM commit + native CPU write/read witness до generation commit |
| Original obligations | External retention + original native record/pages; no reset firmware dereference/reuse after lost lifetime |

## Admission, effect, publication, closure

Admission требует current original owner/VM/endpoint и distinct reset/isolation/mapping domains. Effect — native mailbox/mapping/allocation. Publication — authenticated copied snapshot/owned RAM после revalidation. FreePages dispatch только для original cleared allocation после currentness checks; release publication только после known successful callback outcome; ambiguous/lost/partial/stale/foreign state запрещает read/free/consume/reuse.

В V2/V3 isolation/mapping profiles original19-page record/platform12 pages и unresolved source obligations pinned до exact VM terminal. Default native record после EFI return имеет только unchanged-firmware lifetime; historical RESET_AUTHORITY=0 не platform capability. Whole-VM A не сохраняет старый LoaderData через warm reset и не доказывает physical/cache/remote closure.

При callback ambiguity scratch RAM уже могла быть освобождена EFI до native receipt; повторные read/free/reuse запрещены, используется exact VM termination. Pins19/12 выше не означают доказанное удержание уже freed scratch.

V1 — signed refusal до PCI. V2 — effect-backed PCI/Identify, MappingGeneration0 отказывает HDM. V3 — original mapping → existing crypto/snapshot/RAM/HBI1/cleanup. Actual callback reset windows и pending EDU/provider-loss workloads расширяют только свои bounded evidence scopes. [Reports/epochs](../evidence/README-RU.md).

## Remaining boundary

Existing managed HybridCpuBootSession/IHybridCpuBootHardware adoption, native startup, concrete kernel/platform producers и original aperture proof отсутствуют. [N1 design](SNO-01-N1-TARGET-ABI-RU.md) разрешён/завершён, но guest runtime substrate отсутствует и пользователь это подтвердил. A/N1 не разрешают RuntimeKernel/EBS execution или ISE arch/core/ISA.

DecoderRegistersDisabled ≠ CpuRouteClosed ≠ DmaAndOtherEffectsClosed. Local unmap, signed reply, counter, reset observation и retained pins не заменяют appropriate closure. Same-VM warm reset требует другого external-to-reset ownership contract; это не выбранная A policy.

## Legacy anchor

<a id="concrete-decision-package-25102026-proposed--not-authorized"></a>

Ссылка сохранена для inbound compatibility. Исходный предложенный package superseded explicit choice A и implementation выше. История аудита: [разбор 25.10](../EXTERNAL-AUDIT-RECONCILIATION-20261025-RU.md); evidence/failed artifacts не переписывались.
