# Подсистемы runtime

## Processes, domains и capabilities

ProcessRegistry хранит live/owned-live routes, generations и terminal receipts. Manifest validation и уникальность entry identity предшествуют registration. Termination запускает teardown зависимостей; исчезновение процесса не является external closure. CapabilityAuthority владеет grant/derive/revoke permission, SealedObjectAuthority — seal/object lifetime. DomainRegistry и VirtualDomainAuthority имеют отдельные domains/lineage facts.

## Components и services

Component admission связывает manifest, process/capabilities и resource requirements. DriverResourceSet представляет admitted ресурсы. ServiceRegistry и CapabilityAwareServiceSupervisor ведут service registration/lifetime. NativeService lifecycle/drain останавливает dispatch и сохраняет pending original custody; service termination не подменяет provider closure.

## IPC, sessions и invocations

Путь: SIP interface → generated protocol/sentry → RuntimeSipClientTransport → ChannelRegistry → EndpointSessionRegistry/EndpointSessionInvocationRegistry → service host. Channel владеет enqueue/accepted envelope и channel lifecycle; session — incarnation; invocation — accepted request/input/pin, response и связанные effect dependencies.

Original invocation/input/pin готовятся до effect. Factory-original request/result сохраняются при failed publication. Exact response cancellation может передать custody существующему invocation owner; provider/backing/charge pins остаются независимыми. Returned wrapper/copy не даёт compensation authority.

Compute, Virtualization, Compositor и DriverInterrupt имеют host/client paths. SIP также содержит Process/FileSystem/Networking interfaces; наличие interface не доказывает полноценные filesystem/network stacks. Полнота определяется concrete host и callers.

## Memory и budgets

RegionAuthority владеет ownership/use/damage/backing: allocate/borrow/transfer/subrange/reclaim имеют отдельные commits. Provider mapping/lease не заменяет Region identity. Original RegionUse сохраняется до closure всех independent users.

ResourceBudgetAuthority ведёт reserve/bind/consume/settle/release. Reservation, attached charge и usage telemetry различны. Local guest budgets и executable private pages связываются с original acquisition/backing. Scheduler не изменяет quantitative truth самостоятельно.

CompleteRegionBudgetTransfer проверяет target Bind/routing до source charge consumption. Raw Region success отдельно от final budget composition. Known failed-composition cleanup закрывает loan, Region/backing/use, target quarantine, old source charge и invocation/session pin независимо. Unknown/malformed/absent facts сохраняют charges; re-reserve не маскирует gap.

ResourceBudgetRecoveryJournal и conservative recovery charge существуют. Prepared/HMAC/warm replay не доказывают cold monotonic continuity после all-fact-loss/reboot.

## Events и deadlines

KernelEventRegistry владеет staged/committed event и waiter lifetime. Provider IRQ delivery не local waiter closure. CancellationScopeAuthority ведёт local scopes; cancel request не external containment. Guaranteed deadline требует provider enforcement и schedulability, а не только таймер.

## Compute, jobs и scheduling

ComputePlanner/RuntimeComputeServiceHost связывают planning/admission с execution. Resource envelope/static estimate не reservation. SipJobs содержит plan/DAG verification, stage/segment eligibility, resource transport, file-open bindings, protected flow и gates. Parallel eligibility не отменяет fresh stage admission и generations. VNext scheduler/resource donation/admission — policy/integration, не новая authority.

## GUI, checkpoint и observability

RuntimeCompositorServiceHost использует session и Region transfer paths; raw effect, budget composition и response publication различны. Checkpointing и DeterministicTraceAuthority/TraceReplayEngine сохраняют предусмотренные local facts и observations. Trace replay не machine replay и не permission resurrection.

AuthorityInspector, ReclaimBlockerModel/ReclaimObservability и structured telemetry объясняют retention. Diagnostics не освобождают authoritative charge и не превращают timeout в reclaim.

## Source navigation

Все исходники этих subsystem приведены по директориям в [атласе](09-SOURCE-ATLAS.md). Основные anchors: RuntimeKernel.cs, Capabilities/CapabilityAuthority.cs, Processes/ProcessRegistry.cs, Regions/RegionAuthority.cs, Budgets/ResourceBudgetAuthority.cs, Channels/ChannelRegistry.cs, Services/EndpointSessionInvocationRegistry.cs, Events/KernelEventRegistry.cs, Compute/ComputePlanner.cs, SipJobs/SipJobPlanVerifier.cs, Observability/AuthorityInspector.cs.
