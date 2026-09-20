# P10 — Resource scheduler and provider-agent policy layer

## Goal

Introduce a placement/resource policy layer inspired by Barrelfish without distributing authority truth.

## Architecture

```text
Authority Core
   |
   +-- ResourceScheduler / PlacementPolicy   (policy only)
   |
   +-- ProviderAgent[Host]
   +-- ProviderAgent[HybridCPU]
   +-- ProviderAgent[CXL]
   +-- ProviderAgent[Device...]
```

Provider agents may own local provider bindings/caches and expose non-authoritative observations:

- availability;
- queue depth;
- latency/bandwidth classes;
- thermal/load state;
- preemption granularity;
- qualified semantic resource classes.

## Singular authority rule

Agents MUST NOT own or replicate:

- capability lineage;
- Region ownership;
- resource-capability lineage;
- resource lease ownership;
- publication truth.

Those remain exact local SingNext owners.

## Policy API

Scheduler receives immutable intent/evidence snapshots and returns a **placement decision**. It does not mint authority. Before execution, owners revalidate all exact generations.

## Cache discipline

Cache:

```text
where/how to validate
provider generation
performance evidence
```

Never cache:

```text
authorized=true
lease still valid=true
region still owned=true
```

## Fairness

Implement fairness initially over already-admitted leases. Do not make scheduler policy a source of new budget.

## Exit criteria

ResourceScheduler can choose Host vs HybridCPU model/executable candidates while all authority decisions remain external to scheduler.
