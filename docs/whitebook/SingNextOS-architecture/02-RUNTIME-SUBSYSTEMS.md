# Runtime subsystems

## Processes, capabilities and domains

[`ProcessRegistry`](../../../src/Runtime/SingPlus.Runtime/Processes/ProcessRegistry.cs) owns process identity, generation and terminal routing. [`CapabilityAuthority`](../../../src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs) owns grants, derivation, revocation and effective constraints; [`SealedObjectAuthority`](../../../src/Runtime/SingPlus.Runtime/Capabilities/SealedObjectAuthority.cs) handles object-specific sealed lifetime. [`DomainRegistry`](../../../src/Runtime/SingPlus.Runtime/Domains/DomainRegistry.cs) registers runtime domains independently of provider topology.

`RuntimeKernel.CreateProcess` and `AdmitProcess` are separate operations. Admission re-resolves a process handle and compares the original process instance after manifest requirements have been captured. A terminated process is not proof that its external device/guest/DMA uses have closed; teardown must be explicit. Source: [RuntimeKernel](../../../src/Runtime/SingPlus.Runtime/RuntimeKernel.cs).

## Components and supervised services

[`ComponentAdmission`](../../../src/Runtime/SingPlus.Runtime/Components/ComponentAdmission.cs) and [`DriverResourceSet`](../../../src/Runtime/SingPlus.Runtime/Components/DriverResourceSet.cs) compose manifests, requested resources and admitted principals. [`ServiceRegistry`](../../../src/Runtime/SingPlus.Runtime/Services/ServiceRegistry.cs), [`CapabilityAwareServiceSupervisor`](../../../src/Runtime/SingPlus.Runtime/Services/CapabilityAwareServiceSupervisor.cs), and [native lifecycle](../../../src/Runtime/SingPlus.Runtime/NativeServices/RuntimeKernel.NativeServiceLifecycle.cs) manage service registration and drain. Drain stops new dispatch; it is not proof of provider closure or cancellation of prior effects.

## Typed SIP, channels and invocation

```text
SIP contract -> generated protocol / sentry
 -> RuntimeSipClientTransport -> ChannelRegistry
 -> EndpointSessionRegistry -> EndpointSessionInvocationRegistry
 -> concrete service host -> response publication
```

[`ChannelRegistry`](../../../src/Runtime/SingPlus.Runtime/Channels/ChannelRegistry.cs) owns accepted envelopes and channel lifetime. [`EndpointSessionRegistry`](../../../src/Runtime/SingPlus.Runtime/Services/EndpointSessionRegistry.cs) owns session incarnation. [`EndpointSessionInvocationRegistry`](../../../src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs) retains an accepted invocation and its exact input/resource dependencies. Failed publication does not authorize replay of external effects. Response cancellation may transfer *existing* custody while backing, budget and external-use pins remain independent.

Source: [SIP client transport](../../../src/Runtime/SingPlus.Runtime/RuntimeSipClientTransport.cs).

File and network [SIP interfaces](../../../src/Sip/SingPlus.Sip/FileSystem/IFileService.cs) ([network interface](../../../src/Sip/SingPlus.Sip/Networking/INetworkService.cs)) expose bounded request and response shapes. Their presence alone does **not** establish a full production filesystem or network stack. Separate concrete hosts and clients determine adoption.

## Region memory and budgets

[`RegionAuthority`](../../../src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs) owns allocation identity, borrow/use state, backing, mutation/damage and reclamation. Physical/virtual addresses or mapping identifiers do not replace Region identity. [`ResourceBudgetAuthority`](../../../src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs) separately owns reserve, bind, consume, settle and release of quantitative facts. Neither a scheduler estimate nor reported usage can mint a reservation.

Cross-owner transfer must account for both the Region fact and the target/source budget. A failed budget composition following an executed Region transition requires conservative, *independent* cleanup, not invented rollback or blind re-reservation.

[`ResourceBudgetRecoveryJournal`](../../../src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetRecoveryJournal.cs) supplies authenticated, sequenced local history. The [kernel constructor](../../../src/Runtime/SingPlus.Runtime/RuntimeKernel.cs) can replay it into conservative recovery charges. This **does not** prove monotonic trusted cold continuation after complete state loss, reboot and rollback without an independent durable anchor.

## Events, deadlines and cancellation

[`KernelEventRegistry`](../../../src/Runtime/SingPlus.Runtime/Events/KernelEventRegistry.cs) owns local staged/committed events and waiter lifetime. Device IRQ arrival is not local waiter closure. [`CancellationScopeAuthority`](../../../src/Runtime/SingPlus.Runtime/Deadlines/CancellationScopeAuthority.cs) owns local cancellation scopes. Deadline expiry is not proof that a remote effect ceased; hard timing guarantees require provider enforcement and schedulability proof.

## Compute, DAG jobs and scheduling

[`ComputePlanner`](../../../src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs) and [`RuntimeComputeServiceHost`](../../../src/Runtime/SingPlus.Runtime/Compute/RuntimeComputeServiceHost.cs) compose compute plans and admitted execution. `SipJobs` includes [plan verification](../../../src/Runtime/SingPlus.Runtime/SipJobs/SipJobPlanVerifier.cs), [DAG eligibility](../../../src/Runtime/SingPlus.Runtime/SipJobs/SipJobParallelDagEligibility.cs), [stage admission](../../../src/Runtime/SingPlus.Runtime/SipJobs/SipJobStageExecutionEligibility.cs), and [protected label flow](../../../src/Runtime/SingPlus.Runtime/SipJobs/SipJobProtectedLabelFlow.cs). Planner eligibility does not authorize new effects without fresh owner and generation checks.

## GUI, checkpoint, tracing and diagnostics

[`RuntimeCompositorServiceHost`](../../../src/Runtime/SingPlus.Runtime/Gui/RuntimeCompositorServiceHost.cs) uses typed sessions and Region transfers; payload handling and publication remain separate commits. [Checkpointing](../../../src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs) restores only its documented local facts. [`DeterministicTraceAuthority`](../../../src/Runtime/SingPlus.Runtime/Tracing/DeterministicTraceAuthority.cs) and [`TraceReplayEngine`](../../../src/Runtime/SingPlus.Runtime/Tracing/TraceReplayEngine.cs) support local observation/replay, not an authoritative machine replay or permission resurrection. [`AuthorityInspector`](../../../src/Runtime/SingPlus.Runtime/Observability/AuthorityInspector.cs) and [reclaim diagnostics](../../../src/Runtime/SingPlus.Runtime/Observability/ReclaimObservability.cs) are read-only explanatory surfaces, not reclaim owners.
