# Protected State, A/B, Rollback and Recovery

## Separate generation domains

At minimum:

```text
CapsuleState:
  ActiveCapsuleSlot
  ConfirmedCapsuleGeneration
  TrialCapsuleGeneration
  CapsuleRollbackFloor

ImageState:
  ActiveImageSlot
  ConfirmedImageGeneration
  TrialImageGeneration
  ImageRollbackFloor

Ephemeral:
  BootMappingGeneration

Runtime-only:
  ProviderGeneration
  RegionGeneration
```

Do not reuse one integer counter for multiple authority domains.

## Trial record

A trial record contains:

- state format/version;
- domain (`Capsule` or `Image`);
- candidate generation;
- trial nonce;
- remaining attempts;
- previous confirmed generation;
- durable sequence number;
- integrity/authentication fields required by the protected-store design.

## Confirmation rule

```text
kernel entry != successful boot confirmation
```

Confirmation occurs only via the designated post-boot success path after required runtime admission/health conditions are met.

## Durable update protocol

Use a versioned replicated or repository-approved atomic state protocol:

1. read all replicas;
2. validate integrity/version;
3. select a unique highest committed sequence consistent with rollback floor;
4. split-brain or ambiguous highest state => recovery/fail closed;
5. write new candidate replica;
6. flush/verify;
7. publish commit marker/sequence according to store contract.

A torn write must not:

- lower rollback floor;
- increase trial attempts;
- confirm an unconfirmed generation;
- cross capsule/image domains.

## Rollback floor

Writable CXL media can propose an image candidate but cannot lower either capsule or OS image rollback floor.

Rollback floor is protected platform state, not BootVolume-owned policy.

## Recovery

Recovery source must be:

- local;
- bounded;
- authenticated/verified;
- independent of unbounded network/media scanning.

Recovery failure ends in fail-stop/reset according to policy.

Watchdog/reset reason is evidence for the state machine, not proof of successful confirmation.
