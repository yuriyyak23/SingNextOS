# ISE / platform dependencies

**26.10.2026.** External Tools разрешены; ISE arch/core/ISA не входят в scope. [Source graph](../SOURCE-MAP.md#external-dma-tools), [evidence epochs](../evidence/README-RU.md).

## HCI-01 — current deny-by-default provider

**Partial native lab; managed/runtime path ExternalBlocked.** Existing VtdGrantOwner + A supervisor establishes actual TE-on grant → original retire/global invalidation → fresh stale-IOVA faults до native PCI/CCI. Existing HybridCpuBootSession требует current isolation generation, но concrete hardware в production src — UnsupportedHybridCpuBootHardware.

Нужны real guest runtime/platform owner и concrete IHybridCpuBootHardware для exact requester/BDF/lease/current generation. Type-3 BDF не назначается источником DMA автоматически; CPU CXL.mem, Bus Master clear, RTADDR/GSTS readback и QMP epoch сами не isolation authority. Unknown provider state — deny/quarantine.

## HCI-01B — same-VM DMA → CXL target

**VerifiedExisting в exact controlled diagnostic profile 26.10.** EDU SID0018, default mask0x0fffffff, IOVA0x2000 → CXL HPA0x190200000. Actual read/write integrity, original retire/global context+IOTLB invalidation, two fresh write/read revoke faults, unchanged target/guards и old original grant ABA-denial.

Native admission permanently withdrawn перед diagnostic grant; source snapshot/RAM/free0 после него. Pins до exact VM terminal. Это не runtime provider, Type-3 requester qualification или physical/cache/remote closure. Низкий IOVA не ограничивает автоматически translated HPA. Report и два input epochs — в [индексе](../evidence/README-RU.md).

## HCI-02 — original lifetime / closure

**Partial:** proved original grant retirement и denied old IOVA — отдельные факты от CPU route и physical/remote closure. A выполняет exact VM termination с retained original obligations. Проверены:

- V3 mapping/snapshot/RAM: fault/reset/loss windows и independent VM published backing.
- EFI FreePages entry/one-instruction/return-before-native-receipt reset.
- Pending EDU RUN/TE-on grant: busy retire/overlap deny, reset/provider loss; independent VM сохраняет original pending backing и затем reaches native RAM.

Pending EDU — timer-command до native admission, не прерывание посередине physical transaction. Callback windows — не arbitrary allocator recovery. Same-VM warm reset/reuse и physical/cache/remote closure не квалифицированы и не добавляются в обязательный A scope.

Historical feasibility, empty-root enforcement и mapped-grant workloads — разные input epochs: TE-off discovery; TE-off allow/TE-on deny; TE-on mapped allow/retire/fault соответственно. Их counts не суммируются. Старые peer/guards были subranges одной allocation; новые provider-loss pairs — независимые VM. Failed first CCMD/CAIG0 run сохранён, финальный original grant result — retry-live.

## HCI-03 — runtime/coherent boundary

Snapshot не coherent live memory. Narrow CXL boot lane не закрывает broad IOMMU-backed DSC/L7, DMA/cache coherence и remote closure. Managed provider и original aperture proof остаются Missing/ExternalBlocked вместе с [SNO-01/N1](../blockers-singnextos/SNO-01-N1-TARGET-ABI-RU.md).

[ISE applicable queue](<../../../../HybridCPU ISE/HybridCPU_ISE/docs/ref2/SecureComputeModernizationPlan/README.md>) читать только для разрешённого ISE/shared delta; не копировать её в QCXL и не выполнять unrelated global work вместо отсутствующего boot producer.
