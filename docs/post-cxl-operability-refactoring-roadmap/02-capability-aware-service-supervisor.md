# Phase 2 — Capability-aware Service Supervisor

## Goal

Introduce a privileged supervisor that turns validated service definitions into generation-bound process/domain instances, orders dependencies, observes health, drains failed instances, and performs safe replacement without creating a second authority system.

## Core contracts

Freeze contracts equivalent to:

```text
ServiceInstanceHandle(ServiceId, ServiceGeneration)
ServiceLifecycleState
ServiceStartRequest / Receipt
ServiceDrainRequest / Receipt
ServiceReplacementRequest / Receipt
ServiceHealthSnapshot
ServiceRestartPolicy
ServiceFailurePropagationPolicy
ServiceDependencyBinding
```

`ServiceInstanceHandle` must map to one exact process/domain generation.

## State model

Minimum lifecycle:

```text
Declared -> Admitting -> Starting -> Ready
Ready -> Degraded
Ready/Degraded -> Draining -> Stopped
Ready/Degraded -> Failed -> Draining/Quarantined
Stopped/Failed -> Replacement generation
```

`Quarantined` means old authority/effect closure is not sufficiently proven for ordinary replacement/reclaim.

## Dependency behavior

Hard dependency:

- must be admitted before dependent start;
- readiness policy is explicit;
- failure can trigger dependent degrade, drain, or stop according to policy;
- rebinding after dependency replacement requires a new exact binding.

Optional dependency:

- absence is an explicit degraded condition;
- late arrival may cause explicit rebinding;
- optional dependency never grants extra capability implicitly.

Graph cycles are rejected unless a future explicit bootstrap primitive is introduced. This phase does not invent one.

## Restart and replacement

Restart is generation-changing replacement. Required sequence:

```text
observe failure
 -> stop new admissions to old generation
 -> cancel/drain cancellable work
 -> close or contain external effects
 -> revoke/close old authority
 -> create generation N+1
 -> run fresh manifest/budget/capability admission
 -> bind dependencies explicitly
 -> publish readiness
```

If old external work is uncontained and resource exclusivity would be violated, replacement must remain blocked or use a policy-approved isolated replacement resource; it may not pretend old work disappeared.

## Health model

Health states are observations:

```text
Starting
Ready
Degraded
Draining
Unhealthy
Failed
Quarantined
Stopped
```

Health evidence cannot replace process generation, provider closure or capability validation.

Health producers may include heartbeat, request progress, dependency state, external-operation backlog, budget pressure and provider state, but every producer is typed and policy-scoped.

## Crash-loop control

Add bounded restart policy:

- maximum restarts per window;
- backoff class;
- terminal crash-loop state;
- operator-visible reason;
- no unbounded immediate restart.

Backoff affects orchestration only; it must not alter correctness of effect closure.

## Upgrade / planned replace

Support a planned replacement flow:

- validate new definition;
- optional ordinary checkpoint in later phase;
- drain old instance;
- create new generation;
- explicitly rebind dependencies/capabilities;
- publish readiness;
- retire old definition instance.

No live provider lease is copied across generations.

## Kernel/runtime integration

Supervisor must consume existing APIs for:

- process creation/teardown;
- capability mint/revoke/delegate;
- platform/external resource teardown;
- later budget admission;
- later deadline/cancellation;
- later checkpoint and telemetry.

Supervisor is not on the ordinary IPC or compute hot path after startup.

## Security requirements

- unprivileged services cannot issue arbitrary supervisor mutations;
- service self-restart is a request to supervisor policy, not direct generation creation;
- dependency names are not capabilities;
- supervisor internal root authority is not exported to services;
- restart does not inherit debug/evidence authority unless policy remints it.

## Required tests

1. Hard dependency startup ordering.
2. Optional dependency absent -> explicit degraded state.
3. Dependency replacement -> dependent exact rebinding.
4. Service crash with no external work -> generation N+1 replacement.
5. Crash with live closable external operation -> drain then replace.
6. Crash with ambiguous external effect -> quarantine/block unsafe replacement.
7. Old capability rejected by new service generation.
8. Restart budget/backoff prevents crash loop.
9. Planned replace preserves only explicitly recreated state.
10. Process teardown failure leaves supervisor state truthful and fail-closed.

## Exit criteria

Supervisor can start, observe, drain, stop and replace services with generation exactness; dependency graph and crash containment are executable; no stale authority crosses a restart boundary.