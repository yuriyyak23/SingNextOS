# Protected State, A/B, Rollback and Recovery

## Distinct domains

```text
Capsule: ActiveSlot, ConfirmedGeneration, TrialGeneration, RollbackFloor
Image:   ActiveSlot, ConfirmedGeneration, TrialGeneration, RollbackFloor
Boot mapping: BootMappingGeneration
Runtime: ProviderGeneration, RegionGeneration
```

No shared integer is an authority shortcut across these domains.

## Trial state

Each domain records version, candidate generation, nonce, remaining attempts, previous confirmed generation, durable sequence, and integrity/authentication fields required by the protected-store contract.

## Confirmation

```text
kernel entry != successful boot confirmation
```

Confirmation is a later explicit success transition after required runtime admission/health criteria.

## Durable protocol

- validate all replicas;
- reject unknown/incompatible state versions;
- choose a unique highest committed sequence consistent with rollback floor;
- split-brain/ambiguous top state => bounded recovery/fail closed;
- write candidate -> flush -> read/verify -> publish commit marker according to store contract;
- torn write cannot lower floor, increase attempts, confirm trial, or cross domains.

Writable CXL media cannot lower rollback floor.

## Implementation/evidence split

Core state machine may become `ModelValidated`/`AdapterQualified` against deterministic protected-store fakes. Claims about atomicity, monotonic counters, power-loss persistence, watchdog/reset reason, or tamper resistance remain hardware/platform dependencies until direct evidence.
