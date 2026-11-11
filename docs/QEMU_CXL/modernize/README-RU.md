# QCXL: canonical consumer queue

**26.10.2026.** Сначала [SingNextOS blockers](../blockers-singnextos/README-RU.md), затем dependency-ready consumer здесь. Исторические отчёты и global v6 backlog не копируются в новую очередь. Current facts: [state](../CURRENT-STATE-RU.md); qualification scopes: [evidence](../evidence/README-RU.md).

Цель — native startup до intended runtime consumer с real authority/lifetime; fixtures, API availability и return-to-Shell не closure.

| ID | Producer → consumer | Статус | Следующий prerequisite |
|---|---|---|---|
| QCXL-01 | EFI HBI1 → HybridBootInfoImporter | VerifiedExisting, lab compatibility | 160-byte development evidence достигает ProviderRefused; не повторять seam |
| QCXL-02 | ushort transport → mailbox/session | Representation VerifiedExisting | Managed current hardware/provider adoption отдельно; byte alias запрещён |
| QCXL-03 | A producer → NativePreparationOwner | Partial: native lab mapping/crypto/snapshot/RAM/lifetime verified | Intended runtime owner/provider adoption; второй native owner не нужен |
| QCXL-04 | Platform isolation → HybridCpuBootSession | Native C lab Partial; managed path ExternalBlocked | Real guest runtime + concrete IHybridCpuBootHardware/current generations |
| QCXL-05 | Actual physical HBI1 tuple → runtime adapters | ExternalBlocked | Native startup и четыре real input owners ниже |
| QCXL-06 | Fresh endpoint → HybridBootAuthorityAdmission | ExternalBlocked by05/provider/aperture | Real provider-backed admission; firmware identities immutable evidence |
| QCXL-07 | Independent trust/state → existing consumers | Missing / scope-blocked | Independent key/revocation provisioning + authenticated durable monotonic provider |
| QCXL-08 | BootCapsuleEntry.Run → composition | Implementation scope-gated | Real owners и отдельное executable authorization |
| QCXL-09 | Retained RAM/HBI1 → kernel entry | Implementation FutureGated; N1 design completed | Supported runtime substrate, original aperture proof, separate EBS/kernel execution authorization |
| QCXL-10 | Durable backend | Conditional | Только если cold persistence станет требованием |

## Текущая остановка

**Implementation blocked.** N1 design разрешён и [завершён](../blockers-singnextos/SNO-01-N1-TARGET-ABI-RU.md); существующего freestanding x64 guest runtime/PAL/bootstrap нет, что подтвердил пользователь. Windows NativeAOT host binary или HybridCPU ISA image не выбранный native substrate. Concrete kernel/platform producers и original aperture proof также отсутствуют. Нужен real artifact/owner либо новый разрешённый substrate scope; новый lab pass не заменит producer.

QCXL-05 требует: actual loader physical address/length; kernel-owned NormalRam copy с current epoch/bounds/image-overlap denial; fresh endpoint provider; original aperture retire/quarantine owner. Reuse existing Create/Import → authority revalidation, [caller audit](../QCXL-05-CALLER-AUDIT-RU.md). Quarantined aperture обязана deny.

## Уже принятые решения

- [A](../blockers-singnextos/QCXL-03-NATIVE-PROVIDER-SCOPE-RU.md): external supervisor/COM2/whole-VM termination implemented в bounded lab. Reset policy повторно не выбирать. V3, callback windows и pending DMA evidence не runtime authority.
- [HCI-01B](../blockers-hybridcpu-v2-ise/README-RU.md#hci-01b--same-vm-dma--cxl-target): exact high-HPA route проверен; diagnostic grant permanently withdraws native admission. Не следующая незавершённая задача.
- [N1](../blockers-singnextos/SNO-01-STARTUP-DECISION-RU.md): design only; EBS/RuntimeKernel execution и original boot edits не разрешены.
- SNO-03 provider не выбран; TPM2 NV/trusted storage — research candidates, не implementation decision.

Для будущего разрешённого executable блока: actual producer/consumer, admission/effect/publication/closure, original receipt либо quarantine/pins; affected compile и meaningful fresh live behavior на exact inputs. Docs-only — semantic/diff/link review без build/restore/VM. [Dev policy](../../SingNextOS-v6-modernize/03-DEV-VALIDATION.md).
