# Phase 2 — Stateful SIP contracts

## Goal

Extend the current generated typed SIP model from “typed request/response messages” to “typed legal protocol transitions with ownership and terminal-state semantics”.

Track A already established correlated responses, async waiting and channel-close cancellation. This phase generalizes that discipline without replacing the existing transport.

## Required contract metadata

A service contract should be able to describe:

```text
ContractId
ContractVersion
ContractDigest
ProtocolStates[]
Messages[]
AllowedTransitions[]
TerminalStates[]
CancellationTransitions[]
OwnershipEffects[]
CapabilityRequirements[]
```

The exact source syntax may be attributes, generated descriptors or a contract DSL already compatible with the generator. The runtime representation matters more than surface syntax.

## Protocol invariant

A message is admissible only when all are true:

```text
channel/session is live
AND message is valid in current protocol state
AND required capability/resource generations are live
AND ownership preconditions hold
AND platform preconditions hold when relevant
```

## Example patterns

File session:

```text
Opened -> Read/Write/Flush -> Opened
Opened -> Close -> Closed
Closed -> no further I/O
```

DMA operation:

```text
CpuOwned -> Grant -> DeviceMapped
DeviceMapped -> Submit -> InFlight
InFlight -> Complete -> Completed
Completed -> Acquire -> CpuOwned
```

Surface presentation:

```text
Writable -> Present -> Presented
Presented -> ReleaseFence -> Writable
```

## Generator/runtime outputs

The generator SHOULD emit:

- protocol-state identifiers;
- transition validation;
- terminal-state handling;
- cancellation/close handling;
- ownership-effect metadata;
- client adapter guards where useful;
- server/runtime validation that remains authoritative even if a client is buggy.

## Failure semantics

Illegal transitions MUST fail closed and MUST NOT partially publish a response, transfer ownership or create a platform effect.

Close/termination must have defined outcomes for pending requests:

- completed and published stays committed;
- not-yet-committed requests cancel/fault according to contract;
- ownership is reclaimed/returned only after the contract-specific drain condition is met.

## Tests

Required negative tests include:

- message valid by type but illegal in current protocol state;
- duplicate terminal transition;
- request after close;
- stale session generation;
- ownership effect attempted from the wrong owner;
- cancellation racing completion;
- peer termination before and after publication;
- generated client bypass does not bypass server/runtime validation.

## Acceptance criteria

At least one real service contract must use generated protocol-state metadata end-to-end, and invalid transition tests must prove that no authority or ownership side effect occurs.
