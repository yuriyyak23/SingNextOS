# Открытые работы и правила claims

Ближайшая полезная работа — не новый PCI/HDM experiment, а closing actual
producer/consumer seams:

1. Реальный EFI BootInfo Security producer -> codec -> importer -> fresh
   discovery attempt.
2. Полная 16-bit mailbox API migration с affected callers/tests.
3. Actual SingBoot entry composition при наличии DMA/reset/provider gates.
4. Только после этих границ — отдельный scope для ExitBootServices, retained RAM
   ownership и kernel entry.

Каждый executable change требует fresh local evidence: selected revision/dirty
diff, profile, source/generated inputs, binary hashes, commands/exits и cleanup.
Сохранённые Pester/JSON summaries не заменяют новый run. Не ослаблять importer,
не фабриковать DMA generation, не превращать LoaderData в kernel-owned RAM и не
называть QEMU access production qualification.

Нынешний этап не подтверждает full SingBoot, RuntimeKernel execution,
ExitBootServices, production trust/antirollback, DMA reset isolation или physical
CXL coherence. Эти границы должны оставаться видимыми, пока не появится exact
producer и evidence.
