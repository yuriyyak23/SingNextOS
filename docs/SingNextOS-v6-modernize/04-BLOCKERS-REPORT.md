# Открытые границы и зависимости

Актуально на 2026-10-15. Единственная очередь — [01](01-STATE-AND-BACKLOG.md). Этот документ объясняет remaining properties, не задаёт второй backlog. Source/callers/assets и terminal inputs приоритетнее dated reports.

| Boundary | Classification | Что остаётся |
|---|---|---|
| B1 durable original continuation | Missing/ExternalBlocked | Effect переживает runtime: original fact должен быть durable до effect; cleanup-only replay после all-fact-loss/reboot требует trusted cold monotonic anchor/provider continuation, freshness/rollback/torn-write и authenticated lifetime. Без них dependent pins сохраняются |
| B2 unknown invocation | Partial | Surviving-known original query реализован у ряда producers. Для остальных actual clients нужны request identity до effect, exact source/generation/acceptance и no-effect либо contained/no-further-use guarantee; lost/malformed histories не повторяют effect |
| B3 artifact/backing/independent users | Partial | Exact Release и surviving-original Start/Bind cleanup реализованы. Incapable/general/history users и independent pins не закрываются metadata, Bind/Start или returned-object compensation |
| B4 cross-owner consequences | Partial | Исправлять конкретный admission/effect/retention/publication/consumption defect у actual callers. Общая theorem/model-check не prerequisite каждого dev fix; general atomicity и crash durability не заявлять |
| B5 executing producers/adoption | Partial | Managed DMA/event/VIO/nested/guest/DSC1 producers и clients исполняются. Remaining producer/history/client/backing needs брать по source. ProductionSecure и machine fault-kind отсутствуют; software не physical evidence |
| B6 later API consumers | Not dependency-ready без actual need | Restart/settlement/pool/coherent APIs требуют producing caller и original acquisition/backing. Cross-owner DMA execution не staged ExternalOperation predecessor; mapped-region guard не обходить |

Prepared/NotFound/HMAC/warm same-original repair, surviving-known response, compiler proof и trace не доказывают B1/B2 cold unknown containment. No RecoveryAuthority/resurrection capabilities, второго ledger или нового owner ради обхода gap.

CXL multi-host local reclaim/teardown release запрещены, owner synchronized; use/Region/charge удерживаются. Positive original remote isolation producer Missing/ExternalBlocked, P11/physical FutureGated. [Audit disposition](06-CXL-EXTERNAL-AUDIT-DISPOSITION.md) отделяет pre-fix findings от current source. Local retention не доказывает positive remote isolation.

Current tuple и выбранный seam находятся только в [01](01-STATE-AND-BACKLOG.md). DMA/DSC1, post-Region failed quantitative composition cleanup, original virtualization request/publication custody, nested/control invocation closure и model Revoke реализованы в bounded contours. General unknown/history/backing остаются Partial; DestroyAsync invocation correlation ещё не принят actual host caller. Historical failed exits сохраняются; поздний возврат файлов или rebuild не переписывает предыдущую verification.

Admission = SingNext authorization AND provider admission AND semantic refinement AND current legality. Один fact — один owner; closure/consumption/quantitative reclaim независимы от result visibility. Не стирать charges по metadata, не re-reserve для маскировки gap. [Dev policy](03-DEV-VALIDATION.md) определяет необходимую verification. Gates OFF; SingPlus.Boot/Samples excluded; distinct SingNext.Boot fresh attribution; Java skipped; ISA NONE; no ProductionQualified.
