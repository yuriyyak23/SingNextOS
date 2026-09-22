# Exit Criteria and Claim Levels

## `ContractOnly`

Requires:

- projects/contracts compile;
- ABI golden vectors pass;
- architecture policy passes;
- no forbidden references/packages.

## `ModelValidated`

Requires:

- production-intent Core semantics exist;
- deterministic unit/property tests pass;
- retained model/oracle differential tests pass for supported scope.

A `*Model` implementation alone is not sufficient.

## `AdapterQualified`

Requires:

- executable SingNext-owned adapter path;
- deterministic fault injection;
- boundedness checks;
- qualification artifact;
- no hidden HybridCPU source change requirement.

Does not imply ISE execution.

## `IseValidated`

Requires end-to-end execution in the qualified HybridCPU ISE baseline, including:

- capsule image load/entry;
- required CXL boot path or exact supported ISE equivalent;
- verified copy;
- BootInfo handoff;
- kernel import;
- fresh runtime admission;
- aperture retirement/quarantine semantics.

## `QemuProtocolValidated`

Independent protocol evidence. It cannot be relabeled as HybridCPU ISE or hardware evidence.

## `HardwareValidated`

Requires direct evidence on the exact hardware/profile for:

- PCI/CXL discovery;
- mailbox timeouts/failures;
- decoder ordering/commit/readback;
- DMA isolation;
- reset behavior;
- persistent-capacity read/cache ordering;
- aperture retirement behavior.

## Roadmap readiness criterion

The roadmap itself is implementation-ready when:

- `P15-00` has no unowned authoritative responsibility;
- the corrected DAG is enforceable;
- all missing external behaviors have feature gates;
- all slices build/test independently;
- all mandatory faults have test owners;
- no claim level exceeds evidence;
- model retirement occurs only after differential proof.
