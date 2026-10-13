# Secure Boot and Anti-Rollback

## 1. Security objective

Only an image authorized by the platform trust root, compatible with current CPU/firmware ABI and not below the protected rollback floor may execute. CXL media and its locator metadata are untrusted inputs.

## 2. Chain of trust

```text
Immutable ROM
  + fused/OTP RootKeyHash + ProductionLock
        |
        v
BootTrustStore (authorized key set, revocations, key-set generation)
        |
        v
SingNextBootManifest signature
        |
        +--> Stage1 hash -> verified RAM execution
        |
        +--> kernel/services hashes -> verified RAM execution/data
```

## 3. Trust storage

### ROM

Contains verification code, supported algorithms and either root public key(s) or code to verify their hashes against fuse/OTP.

### OTP/fuse

Minimum production fields:

```text
RootKeyHash[]
ProductionLock
DebugAuthorizationMode
PlatformIdentitySeed / PlatformId binding if needed
```

### Protected NVRAM / secure element

- BootTrustStore;
- BootPolicy;
- BootState;
- rollback floors;
- bounded boot-failure log.

Protected means integrity + anti-replay/sequence protection appropriate to hardware. Plain CXL pmem is not protected boot state.

## 4. Signature verification

ROM verifies manifest signed region using key selected by `signingKeyId`. Key must:

- exist in active key set;
- be authorized for image role (primary/recovery/dev);
- not be revoked;
- satisfy production/debug policy.

Signature failure never falls back to unsigned execution in production.

## 5. Anti-rollback

`ImageGeneration` is scoped by `RollbackDomainId`.

Admission condition:

```text
manifest.signature valid
AND manifest.generation >= ProtectedRollbackFloor[domain]
AND image is selected by protected Confirmed/Trial state
```

### Confirmation rule

The floor is not raised when Stage-1 or kernel is merely entered. SingNextOS calls `ConfirmBoot(ImageId, Generation)` after its defined boot-success milestone (e.g. core services/provider initialization and persistent root health). The platform verifies the call refers to the currently booted verified image, then atomically:

1. sets confirmed image/generation;
2. clears trial state;
3. raises rollback floor per policy;
4. records success sequence.

This prevents a broken trial image from permanently blocking fallback before confirmation.

## 6. Rollback floor policy nuance

If product wants fallback from generation N trial to confirmed N-1, the floor must remain <= N-1 until N confirms. After N confirmation it may rise to N, making N-1 cryptographically disallowed. Recovery uses a separate domain so it remains serviceable.

## 7. Key rotation

Use root-authorized BootTrustStore generations:

```text
root -> KeySet generation 7: keys A,B; revoke old X
     -> image manifests signed by A/B
```

Rotation procedure installs new key set in protected storage before publishing manifests requiring it. Removing last recovery key is rejected unless manufacturing/service policy explicitly allows.

## 8. Development mode

Development behavior is explicit and measured:

- requires debug fuse/strap or root-authorized dev policy;
- MAY allow a development key or unsigned image only if both hardware and policy permit;
- `HybridBootInfo.SecurityState` records `DevelopmentMode`, `ManifestVerified`, `UnsignedAllowed`, key ID and reasons;
- production-locked system ignores software-only request to enable unsigned boot;
- SingNextOS may disable secure workloads/attestation when development mode is active.

There is no hidden “if signature fails, continue” switch.

## 9. Threat model

| Threat | Mitigation |
|---|---|
| malicious CXL device | bounded config/mailbox parsers, no bus mastering, signed manifest, hashes, timeouts |
| forged BootVolume/HBLR | BootPolicy target + signed manifest binding; locator untrusted |
| tampered manifest | signature verification |
| payload tamper | stored/final component hashes |
| downgrade/replay | protected rollback floor + protected selected image state |
| stale image replica | generation floor and confirmed/trial ImageId matching |
| device substitution | physical ID only evidence; optional required selector for appliance mode; trust still manifest-based |
| duplicate BootVolume ID | signed ReplicaId/ImageId conflict rules; ambiguous split-brain fails closed |
| malicious Stage-1 | must itself be signed/hashed; Stage-1 is inside TCB after verification |
| early DMA | bus mastering off; optional bounce/IOMMU hardware profile |
| temporary mapping abuse | reserved aperture, source read-only, not OS usable RAM, teardown after fresh admission |
| firmware TOCTOU | execute verified RAM copy; carry verified manifest digest; component final hashes |
| BootInfo tampering | construct in reserved RAM, checksum/hash, mark read-only where possible, kernel validates bounds/hash |
| protected-state rollback | sequence/monotonic secure storage; never trust CXL state for floors |

## 10. Measured boot versus verified boot

v1 requires verified boot. Measurement log is optional but recommended:

```text
ROM version/profile measurement
BootPolicy digest
BootTrustStore generation
manifest digest
Stage1 loaded hash
kernel/services loaded hashes
SecurityState flags
```

A future TPM/attestation backend can consume the same records. Measurements are evidence, not authorization by themselves.

## 11. BootInfo integrity

BootInfo has header CRC for structural corruption and a SHA-384 digest over the immutable block. If the platform has a measured-boot register/TPM, Stage-1 MAY extend the digest. Kernel must still bounds-check all entries; verified firmware does not justify unsafe parser assumptions.

## 12. CXL link security / IDE

CXL IDE/security state is valuable on real hardware but not universally available and QEMU does not prove it. BootPolicy can require `AuthenticatedLinkEvidence` in a future hardware profile. It is **additional transport security**, not a replacement for signed images.

## 13. Confidentiality

This roadmap provides integrity/authenticity, not encrypted-at-rest CXL boot media. If image confidentiality is required, add signed encrypted-component descriptors with a hardware-bound decryption key. It is explicitly outside v1 because it changes key management and recovery.

## 14. Recovery key model

Local recovery image should be signed by a key authorized for `Recovery` role. It may be separate from normal image signing keys. Production policy should keep at least one recovery path valid even after normal key rotation.
