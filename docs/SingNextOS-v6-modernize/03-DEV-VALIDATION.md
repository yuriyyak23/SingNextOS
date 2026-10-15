# Dev verification и qualification

Объём проверки определяется изменённым actual consumer, риском и claim. Actual behavior/fix → необходимая verification → следующий dependency-ready consumer. Не превращать обычную реализацию в кампанию доказательств.

## Применимый минимум

| Изменение | Проверка |
|---|---|
| Ordinary docs/prompts | Semantic/scope/diff review и changed local links. Без build/restore/P00/runtime report/hash campaign |
| Source/refactoring | Actual callers/owners, affected compile и необходимые behavior/negative tests; focused test, компилирующий graph, заменяет отдельный build |
| Shared runtime/teardown/capability | Affected native/fault/race checks и один applicable main после согласованного substantive блока на стабильных inputs |
| Contracts/schema/security | Actual consumer adoption, compatibility/parser/canonical digest/mandatory unknown, exact generation/receipt/closure и package integrity checks |
| Qualification/promotion | Requirement-by-requirement evidence для конкретного свойства и exact deployment; physical claim требует physical evidence |

Проверять только затронутые boundaries: absent/foreign/copy/ABA/revision exhaustion, source/generation loss, throw/reentry/overlap, history loss, lost/malformed reply/query, partial consumption/retry. Interleaving должен устанавливать actual consequences: foreign resource жив, original закрыт либо pinned, backing/Region/attached charges точно reclaimed либо retained. Не писать duplicate tests или test-only micro-slices ради counts.

Security/canonical/source digests, mandatory unknown deny, exact generation, original receipt и closure checks не ослаблять. Timeout/NotFound/exception/absence не containment. Completion/result read не permission или independent lifetime closure.

## Dotnet и стабильность

Перед каждым dotnet проверить fresh live full command lines, prior relevant logs/TRX/exits и affected source/test/project/assets/global drift. Один overlapping graph runner. Чужие processes/VS pools не завершать; shared inputs живого runner не менять. Проверить actual command/parent attribution, не полагаться на исторический PID.

Restore только при missing assets/dependency delta. Далее --no-restore -m:1 -nr:false -p:UseSharedCompilation=false для MSBuild commands. Timeout observation не restart: ждать тот же handle и сверять authoritative state. Исчезновение процесса само не stable source epoch.

Docs вне tested/canonical inputs можно редактировать во время тестов. Failed main/pending verification не completed; excluded SingPlus.Boot/Samples не менять ради green. Distinct SingNext.Boot/shared graph requires fresh attribution. Повтор нужен после fix/failure/drift/concrete risk, не ради новой записи на unchanged inputs.

## Запись результата

Для substantive runtime/teardown/package блока — короткий report: consumer/изменение/owners/locks, verification commands/counts/durations/exits и failed history, actual inputs/package/toolchain attribution, ограничения и open gaps. Scoped diff плюс relevant snapshots **либо** selected hashes достаточно; full-tree inventories, обе формы одновременно, отдельный report на каждый тест и repeated captures не нужны.

Отдельный machine-readable audit/collector требуется только если его потребляет applicable validator/qualification workflow. Обычный dev блок не обязан создавать audit.json, proof matrix или новые IDs. Существующие security/qualification schemas и strict scripts сохраняются.

Micro-fixes/test-only strengthening включать в record своего блока. Одна краткая ссылка/status в01; журналы не копировать в phase/очередь. Недостающий report не требует повторить terminal pass на unchanged inputs. Evidence copies — .snapshot, не active .cs/.csproj/global.json. Historical failed exits и manifests не переписывать.

## Более сильные claims

Full formal/model-check всех протоколов, full qualification manifest/CI, hardware/perf campaign не dev prerequisite. Выбирать их по applicable claim или конкретному unresolved risk. Missing trusted cold anchor/physical producer не заменять warm same-record restoration/model/callback.

MANIFEST/SHA256SUMS historical, explicit refresh только перед actual strict freeze. Freeze/Qualify, dependency/security integrity и exact deployment qualification не ослаблять. Все новые gates OFF; модели/эмуляторы/unit/compiler не ProductionQualified или physical proof. Java-only skipped, ISA NONE.
