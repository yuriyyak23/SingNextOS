# Boot Identity and Policy

## 1. Identity layers

Three separate identity planes are mandatory.

### Physical device identity/evidence

Examples: PCIe DSN, device serial, vendor/device, PCI BDF, current fabric path. Used for locating, pinning, diagnostics and substitution evidence. It is not stable enough to name an installation and is not authority.

### Logical boot-volume identity

`BootVolumeId` is a 128-bit UUID generated at installation/provisioning time. It names the logical SingNextOS boot volume across physical replacement and intentional replicas.

### Boot-image identity

`ImageId` is an immutable UUID for one signed image set. `ImageGeneration` is a strictly monotonic unsigned generation inside a `RollbackDomainId`.

Add `ReplicaId` to distinguish physical/logical copies of the same `BootVolumeId` without abusing DSN.

## 2. BootVolumeId lifecycle

- fresh installation: new random UUID;
- normal A/B update: unchanged;
- exact physical device replacement preserving installation: unchanged after controlled copy/provision;
- intentional replica: same `BootVolumeId`, new `ReplicaId`;
- cloned independent machine/install: tooling MUST generate a new `BootVolumeId` unless explicitly creating a replica;
- factory image template: contains placeholder/zero ID and is instantiated during provisioning, not booted as-is in production.

Knowing `BootVolumeId` grants no memory access.

## 3. Boot policy storage split

Do not put all security state in one CXL record.

| Storage | Contents | Mutability |
|---|---|---|
| immutable ROM | parser/algorithms, ABI support, compiled fallback key if profile uses it | none |
| OTP/fuse/secure element | root-key hash(es), production/debug lock, device/platform identity seed | one-time/monotonic |
| protected NVRAM | BootPolicy, BootTrustStore, BootState, per-domain rollback floors, failure counters | authenticated, controlled writes |
| CXL persistent media | BootVolumeHeader, signed manifests, payloads, update journal | OS/update tooling writable |
| LSA | optional locator only | provisioning/update tooling; untrusted at boot |

Critical rule: `minimumAllowedGeneration` does **not** live solely in BootPolicy because a downgraded policy must not lower rollback protection. It is a separate monotonic protected record.

## 4. HybridBootPolicy

Logical wire layout (all offsets/lengths validated; fixed fields little-endian):

```c
struct HybridBootPolicyHeaderV1 {
    uint32_t magic;              // 'HBP1'
    uint16_t version;            // 1
    uint16_t headerSize;
    uint32_t totalSize;
    uint32_t flags;
    uint64_t policyGeneration;
    Guid     platformId;
    Guid     keySetId;
    Guid     defaultRecoveryTargetId;
    uint16_t targetCount;        // <= 16 v1
    uint16_t reserved0;
    uint32_t crc32c;             // structural corruption; outer protected record authenticates
};

enum BootTargetKind : uint16_t {
    CxlPersistentVolume = 1,
    LocalRecovery       = 2,
    LocalServiceImage   = 3,
};

enum PhysicalSelectorKind : uint16_t {
    None       = 0,
    PciDsn     = 1,
    DeviceSerial = 2,
    PlatformSlot = 3              // platform-defined physical slot label
};

struct BootTargetV1 {
    Guid     targetId;
    uint16_t kind;
    uint16_t priority;            // lower = earlier
    uint32_t flags;
    Guid     bootVolumeId;
    Guid     rollbackDomainId;
    uint64_t requiredProperties;  // persistent/type3/etc semantic bits
    uint16_t physicalSelectorKind;
    uint16_t physicalSelectorLength;
    uint8_t  physicalSelector[32]; // optional preference/constraint
    uint16_t maxAttemptsPerBoot;  // bounded
    uint16_t reserved;
};
```

Policy target order is `(priority, targetId)`; array order is not semantic.

## 5. Policy flags

Recommended:

```text
RequireSecureBoot
AllowDevelopmentImages       # valid only when debug fuse/policy permits
RequirePhysicalSelectorMatch # rare locked appliance mode
PreferPhysicalSelectorMatch  # normal pin/hint mode
AllowReplicaFailover
AllowBootstrapHighestValid   # only when no confirmed state exists
RecoveryOnly
```

Physical selector defaults to preference, not requirement.

## 6. BootTrustStore

Protected NVRAM record signed/authorized by root:

```c
struct BootTrustStoreV1 {
    Guid     keySetId;
    uint64_t keySetGeneration;
    uint16_t imageKeyCount;
    uint16_t recoveryKeyCount;
    uint16_t revokedKeyCount;
    KeyDescriptor imageKeys[...];
    KeyDescriptor recoveryKeys[...];
    KeyId revokedKeys[...];
    Signature rootAuthorization;
};
```

ROM validates key-store generation/revocation before image manifest. Key rotation is therefore possible without replacing ROM, while a fused root hash anchors the authorization chain.

## 7. Protected BootState

```c
struct TargetBootStateV1 {
    Guid     targetId;
    Guid     confirmedImageId;
    uint64_t confirmedGeneration;
    Guid     trialImageId;           // zero => none
    uint64_t trialGeneration;
    uint16_t trialAttemptsRemaining;
    uint16_t consecutiveTargetFailures;
    uint32_t flags;
    uint64_t stateSequence;
};
```

BootState is double-buffered/authenticated by the platform store. An attacker changing CXL `activeSlot` metadata cannot override it.

## 8. Rollback floor

```c
struct RollbackFloorV1 {
    Guid     rollbackDomainId;
    uint64_t minimumAllowedGeneration;
    uint64_t floorSequence;
};
```

Updates are monotonic: protected storage rejects a lower value. Normal production confirmation raises floor according to policy. Recovery can use a separate rollback domain/key so a primary OS floor does not brick the recovery environment.

## 9. Optional physical pinning

Examples where `RequirePhysicalSelectorMatch` may be reasonable:

- forensic appliance where boot media replacement must be an explicit provisioning event;
- compliance device bound to a chassis slot/device certificate;
- service-mode recovery.

Default product behavior should use `PreferPhysicalSelectorMatch`: try expected DSN first, but accept a valid replacement replica with matching BootVolume and trusted manifest.

## 10. Duplicate BootVolumeId

Duplicate UUID is not automatically an error; it can mean replica. Use signed `ReplicaId`/manifest tuple:

- same `BootVolumeId + ImageId + ImageGeneration`, different `ReplicaId`: legitimate replicas;
- same `BootVolumeId + generation`, different `ImageId`: split-brain unless protected BootState explicitly selects one;
- same `ReplicaId` with conflicting content: corruption/spoof; reject that replica;
- same physical DSN reporting multiple replicas: possible MLD/logical-device case; do not collapse solely by DSN.

## 11. Provisioning operations

Provisioning tool must atomically coordinate:

1. create/retain BootVolumeId;
2. allocate ReplicaId;
3. install payloads and signed manifest;
4. write redundant BootVolumeHeader;
5. optionally write HBLR to allowed LSA namespace;
6. update protected BootPolicy only if new logical target is introduced;
7. never lower rollback floor.

Device replacement does not require changing BootPolicy when BootVolumeId is preserved and physical selector is only preferred.
