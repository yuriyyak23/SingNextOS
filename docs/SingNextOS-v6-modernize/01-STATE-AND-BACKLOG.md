# Состояние и очередь модернизации

Актуально на 2026-10-15, Europe/Moscow. Это единственная implementation очередь. Source/callers, scoped diff, actual assets и terminal records приоритетнее dated handoff. Phase/ADR описывают требования и историю решений, а не список отсутствующего кода.

## Текущий блок

**Bounded original model Revoke producer/teardown adopted; general B2/B3 Partial.** [Record](../../artifacts/v6/iteration-20261015-original-model-revoke/report.md): original close request/raw before effect, canonical same-original resolver, retry avoids another drain/revoke. Reply/query loss and partial invocation retirement: Revoke1, charge512->0, Region Released; copy/history loss retains512, before-admission loss effect0 retained, foreign bytes0x74 survive. Focused562 exit0; main8510+2skip exit0, selected inputs stable. Initial561+1fail exit1 preserved. Next consumer: DestroyAsync original accepted domain/close -> invocation lifetime.

Actual assets: SDK11.0.100-rc.1.26425.128/net11.0; ExternalRuntime1.24.0/Contracts1.35.0/schema1.4.0. Версии перепроверять перед adoption; sibling source сам по себе не меняет NuGet consumer.

Historical [ComputeService closure record](../../artifacts/v6/iteration-20261013-compute-invocation-closure/report.md) относится к своим inputs, включая recorded failed main. Current status задаёт блок выше; исторический failure не является текущим blocker и не переписывается.

## Порядок открытой работы

| Приоритет | Boundary / classification | Действие |
|---|---|---|
| 1 | DestroyAsync invocation closure, Partial | Host still calls direct DestroyVirtualDomain without invocation correlation. Bind accepted original domain/close lifetime before effect; consume exact provider/domain closure into its invocation, including failed response/publication and partial retirement retry. Use existing domain/registry owners, no duplicate ledger/effect replay/permission resurrection; validate independent Region/backing/charge reclaim and foreign resources |
| 2 | B2/B3/B5, Partial | Remaining actual provider unknown/history/independent-user consequences: request/fact до effect, exact same-original no-effect/closure, no-repeat, actual adoption и Region/charge последствия. Выбирать конкретного клиента по source; API/helper/test callback не producer |
| 3 | B1, Missing/ExternalBlocked | Durable original fact до effect и cleanup-only replay после all-fact-loss/reboot. Нужны trusted cold monotonic anchor/provider continuation, freshness/rollback/torn-write и authenticated lifetime semantics. Сохранять dependent pins; независимую работу продолжать |
| 4 | P08/P03 consumers, not dependency-ready без prerequisite | DMA cross-owner execution не required staged ExternalOperation predecessor. Не фабриковать predecessor, не обходить mapped-region acquisition guard. Restart/settlement/pool/coherent APIs расширять после actual client и original acquisition/backing need |
| 5 | A0 и conditional направления, Partial/FutureGated | Existing non-Boot graph policy сохранять; developer debug/locality/fairness/perf/remote брать по concrete need. Stateful resume/multi-host/physical — отдельные contracts/claims |

## Реализованное — возвращаться только при defect/drift

- Post-Region failed quantitative composition cleanup; original virtualization request/result custody и native drain; nested/control invocation closure; original model transition/Revoke producers и teardown adoption. DestroyAsync correlation остаётся в очереди выше.
- Canonical root/device/secure owners; original mapping/child/guest/artifact routing; publication/interlocks; consumed closure/charge; unpublished Bind/Map и native root Close.
- Optional artifact Release, surviving-original failed/unknown Start/Bind cleanup; independent/incapable/general-history users остаются pinned.
- Original software DMA/device/MMIO/IRQ, event delivery, nested/guest/artifact factories и retirement; Virtual-I/O private backing, generated session/data-plane и executing clients. Это software producing adoption, не physical DMA/coherence или ProductionSecure.
- Compiler-produced guest HCEXE: linked entry, arrays/newarr, EH catch/finally/rethrow, nominal slots/reference returns/cctors; executable private pages/quota/guest charging; original Completed/result и generic failure readers.
- DSC1 software backing v2 и actual Kernel/ComputeService adoption; preparation identity, raw continuity, malformed/dependent histories и late-source cleanup. Existing reference lifecycle ModelOnly остаётся отдельным режимом.
- CXL multi-host local Fence/teardown сохраняют RegionUse/backing/charge; gate synchronized. Local retention не remote isolation.

Выборочные входы: [ADR-032 DMA](../SingNextOS-v6-roadmap-reworked-2026-09-23/ADR-032-ORIGINAL-NATIVE-DMA-COPY.md), [ADR-035 VIO](../SingNextOS-v6-roadmap-reworked-2026-09-23/ADR-035-ORIGINAL-VIRTUAL-IO-BACKING.md), [ADR-054 DSC1](../SingNextOS-v6-roadmap-reworked-2026-09-23/ADR-054-ORIGINAL-SOFTWARE-DSC1-BACKING.md). Report подтверждает только свои inputs; missing historical report не означает Missing implementation или повод повторить unchanged suite. Прежняя развёрнутая навигация сохранена в TempEnv/documentation-cleanup-20261013/01-STATE-AND-BACKLOG.md.snapshot; не загружать её без regression/trace need.

## Открытые ограничения

ProductionSecure и machine fault-kind producers не установлены; generic ExecutionFailure их не заменяет. Remote CXL exact no-further-use, trusted cold continuation и physical qualification не закрыты: [CXL disposition](06-CXL-EXTERNAL-AUDIT-DISPOSITION.md). General unknown/all-fact-loss и независимые artifact/backing users не закрываются surviving-known query.

Standalone AssignPool/BindCoherentAccess и restart не становятся ready по наличию API. Нужен original capability/process/device/acquisition tuple и actual producing caller. Отсутствие общей formal theorem не отдельный implementation blocker.

[Порядок](02-IMPLEMENTATION-ORDER.md), [dev validation](03-DEV-VALIDATION.md), [границы](04-BLOCKERS-REPORT.md), [handoff](04-ROOT-CONTINUATION-PROMPT.md). SingPlus.Boot/Samples исключены; distinct SingNext.Boot требует fresh shared-graph attribution. Gates OFF; ISA NONE; Java-only skipped; ProductionQualified не заявлять.
