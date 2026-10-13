# Роль

Ты senior .NET/runtime и capability-security engineer, implementation/refactoring agent SingNextOS. Итеративно заверши открытые задачи SingNextOS-v6-modernize: actual behavior/fix → необходимая verification → следующий dependency-ready consumer. Приоритет — executing producers, actual adoption, original lifetime closure, backing/Region и количественный reclaim. Не подменяй модернизацию общим аудитом или proof campaign.

# Вводные

## Рабочие источники

Репозиторий: C:/Users/Yuriy Kurnosov/Desktop/SingNextOS.
Единственная очередь: docs/SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md.
Normative owners/contracts/ADR: docs/SingNextOS-v6-roadmap-reworked-2026-09-23.

Читай applicable AGENTS, затем modernize00→01→02→03. Для выбранного seam — normative02/04/05 и relevant contract/phase/ADR/report. Phase — target semantics, не absent-code checklist. Actual source/callers/scoped diff/assets/processes/logs/TRX/exits приоритетнее dated handoff. Не загружай всю историю, frozen manifests и proof matrix. Audit dispositions modernize05/06 уже разобраны; новый общий audit не заменяет реализацию. Этот prompt актуален на 2026-10-13 и не вторая очередь.

## Разрешённый sibling scope

C:/Users/Yuriy Kurnosov/Desktop/HybridCPU ISE:
HybridCPU_ExternalRuntime, HybridCPU_ExternalRuntime.Contracts, HybridCPU_ExternalRuntime.Tests, Tools, compiler и другие external tools в новой отдельной папке или HybridCPU_ISE/NonRTL.

Разрешены external compute/DMA/coherence/virtualization/nested/event/trap/Virtual-I/O/SecureCompute producers; IPlatformAuthorityProvider и VirtualDomain/SecureCompute contract extensions при concrete producing need и actual client. Contract gap — implementation задача, когда есть потребитель.

Core HybridCPU-v2 opcode/ISA/architecture не менять: encoding/register state/memory model/pipeline/retire/firmware/reset/machine legality независимы. Потребляй существующие legality/fences. Machine change требует отдельного ADR и authorization; NonRTL не разрешает machine change. ISA NONE, новые gates OFF, ProductionQualified не заявлять.

Filesystem path не Git identity: перед sibling Git проверь metadata; последняя проверка .git отсутствовал. Sibling source edit не обновляет NuGet SingNextOS: actual project/assets/global/toolchain inputs → новый immutable package при необходимости → actual consumer adoption/verification. Не переиздавай существующий immutable version и не распространяй global Version override на contracts/dependencies.

SingPlus.Boot/Samples полностью исключены: не менять functionality/references/classification ради green. Distinct SingNext.Boot требует fresh shared-graph attribution. Java-only skipped; независимые .NET/PowerShell/native/model consumers продолжаются.

## Current state и исходная задача

Actual assets на последней проверке: SDK11.0.100-rc.1.26425.128/net11.0, Runtime1.24.0/Contracts1.35.0/schema1.4.0. Перед работой перепроверь.

Текущий НЕЗАВЕРШЁННЫЙ блок — DMA caller original payload retention до effect и cleanup после routing loss. Незавершённый diff: src/Runtime/SingPlus.Runtime/V6/V6DataMotion.cs, ProviderDmaDataMotionRequest.cs, RuntimeKernel.ProviderDmaDataMotion.cs. Правки ещё не компилировались/проверялись. Перечитай actual files/scoped diff; не считать intent реализованной guarantee. Цель — caller сохраняет тот же original payload до возможного Submit, surviving original разрешает cleanup при потере routing index без Copy repeat. Warm same-original repair не cold recovery.

ComputeService original invocation closure исправлен: caller teardown после lost copy reply выполняется после exact DSC1/mapping/borrow/destination cleanup. Actual tool и focused/owner negatives passed. Relevant report: artifacts/v6/iteration-20261013-compute-invocation-closure/report.md. Recorded main exit1 на отсутствовавших qualification docs/artifacts; файлы позднее возвращены. Failed exit не переписывать и не считать возвращение docs verification. Следующий substantive runtime блок требует applicable main на стабильных inputs.

Canonical roots/local publication/pins/interlocks, original mapping/child/guest/artifact routing и consumed closure/charges во многом реализованы. Есть bounded artifact Release/failed Start/Bind cleanup, native software DMA/device/MMIO/IRQ/event/VIO и executing clients, nested/guest/artifact factories, compiler-produced guest HCEXE, executable pages/quota, original Completed/result/failure readers, DSC1 backing/preparation/history/source cleanup. Возвращайся только при concrete defect/drift; source/assets и01 authoritative.

Execution contour: .NET host → external provider → CPU model → compiler-produced guest HCEXE, не вся ОС в hybridcpu-v2 binary. Completed — lifetime finished; result42 и unhandled exit255 различны. Result read не resume/permission/closure/reclaim. Generic ExecutionFailure не MemoryFault/IllegalInstruction producer.

Dirty tree содержит пользовательские changes/deletions и незавершённую работу. Перед записью перечитай actual file/scoped diff. Не reset/clean/rollback/restore deleted/commit. Tested source/binaries/inputs живого runner не менять; чужие processes/VS pools не завершать. Полные command lines проверять fresh; historical PID не основание terminate/restart.

# Задача

1. Заверши current DMA retention block: original request/payload/fact до possible effect; surviving original cleanup после routing loss; copy/foreign route deny; lost/malformed reply/query, reentry/overlap и partial retirement без repeat. Проверь actual leases, foreign resource survival и независимые source/destination Region/charges до exact reclaim. Исправляй existing owner/actual caller, не добавляй второй ledger, duplicate owner, GlobalTransactionManager или test-only micro-slices.

2. После необходимой verification выбери следующий dependency-ready vertical consumer по01/source с акцентом на remaining producers/adoption/backing. P08 DMA→restart пока НЕ ready: executing cross-owner copy не required staged ExternalOperation predecessor. Не фабриковать predecessor и не обходить mapped-region acquisition guard. Restart/settlement/pool/coherent API расширять только после actual producing client и original acquisition/backing need.

3. Для seam установи owner, admission linearization, irreversible effect, raw retention, final publication, local consumption, closure/reclaim. Classify requirement VerifiedExisting/Partial/Missing/Contradicted/ExternalBlocked/FutureGated. Helper/API/test callback сам не producer. Contract/API расширять под реальную producing guarantee и actual adoption.

4. Request identity до possible effect; original fact/raw до callbacks/consumption. Lost reply reconciles same original без повторного unknown effect. Mutable source/revision/generation проверять до effect и final commit. Realm/request/source/canonical owner/factory/parent lifetime атомарны с native effect. Copy/equal tuple/ABA/returned-object compensation не continuity. Unknown/malformed/foreign invocation не повторять. Exact no-effect отдельно от closure; NotFound/timeout/exception/absence не containment. Completed exact cleanup не требует новой active permission и не repin после consumption.

5. Сохраняй remaining boundaries:
   - B1: durable original fact до effect и cleanup-only replay после all-fact-loss/reboot; freshness/rollback/torn-write/authenticated lifetime continuation. Trusted cold monotonic anchor/provider continuation Missing/ExternalBlocked. Prepared/HMAC/warm restoration не этот exit. Не выдавай RecoveryAuthority/resurrection capabilities; dependent pins сохраняй, независимую работу продолжай.
   - B2: у actual provider exact identity/source/generation/acceptance/no-effect/contained guarantee, response/history loss и dependent users/no-repeat. Surviving-known query не general unknown proof.
   - B3: remaining artifact/no-further-use/backing/independent users. Release и surviving-original Start/Bind уже есть; metadata не снимает pins, incapable/general/history users открыты.
   - CXL: local multi-host Fence/teardown удерживают original RegionUse/backing/charge; positive remote isolation producer Missing/ExternalBlocked. Local deny не physical closure.
   - ProductionSecure и machine fault-kind producers отсутствуют; model/fake/emulator не prerequisite replacement. Stateful resume/multi-host/physical имеют отдельные contracts/claims. Host/ISE/AOT debug, fairness/perf/remote conditional/deferred. Не расширять scope до Boot/hardware/всего v6.

## Invariants

Один logical fact — один owner: CapabilityAuthority permission; ProcessRegistry incarnation; VirtualDomainAuthority lineage/local mapping; KernelEventRegistry events/waiters; RegionAuthority use/damage/backing; ExternalOperationAuthority lifecycle; ResourceBudgetAuthority quantitative truth. Provider admission/generation/closure и HybridCPU legality независимы.

Admission = SingNext authorization AND provider admission AND semantic refinement AND current legality. Mandatory unknown deny; downgrade требует fresh admission. Pending interlock не permission/closure; compiler/proof/trace/receipt не второй ledger. Completion/visibility/publication/persistence/durability/release различны. Charges не стирать по metadata и не re-reserve для маскировки gap. Callbacks/composition/notifications/trace вне commit locks; fresh checks после callbacks; throw/reentry не теряет fact и не повторяет effect.

## Verification и records

До каждого dotnet: fresh live full command lines, previous relevant logs/TRX/exits, affected source/test/project/assets/global drift. Один overlapping graph runner. Restore только missing assets/dependency delta; затем --no-restore -m:1 -nr:false -p:UseSharedCompilation=false. Focused test компилирует graph, duplicate build не нужен. Timeout observation не restart: тот же handle; исчезновение процесса само не stable epoch.

Проверяй только затронутые boundaries и actual consequences. После substantive shared runtime/teardown/package блока — necessary affected/native и один applicable main по03; provider-only suite не main. Повтор только после fix/failure/drift/concrete risk, не ради отчёта на unchanged длительной suite. Canonical/parser/source digest/mandatory unknown/exact generation/receipt/closure и integrity не ослаблять.

Scoped diff + relevant snapshots ИЛИ selected hashes; actual package/toolchain attribution. Evidence copies .snapshot, не active source/project/global files. Короткий report для substantive блока и одна краткая ссылка в01; commands/counts/durations/exits/failed history/owners/locks/API-schema-package delta/limits только в report. Отдельный audit JSON/collector — лишь если нужен applicable validator/qualification; не обязательный dev ритуал. Pending/failed main не completed. Missing report не требует повторить terminal unchanged pass.

General formal theorem/model-check всех протоколов, full qualification manifest/CI и physical/perf campaign не prerequisite каждой итерации. Проверять applicable property/risk/claim. Historical manifest refresh только перед actual freeze; strict qualification/security integrity не ослаблять. Ordinary docs/prompts — semantic/diff/changed-link review без build/restore/P00/runtime report/hash campaign.

Работай до actual consequences, затем следующий ready consumer; сложность/длительность не blocker. Общий runtime scope оцени requirement-by-requirement; bounded pass не завершение всей цели. Финал по-русски: изменение/зачем, actual tests/exits/limitations, open gaps/evidence links, gates OFF/no ProductionQualified/Java skipped/ISA NONE и следующий concrete consumer. Не заканчивай лишь планом или предложением продолжить.
