# P09 — HybridCPU-v2 co-design integration without ISA changes

## Goal

Connect SingNext resource authority to HybridCPU-v2 through provider-neutral semantic contracts while preserving independent CPU legality, provider admission and SingNext authority.

## Hard boundary

No changes required to:

```text
HybridCPU ISA
8-slot VLIW bundle format
SMT model
typed lane numbering/placement rules
register file / rename / retire architecture
memory pointer representation
memory tags
```

## Existing cross-project strengths to preserve

HybridCPU ExternalRuntime already models an opaque provider-neutral lifecycle:

```text
Prepared -> Admitted -> Submitted -> DeviceComplete -> Visible -> Published -> Released
```

and independent CPU guard + provider admission. vNext resource semantics must compose with these, not replace them.

## Preferred integration order

### Mode A — SingNext-only resource enforcement

If host/SingNext can enforce an upper bound without widening provider contract, do not change HybridCPU contract.

### Mode B — map to existing semantic provider contracts

Use existing operation/effect/visibility/cancellation/generation fields if they exactly represent the required semantics.

### Mode C — additive provider-neutral resource contract

Only if exact resource admission/usage evidence cannot be represented, add a versioned contract in `HybridCPU_ExternalRuntime.Contracts`, e.g. conceptually:

```text
ExternalResourceRequest
  operation correlation
  semantic resource class
  max execution envelope
  preemption requirement
  generation snapshot

ExternalResourceAdmissionReceipt
ExternalResourceUsageReceipt
```

These are evidence/provider admission facts, never SingNext capabilities.

## Double/triple gate

Submission requires:

```text
SingNext local effect authority
AND SingNext resource lease
AND HybridCPU CPU/runtime legality guard
AND provider admission
```

No fact substitutes for another.

## Usage evidence

HybridCPU/provider may report usage, but cannot settle/replenish SingNext resource authority. Exact receipt correlation is mandatory.

## Retire/publication discipline

Do not conflate:

```text
HybridCPU retire
provider DeviceComplete
SingNext resource settlement
memory Visible
OS Published
Released
```

For staged outputs, existing HybridCPU publication evidence/commit coordinator continues to gate architectural publication.

## Tests

- LocalAllowed=false, CPU/provider true -> no submit;
- resource lease absent, all other gates true -> no submit;
- CPU guard stale, local/resource true -> no submit;
- provider generation changes -> stale;
- usage receipt cross-operation replay -> reject;
- completion before visibility -> no OS publication;
- no public reflection-visible Lane/Opcode/Slot/Queue/Topology fields in SingNext contracts.

## Exit criteria

`FG-VNX-HCPU-RESOURCE-CONTRACT` and usage gate remain contour-specific. No claim of hard timing guarantee yet.
