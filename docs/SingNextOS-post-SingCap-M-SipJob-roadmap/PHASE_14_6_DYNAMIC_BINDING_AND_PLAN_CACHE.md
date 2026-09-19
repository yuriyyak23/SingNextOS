# P14-6 — Dynamic Plan Binding from Precompiled Qualified Stages and Safe Caching

## Goal

Allow runtime selection/aggregation of a subset of admitted SIP stages without dynamic code generation and without caching authority decisions.

## Dynamic composition model

Allowed:

```text
precompiled generated StageThunk A
precompiled generated StageThunk B
precompiled generated StageThunk C

runtime selects A -> C -> B
verifier builds immutable plan
binder resolves exact service/session/thunk candidates
```

Forbidden:

```text
Reflection.Emit
runtime IL generation
arbitrary Assembly.Load
unqualified dynamic delegate supplied by ManagedCap code
MethodInfo.Invoke as steady-state Job dispatch
string-only service/method binding without digest/signature identity
```

## Qualified stage catalog

Introduce one TCB-private deterministic catalog derived from admitted generated artifacts. The catalog is **not authority**. It maps exact immutable identities to trusted code locations/binding factories, for example:

```text
(contract digest, message id, generated thunk schema version)
    -> trusted stage thunk descriptor
```

Catalog entries are invalidated/rebuilt on runtime/toolchain/admission-policy tuple change.

## Job binding

Binding resolves:

```text
exact service identity/incarnation
exact generated stage thunk
exact EndpointSession handles or creation policy
candidate exact capability references for each declared requirement
expected seal/Region schema constraints
feature-gate availability
```

A binding contains no durable authorization result.

## Cache rules

`FG-PLAN-CACHE` may cache:

```text
plan verification result keyed by canonical digest + runtime policy tuple
exact binding keys/references
contract/schema/thunk lookup
```

Every run still validates live:

```text
process/service/session generation
capability revocation/lineage/resource generation
seal generation/state
Region generation/use state
feature-gate/runtime incarnation
```

Stale/ambiguous cache entry fails closed or rebinds.

## Split-runtime fallback

`FG-SPLIT-RUNTIME` permits one logical plan to become:

```text
[fused local segment]
      -> ordinary SIP transport barrier
[remote/other-runtime segment]
      -> ordinary SIP transport barrier
[fused local segment]
```

The plan must not expose transport placement as authority. Relocation/topology change can alter materialization boundaries without changing the SIP contract.

## Job handle semantics

If an opaque reusable handle is exposed, it behaves like identity/binding selection, not permission. `ExecuteJob(handle, ...)` still performs current exact authority admission. Wrong caller/session/incarnation or stale binding is denied.

## Primary paths

```text
sdk/SingPlus.Generators/
tools/SingPlus.Admission/
src/Runtime/SingPlus.Runtime/Services/
new internal Jobs catalog/binder/cache
```

## PR slices

- P14-6.1: deterministic generated stage catalog.
- P14-6.2: runtime plan binder.
- P14-6.3: stale-safe plan verification/binding cache.
- P14-6.4: opaque Job binding handle (only if needed).
- P14-6.5: split-runtime segmentation using ordinary SIP fallback.

## Tests

```text
same name/different digest substitution denied
service restart invalidates binding
runtime restart invalidates old realm/binding
capability revoke after cache creation still denies execution
Region generation change after cache creation still denies execution
AdmissionVerifier rejects dynamic codegen/assembly load path
split-runtime and same-runtime produce equivalent declared semantics
feature gate change invalidates incompatible cache
```

## Performance gate

Report cold verify/bind, warm cached bind and steady-state run separately. Cache speedups do not justify skipped live checks.

## Exit criteria

Runtime can select arbitrary **admitted precompiled** stage subsets within bounded plan rules, and no dynamic-code or stale-positive-authorization shortcut is introduced.
