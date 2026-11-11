# BootCapsule и SingBoot seam

Existing `DirectBootCapsule`/`BootCapsuleEntry.Prepare` копируют already
authenticated bytes в normal RAM, проверяют SHA-384 destination и пишут bounded
HybridBootInfo. Reset-aware preparation очищает destinations при late reset.
Это preparation contract, не executable OS boot.

`BootCapsuleEntry.Run()` возвращает `Unsupported`. Поэтому native EFI pipeline
нельзя называть запуском SingBoot или RuntimeKernel. Следующая composition должна
использовать existing owners/gates, а не создавать success path поверх diagnostic
EFI result.

Главный compatibility seam — BootInfo. Lab producer пока даёт zero-record
envelope; runtime importer требует один действительный Security record с flags
`Required | EvidenceOnly` и bounded 32-byte payload. После parser acceptance
runtime выполняет fresh discovery; `NoFreshEndpoint` или `ProviderRefused` без
реального provider — корректный scoped refusal, не takeover.

Mailbox seam остаётся открытым: managed boot interface принимает `byte`, тогда
как native Identify — `0x4000`. Нужна согласованная `ushort` migration через
owners/callers/tests; token mapping может быть лишь временной laboratory мерой.
