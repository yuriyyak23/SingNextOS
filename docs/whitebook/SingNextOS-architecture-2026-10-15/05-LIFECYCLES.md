# Сквозные сценарии и failure semantics

## Service effect

1. Admit caller/capability/process/session и relevant generations.
2. Retain accepted original invocation/input/resource request до possible effect.
3. Provider admission/refinement/legality; mark effect-possible.
4. Invoke один раз, сохранить original raw до callbacks.
5. Fresh source/owner checks и final local composition.
6. Publish response отдельным existing owner.
7. Consume validated original closure; independently release use/backing/charges; retire invocation/session pin.

## Lost reply и failed publication

Effect мог исполниться. Same-original pure lookup разрешает continuation; absent result сохраняет pins. Response-only retry не повторяет provider effect. Foreign route/copy/equal tuple не используется для compensation. Failed budget composition после Region effect сохраняет original quantitative facts для independent cleanup.

Exact no-effect относится к конкретной попытке, closure — к прекращению use созданного ресурса. NotFound/timeout/exception/absence не доказывают ни того, ни другого. Local refusal до provider invocation отличается от provider NotAccepted.

## Teardown и partial retirement

Drain запрещает новый dispatch, но не подтверждает external closure. Original close может быть consumed раньше завершения local domain/invocation cleanup. Retry продолжает оставшиеся commits без повторного close/repin consumed facts. Independent users закрываются отдельно. Closed metadata retention не heap reclaim.

## Callback/reentry/source drift

Source/incarnation/revision/generation проверяются до effect и final commit. Callback может изменить route/source: после него fresh checks. Interlock предотвращает overlap, не даёт permission. Throw/reentry сохраняют raw и no-repeat discipline.

## Recovery

Warm original restoration, prepared journal и HMAC не trusted cold monotonic continuation. После all-fact-loss/reboot требуется anchor/provider continuation с freshness/rollback/torn-write/authenticated lifetime. До этого dependent pins сохраняются, RecoveryAuthority/resurrection capabilities не выдаются. Независимые consumers продолжают работу.

## Quantitative consequence

Meaningful verification измеряет effect count, original/foreign survival, independent source/destination Region/backing/charges и exact reclaim либо retention. Counts тестов сами не completeness proof. Успешный result read не release, metadata disappearance не no-further-use.

Нормативная основа: [invariants](../../SingNextOS-v6-roadmap-reworked-2026-09-23/04-NORMATIVE-INVARIANTS.md) и [dev verification](../../SingNextOS-v6-modernize/03-DEV-VALIDATION.md).
