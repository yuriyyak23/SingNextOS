# Исторические результаты 24.10.2026

Этот файл — compact index старых этапов, не current full-suite status и не рабочая очередь. Подробные measurements, exact artifact paths и ограничения сохранены в phase reports ниже и их CXL/logs inputs. Поздние25–26.10 lifecycle/DMA/native-owner epochs — в [evidence index](evidence/README-RU.md).

| Этап | Historical record |
|---|---|
| Native Windows RAM backend, принято отсутствие cold persistence | [Variant B](VARIANT-B.md) |
| CPU CXL access / Shell capsule round trip | [Guest access](CXL-GUEST-ACCESS-RU.md) |
| HDM stale alias, host-first uncommit и reset | [Investigation](HDM-INVESTIGATION-RU.md), [host gate](HOST-HDM-GATE-RU.md) |
| Test EFI byte reader, затем guest SHA384/ECDSA и vectors | [Reader](EFI-READER-RU.md), [crypto](EFI-CRYPTO-RU.md) |
| Development signer / static floor | [Development trust](DEVELOPMENT-TRUST-RU.md), не independent protected state |
| Early RAM/HBI1 preparation, затем security evidence record | [Early RAM](EFI-RAM-PREPARATION-RU.md), [QCXL-01](../../CXL/logs/qcxl01-bootinfo-import-f547d3b540fc4e5e95b45cd8a64814b2/qcxl01-report.md) |
| PCI/ACPI discovery / direct Identify | [Discovery](EFI-DISCOVERY-RU.md), [mailbox](NATIVE-MAILBOX-RU.md) |
| Managed transaction/capsule, затем native HDM/FAT/crypto | [Transaction](TRANSACTIONAL-HDM-RU.md), [managed capsule](MANAGED-CAPSULE-RU.md), [native HDM](NATIVE-HDM-RU.md), [native capsule](NATIVE-CAPSULE-RU.md) |
| Native RAM/faults/file failures/bounds/capacity | [RAM](NATIVE-RAM-RU.md), [faults](NATIVE-RAM-FAULTS-RU.md), [files](NATIVE-FILE-FAILURES-RU.md), [boundary](NATIVE-BOUNDARY-RU.md), [capacity](NATIVE-CAPACITY-GATE-RU.md) |
| Held-RAM reset / host PrepareKernelResetAware contract | [Reset](NATIVE-RAM-RESET-RU.md), [core contract](CORE-RESET-CONTRACT-RU.md) |

Ранние120-byte HBI1/no-record и поздние160-byte HBI1 development Security record — разные epochs. Byte mailbox gap исправлен QCXL-02 отдельно; не считать его current missing API. Raw failed HDM/mailbox/other attempts не переписаны; negative test success не failure самого workload. Stored Pester assertions не дополнительные VM runs.

Ни один historical result не доказывает runtime authority admission, kernel handoff, independent durable antirollback или ProductionQualified. Текущие status/scope только в [CURRENT-STATE](CURRENT-STATE-RU.md) и [canonical queue](modernize/README-RU.md).
