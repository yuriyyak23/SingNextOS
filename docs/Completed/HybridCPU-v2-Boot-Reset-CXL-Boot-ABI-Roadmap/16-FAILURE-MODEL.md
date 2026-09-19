# Boot Failure Model

## 1. Policy

Every failure class has a deterministic detection point, bounded recording behavior and an explicit fallback. Unknown/ambiguous state fails closed; it never means “probably safe to continue”.

## 2. Failure taxonomy

| Failure | Detect | Record | Fallback | Recovery/Halt |
|---|---|---|---|---|
| no CXL root | platform root discovery empty | `NoCxlRoot` | next non-CXL/secondary target | local recovery |
| no Type-3 device | bounded enumeration | `NoBootCapableDevice` | next target | local recovery |
| device/config timeout | deadline | BDF/evidence digest + timeout | next candidate | recovery after budgets |
| malformed PCI capability chain | bounded parser | `MalformedPciCapabilities` | reject endpoint | next candidate |
| CXL capability unsupported | capability parser | `UnsupportedCxlBootPath` | reject endpoint | next candidate |
| mailbox unavailable | transport | `MailboxUnavailable` | try no-LSA path if capacity info sufficient; else candidate fail | next candidate |
| mailbox timeout/failure | command status/deadline | command class, no raw secret payload | no-LSA or candidate fail | next candidate |
| invalid LSA/HBLR | CRC/version/bounds | `InvalidLocator` | ignore locator, map canonical anchor | continue |
| wrong BootVolumeId | header/manifest mismatch | expected/observed UUID digest | next candidate | next target |
| duplicate BootVolumeId same image | replica grouping | replica facts | deterministic replica choice | continue |
| duplicate split-brain | same gen different ImageId | `AmbiguousBootVolume` | try explicit protected selection or reject group | confirmed/other target/recovery |
| no persistent capacity | Identify/partition | `NoPersistentCapacity` | next candidate | next target |
| aperture unavailable | platform map | `NoBootAperture` | next target if other medium | local recovery |
| HDM programming failure | transaction/readback | path evidence digest | rollback partial mapping, next candidate | recovery |
| decoder conflict/unknown preserved state | map precheck | `DecoderConflict` | clear only owned range or reject path | recovery if all fail |
| persistent read fault | mapped read | offset/window + error | retry bounded once if transient, then candidate fail | next replica |
| invalid HBV header | magic/CRC/bounds | `InvalidVolumeHeader` | other header copy, then candidate | next candidate |
| equal-sequence conflicting headers | comparison | `AmbiguousVolumeHeader` | reject candidate | next replica |
| unknown manifest major | version | `UnsupportedManifestVersion` | other selected image/target | recovery/update environment |
| malformed manifest | bounds/overflow/count | `MalformedManifest` | reject image | confirmed/other target |
| bad signature | crypto | key ID/image ID | reject image, never unsigned production fallback | other signed image/recovery |
| revoked/unknown key | trust store | key ID | reject image | recovery |
| bad component hash | copy/hash | component index | reject image/replica | same image on another replica, then fallback |
| rollback generation | protected floor | generation/floor | reject image | confirmed allowed image or recovery domain |
| unsupported CPU ABI | compatibility | required/current | reject image | older compatible confirmed/recovery |
| unsupported FW Boot ABI | compatibility | required/current | reject image | compatible image/recovery |
| unsupported compression/feature | Stage1 capability | feature ID | reject image | other image/recovery |
| Stage1 allocation/copy failure | allocator/copy | required bytes/range | other image only if RAM state reset/cleaned | recovery |
| Stage1 entry invalid | bounds/alignment | image/component | reject image | fallback |
| Stage1 runtime failure | typed return/reset | component/error | consume trial attempt if trial | reset -> fallback |
| kernel copy/hash failure | Stage1 | component | reject image | reset -> confirmed/recovery |
| kernel entry invalid | Stage1 | range/alignment | reject image | reset -> fallback |
| BootInfo construction overflow | Stage1 | record/type | fail image/platform | local recovery |
| all primary targets failed | selector | aggregate digest/codes | secondary/local recovery | ROM monitor/halt |
| local recovery invalid | ROM verify | recovery key/image | ROM monitor | halt/service mode |
| protected BootPolicy corrupt | protected store | sequence/error | immutable recovery policy | monitor/service |
| trust store corrupt | root verify | generation/error | immutable recovery key path | service/halt |
| rollback store unavailable | protected store | error | fail secure primary | signed recovery/service |
| device removed mid-boot | config/read error | endpoint evidence | invalidate mapping | next replica |
| warm-reset stale mapping | validation mismatch | `StaleFirmwareMapping` | tear down/rebuild | recovery if cannot |
| post-handoff rediscovery mismatch | Sing provider | OS diagnostics | quarantine firmware mapping | OS degraded/recovery |

## 3. Error code namespace

Use stable boot-domain codes, not CXL raw status as public ABI:

```c
enum HybridBootFailure : uint32_t {
    None = 0,
    PlatformProfileInvalid = 0x0101,
    ProtectedStoreFailure  = 0x0102,
    NoCxlRoot              = 0x0201,
    NoBootCapableDevice    = 0x0202,
    CxlTransportTimeout    = 0x0203,
    InvalidLocator         = 0x0204,
    HdmMappingFailure      = 0x0205,
    InvalidVolumeHeader    = 0x0301,
    WrongBootVolume        = 0x0302,
    AmbiguousBootVolume    = 0x0303,
    MalformedManifest      = 0x0401,
    BadManifestSignature   = 0x0402,
    RollbackRejected       = 0x0403,
    IncompatibleCpuAbi     = 0x0404,
    IncompatibleFirmwareAbi= 0x0405,
    ComponentHashMismatch  = 0x0501,
    CopyFailure            = 0x0502,
    InvalidEntryPoint      = 0x0503,
    AllTargetsFailed       = 0x0601,
    RecoveryFailed         = 0x0602,
};
```

Backend-specific status can be hashed/stored in privileged diagnostics, not used as long-term semantic failure contract.

## 4. Retry policy

Retries are for plausible transient transport failures only, never for deterministic security failures.

- bad signature/hash/rollback/ABI: zero retry on same bytes;
- mailbox/config timeout: at most one short retry per command class;
- CXL read timeout: one retry after mapping revalidation if platform classifies transient;
- device removed: no retry until optional bounded rescan;
- HDM program failure: one cleanup/retry only if backend reports partial transient failure; otherwise next candidate.

This prevents malicious hardware from holding boot indefinitely.

## 5. Cleanup after candidate failure

Before trying another endpoint/image:

- stop accesses to current aperture;
- invalidate source cache lines as required;
- disable/rollback temporary decoder state owned by firmware;
- invalidate mapping generation/token;
- zero Stage-1 destination if executable bytes were partially copied;
- release scratch allocations;
- retain only bounded failure evidence.

## 6. Trial image failure

Security failure of a trial image consumes/clears trial according to policy immediately; a signature/hash failure should normally clear trial rather than waste multiple boots. Runtime watchdog/crash consumes one attempt. Confirmed image security failure is serious: try another replica with same signed digest, then recovery.

## 7. Split-brain behavior

Do not select “highest generation on first enumerated device” when replicas disagree.

Rules:

- protected Trial/Confirmed ImageId breaks ambiguity if exactly one matching signed image exists;
- if protected state says image X but replicas present same generation Y and Z, ignore Y/Z;
- first-install mode with same max generation but different ImageIds is ambiguous -> local recovery/provisioning;
- diagnostics list replica IDs and manifest digests, never choose by BDF order.

## 8. Post-kernel failure

Once kernel entry occurs, firmware does not continue the boot state machine in parallel. SingNextOS owns runtime error policy. A fatal early-boot error requests a reset with reason; next Stage-0 attempt consumes trial state/fallback deterministically.
