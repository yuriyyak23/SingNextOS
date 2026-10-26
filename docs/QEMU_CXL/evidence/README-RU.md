# Evidence: exact scopes / input epochs

Индекс сверён26.10.2026 без новых executable runs. Каждый report относится к собственным sources/dirty inputs, dependencies/toolchain, EFI/capsule/adopted binaries/profile и terminal exits. Counts не суммируются в общий end-to-end boot pass. «Inputs unchanged» — результат сохранённого recheck данного epoch, не новая проверка всего current tree.

[N1 target/ABI design](../blockers-singnextos/SNO-01-N1-TARGET-ABI-RU.md) — отдельный source/spec result, не integration pass. Actual native runtime substrate/aperture proof Missing; execution/EBS не разрешены.

## Последние integration records

| Epoch / report | Проверено / limits |
|---|---|
| [HCI-01B26.10](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-dma-target-a-20261026/report.md>), [current observation](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-dma-target-a-20261026/route-aba/cxl-route-observation.json>) | Initial route +2 controls exit0; отдельная ABA delta route exit0, saved30/30 current-epoch inputs unchanged. Per-route8 diagnostic commands/2 revoke faults; native admission withdrawn, snapshot/RAM0 после grant |
| [Pending EDU DMA25–26.10](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-pending-dma-a-20261025/report.md>), [controls](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-pending-dma-a-20261025/control-exits.json>), [independent loss](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-pending-dma-a-20261025/independent-loss-result.json>) | Positive +6 controls exit0; отдельный two-VM loss workload pass, injected target supervisor−1 expected. Saved28/28 inputs unchanged. Actual RUN/pins до terminal; peer completion→native RAM. Не mid-transaction physical DMA |
| [EFI callback25.10](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-callback-a-20261025/report.md>), [exits](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-callback-a-20261025/final-exits.json>) |9 cases exit0, saved27/27 inputs unchanged. Actual entry/one instruction/return-before-native-receipt reset; не arbitrary torn allocator/same-VM recovery. Failed transport histories сохранены |
| [A V3 mapping/RAM25.10](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-mapping-a-20261025/report.md>), [exits](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-mapping-a-20261025/verified-exits.json>), [independent loss](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-mapping-a-20261025/independent-loss-result.json>) |18 cases exit0, saved26/26 inputs unchanged; separate independent VM published-backing loss test. Crypto/snapshot/RAM/HBI1; source pins до terminal, не runtime admission |
| [A V2 isolation25.10](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-isolation-a-20261025/report.md>), [exits](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-isolation-a-20261025/verified-exits.json>), [reset-guard delta](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-isolation-a-20261025/delta-exits.json>) |7 isolation+V1 cases; отдельный grant regression3VM/30commands/12faults; затем identify/reset-before-arm/V1 delta3cases. Отдельные27-input epochs. V2 mapping/RAM deny, не current V3 limitation |
| [A V1 refusal25.10](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-authorized-a/report.md>), [exits](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-lifecycle-authorized-a/current-exits.json>) |7 cases exit0 + supervisor-loss/independent peer. Signed refusal до PCI; этот V1 epoch не positive isolation/mapping admission |
| [Native default owner25.10](../../../CXL/logs/qcxl03-native-owner-7a52419e306e4e10a7e67ad57ce9c7bb/report.md), [results](../../../CXL/logs/qcxl03-native-owner-7a52419e306e4e10a7e67ad57ce9c7bb/qualified-results.json) | Qualified seven VMs,33 inputs unchanged в report. Record retained после EFI return, не cross-reset preservation. Preceding phase1/final-* — другие inputs |
| [QCXL-01](../../../CXL/logs/qcxl01-bootinfo-import-f547d3b540fc4e5e95b45cd8a64814b2/qcxl01-report.md), [QCXL-02](../../../CXL/logs/qcxl02-ushort-mailbox-45ae4ce8f9434af181e71ce92894e2f5/qcxl02-report.md) |24.10 lab HBI1 importer→ProviderRefused и ushort migration; не native startup/provider authority |

## Отдельные ранние external DMA series

| Report25.10 | Scope |
|---|---|
| [Feasibility](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-iommu-feasibility-fb837fce43ad432ab4a398baa689467b/report.md>) |3 VM, DMAR/CXL scope/relocation, TE off; не enforcement |
| [Enforcement](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-dma-enforcement-8163a16de40443e2ae9eeda838ac1f2d/report.md>) |3 VM,18 commands/6 faults; TE-off allow/empty-root TE-on deny |
| [Mapped grant](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-dma-grant-1a53dc2b61134d16864263079fd8100d/report.md>), [retry-live summary](<../../../../HybridCPU ISE/Diagnostics/_audit_output/cxl-dma-grant-1a53dc2b61134d16864263079fd8100d/retry-live/summary.json>) |3 VM,30commands/12faults; TE-on grant/retire/context+IOTLB invalidation/old-IOVA deny/native cleanup |

В этих трёх series EDU root00:03.0/SID0018, hierarchy0e:00.0/0e00, relocated1a:00.0/1a00; Type-3 — другая функция. Peer/guards — subranges одной allocation, reset после cleanup, не active-reset/provider-loss proof. Эти ограничения не отменяют поздние independent-VM/pending-command tests выше.

Failed first mapped live (CCMD CAIG0) сохранён; final pass — retry-live с actual global completion, не busy-bit disappearance. Historical [host-only cleanup](../../../CXL/logs/qcxl03-lifetime-cleanup-4b3dfc48c02b4882bded0aba378aa637/report.md) не guest closure. Failed callback и first mapping attempts также сохраняются в своих reports/artifacts.

## Исторические этапы

Эти документы описывают собственные старые inputs/recipes. Они не current task list, не рекомендации перезапустить тесты и не qualification последующих changes.

| Область | Отчёты |
|---|---|
| Backend | [Variant B](../VARIANT-B.md), [environment](../ENVIRONMENT-RU.md) |
| Discovery/mailbox | [EFI discovery](../EFI-DISCOVERY-RU.md), [Identify](../NATIVE-MAILBOX-RU.md), [capacity](../NATIVE-CAPACITY-GATE-RU.md) |
| HDM/CPU route | [Investigation](../HDM-INVESTIGATION-RU.md), [host gate](../HOST-HDM-GATE-RU.md), [managed](../TRANSACTIONAL-HDM-RU.md), [native](../NATIVE-HDM-RU.md) |
| Capsule/crypto | [Guest access](../CXL-GUEST-ACCESS-RU.md), [reader](../EFI-READER-RU.md), [crypto](../EFI-CRYPTO-RU.md), [development trust](../DEVELOPMENT-TRUST-RU.md), [managed](../MANAGED-CAPSULE-RU.md), [native](../NATIVE-CAPSULE-RU.md) |
| RAM/bounds/faults | [Early RAM](../EFI-RAM-PREPARATION-RU.md), [native RAM](../NATIVE-RAM-RU.md), [faults](../NATIVE-RAM-FAULTS-RU.md), [file failures](../NATIVE-FILE-FAILURES-RU.md), [boundary](../NATIVE-BOUNDARY-RU.md) |
| Reset | [Held RAM](../NATIVE-RAM-RESET-RU.md), [core contract](../CORE-RESET-CONTRACT-RU.md) |
| Timeline | [TEST-RESULTS](../TEST-RESULTS.md) |

ConfigurationValid ≠ QemuDeviceCreated ≠ GuestCxlMemoryAccessible ≠ guest crypto/RAM/HBI1 compatibility ≠ runtime authority ≠ kernel handoff. Stored Pester assertions не fresh VM. Direct C mailbox не guarded managed session. Software evidence не ProductionQualified. [Current state](../CURRENT-STATE-RU.md), [primary blockers](../blockers-singnextos/README-RU.md).
