# Recovery and A/B Boot

## 1. Production recovery contract

A system whose primary OS is on CXL MUST still have a useful non-CXL failure path. Minimum production chain:

```text
Primary CXL target / replicas
 -> Secondary CXL target(s)
 -> signed local recovery image
 -> immutable ROM recovery monitor
 -> halt/debug with persistent failure code
```

Network recovery is optional and belongs in the signed local recovery environment, not immutable ROM.

## 2. Why local recovery is mandatory

Without it, failure of CXL root port, fabric assignment, mailbox, decoder, device power or persistent media can remove both OS and diagnostics. A local signed recovery image keeps storage/network/debug tooling available even when no CXL memory is reachable.

## 3. A/B state model

A and B are signed image slots on a BootVolume. Protected BootState names which image is confirmed/trial; CXL header only describes available manifests.

State machine:

```text
Confirmed(A)
  | updater writes/verifies B
  v
TrialPending(B, attempts=N)
  | reboot
  v
TrialBoot(B)
  |-- ConfirmBoot(B) --> Confirmed(B), clear trial, raise floor
  |-- crash/reset ----> attempts--, TrialPending(B)
  |-- attempts==0 ----> FallbackConfirmed(A)
```

After B confirms and rollback floor rises to B generation, A may remain physically present but is no longer executable if below floor.

## 4. Protected APIs

```csharp
interface IPlatformBootControl
{
    void SetTrial(Guid targetId, Guid imageId, ulong generation, ushort maxAttempts);
    void ConfirmCurrentBoot(Guid imageId, ulong generation);
    void ClearTrial(Guid targetId);
    void SetNextBootTarget(Guid targetId, Optional<Guid> imageId);
    void RequestReset(HybridResetReason reason);
}
```

Caller authorization is a platform/SingNextOS kernel policy problem. Applications cannot directly confirm arbitrary image IDs.

## 5. Safe update sequence

Updater prerequisites:

- currently running system has fresh CXL provider authority;
- target BootVolume is opened through provider/storage service;
- new manifest signature is valid before changing trial state;
- new generation satisfies rollback policy.

Protocol:

```text
write inactive payloads
 -> persistence fence
 -> write signed manifest redundantly
 -> persistence fence
 -> update inactive HBV metadata copy
 -> persistence fence
 -> re-read + hash verify
 -> optionally update HBLR hint
 -> platform SetTrial(new ImageId, generation, attempts=3)
 -> reboot when policy allows
```

Power loss before `SetTrial` leaves current confirmed image untouched. Power loss after `SetTrial` but before reboot simply leaves a pending trial.

## 6. Trial success milestone

Do not confirm at kernel entry. Recommended SingNextOS milestone:

- kernel core initialized;
- essential local/system memory managers operational;
- boot manifest/BootInfo accepted;
- required root services started;
- CXL provider fresh-discovery outcome handled;
- persistent root/critical service health checks complete;
- no fatal upgrade migration pending.

Then call `ConfirmCurrentBoot`.

## 7. Trial failure accounting

Reset reason and a protected “boot reached confirmation?” flag distinguish:

- explicit rollback request;
- watchdog/crash before confirmation;
- power loss (platform may not reliably distinguish);
- manual reset.

Conservative default: any reboot of an unconfirmed trial consumes one attempt unless platform can prove it was a benign power event and policy chooses otherwise.

## 8. Multi-replica A/B

Replicas may not update atomically together. Protected state names `ImageId`, not “slot B”. Boot selection can use any replica containing the selected signed ImageId/generation. Thus:

- replica R1 may store B in slot B;
- replica R2 may store the same ImageId in slot A after compaction;
- slot letter is media-local descriptive state;
- image identity is global semantic selection.

If no replica has the selected trial image, record failure and fall back to confirmed image without mutating the logical BootVolume identity.

## 9. Recovery image policy

### Local recovery

- stored in local flash/ROM-adjacent immutable-or-A/B storage;
- signed with recovery-authorized key;
- separate rollback domain recommended;
- has minimal PCI/CXL diagnostics/update tooling;
- can provision/repair BootPolicy and CXL volume only with privileged authorization.

### CXL recovery slot

Useful but optional; it does not replace local recovery because CXL path itself may be unavailable.

### ROM monitor

Last resort only; no full OS/filesystem/network stack.

## 10. Recovery boot causes

Enter local recovery when:

- recovery strap/request set;
- BootPolicy explicitly chooses recovery next;
- all primary/secondary targets exhaust attempt/failure budgets;
- trust store/policy is valid but no compatible image exists;
- CXL root unavailable;
- repeated trial failures exceed threshold;
- platform firmware update requires service environment.

If protected trust/policy storage itself is corrupt, ROM uses an immutable emergency recovery key/profile or service/manufacturing path according to production policy.

## 11. Reset and CXL reconfiguration

SingNextOS-requested reboot for CXL reconfiguration MAY use warm reset without electrically resetting the CXL topology. Boot firmware revalidates path/mapping regardless. If reconfiguration intentionally moved BootVolume to another device, same BootVolumeId allows boot to follow it without policy rewrite.

## 12. Device replacement

Supported replacement workflow:

1. new Type-3 device provisioned with copied valid volume;
2. retain BootVolumeId, create new ReplicaId;
3. DSN naturally changes;
4. optional BootPolicy physical selector updated only if locked/pinned policy demands;
5. boot selects by BootVolumeId and signed selected ImageId;
6. OS fresh provider discovery treats new hardware identity as new device generation.

## 13. Firmware/trust updates

Boot ROM remains immutable. BootTrustStore and local recovery may be A/B/protected records. Update order must preserve at least one key capable of verifying current confirmed primary and recovery image. Key revocation that would brick both paths is rejected by provisioning tooling.

## 14. Recovery telemetry

Protected boot log stores bounded entries:

```c
struct BootFailureRecordV1 {
    uint64_t sequence;
    uint32_t resetReason;
    uint32_t failureCode;
    Guid targetId;
    Guid bootVolumeId;
    Guid imageId;
    uint64_t imageGeneration;
    uint8_t evidenceDigest[32];
};
```

No secrets/private keys/raw unbounded device data.
