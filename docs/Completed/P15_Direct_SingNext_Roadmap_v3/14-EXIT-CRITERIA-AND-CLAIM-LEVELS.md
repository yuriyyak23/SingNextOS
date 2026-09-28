# Exit Criteria and Claim Levels

## ContractOnly

Projects/contracts compile; ABI vectors and architecture/package/security-inventory gates pass. No functional boot claim.

## ModelValidated

Production-intent pure Core semantics exist and pass deterministic/property + model differential tests. A `*Model` alone never qualifies.

## AdapterQualified

A SingNext-owned executable adapter path runs with deterministic fault injection and boundedness checks. For handoff, production implementations/wiring for fresh discovery and aperture retirement must exist; test-local classes are insufficient.

## IseValidated

Requires an end-to-end run on a **requalified** HybridCPU baseline with capsule image entry, required CXL/HDM behavior (or exact explicitly scoped equivalent), verified copy, BootInfo handoff, existing importer, fresh admission, and retirement/quarantine.

Current SingNextOS qualification pin drift means `IseValidated` cannot be inherited from the old pin.

## QemuProtocolValidated

Protocol/state-machine evidence only. It cannot be renamed ISE or hardware evidence.

## HardwareValidated

Requires direct evidence on the named hardware/profile for PCI/CXL enumeration, mailbox faults/timeouts, decoder commit/readback/rollback, DMA isolation, reset, cache/order/persistent read visibility, retirement, and protected-state durability.

## Roadmap implementation-readiness

The roadmap is ready to drive refactoring when P15-00 confirms no new drift, every authoritative responsibility has exactly one owner, new project classifications are enforceable, and all external gaps are gates rather than hidden prerequisites.
