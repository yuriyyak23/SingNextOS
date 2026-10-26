# QCXL-02: закрытый representation request

Исторический byte opcode gap устранён: IBootCxlTransport.Execute, IHybridCpuBootHardware.ExecuteCxlMailbox, HybridCpuBootSession и DeadlineMailbox используют ushort. Byte overload/alias/cast не возвращать.

[Exact report24.10](../../CXL/logs/qcxl02-ushort-mailbox-45ae4ce8f9434af181e71ce92894e2f5/qcxl02-report.md) проверяет representation и distinct0x4000/0x0000. Это не guarded managed hardware adoption: та требует real provider/runtime и остаётся [QCXL-04](modernize/README-RU.md).

Файл сохранён для inbound links, не незавершённый запрос на повторную migration. [Current state](CURRENT-STATE-RU.md), [platform dependencies](blockers-hybridcpu-v2-ise/README-RU.md).
