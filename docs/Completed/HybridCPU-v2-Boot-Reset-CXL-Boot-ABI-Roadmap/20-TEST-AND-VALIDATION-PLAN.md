# 20 — Test and Validation Plan

## 1. Test philosophy

The primary correctness property is not merely “kernel entry occurred.” A passing boot MUST also prove that selection did not depend on runtime enumeration order, every executable payload was authenticated according to policy, rollback rules were honored, all firmware-owned mappings remained temporary, and SingNextOS created fresh provider authority after handoff.

Every simulator test uses an explicit seed but deterministic event ordering. Random/property tests print the seed and reduce to a persistent regression vector. Timeouts use a virtual monotonic clock, never wall-clock sleeps.

## 2. Test layers

| Layer | Scope | Expected owner |
|---|---|---|
| Wire-format unit | headers, manifest, policy, BootInfo, CRC/hash bounds | `HybridCPU_Boot.Contracts` |
| Reset unit | register/PC/interrupt/microstate reset | HybridCPU runtime |
| Selector unit | identity/generation/replica/A-B decisions | boot selector |
| Transport unit | PCI capability, mailbox, LSA, HDM model | preboot backend |
| Stage-0 integration | ROM→candidate→manifest→Stage-1 | firmware simulator |
| Stage-1 integration | payload verify/copy→BootInfo→entry | firmware simulator |
| Cross-project | BootInfo→fresh provider admission | HybridCPU + SingNextOS |
| Protocol validation | Type-3/LSA/HDM behavior in QEMU | host-side harness |
| Hardware qualification | reset/power/fault/security/recovery | target platform |

## 3. Mandatory scenario matrix

| ID | Scenario | Injection/setup | Required result |
|---|---|---|---|
| T001 | cold reset → valid CXL boot | one valid target | reset vector→ROM→verified Stage-1/kernel→BootInfo→kernel |
| T002 | no CXL devices | empty discovery | bounded probe; signed local recovery |
| T003 | one invalid + one valid device | bad manifest + valid replica/target | reject bad; boot valid independent of enumeration order |
| T004 | wrong `BootVolumeId` | signed image for other logical target | not selected; continue policy/fallback |
| T005 | duplicate `BootVolumeId` | two valid replicas | deterministic replica resolution; no index-based priority |
| T006 | conflicting duplicate volume | same logical ID/generation but incompatible signed image identity where policy cannot disambiguate | fail target as ambiguous; fallback/recovery |
| T007 | bad volume/header CRC | corrupt canonical header | ignore/recover; never parse unchecked offsets |
| T008 | bad manifest checksum/encoding | corrupt envelope | reject before signature-dependent interpretation |
| T009 | bad signature | tamper signed bytes | reject; record security failure |
| T010 | payload hash mismatch | tamper Stage-1/kernel | reject slot; no execute |
| T011 | unsupported CPU ABI | higher required ABI | reject candidate; fallback |
| T012 | unsupported firmware ABI | incompatible manifest requirement | reject candidate; fallback |
| T013 | rollback image | generation below protected floor | reject even with valid signature |
| T014 | HDM mapping failure | decoder resource/program error | cleanup partial programming; try alternate candidate/recovery |
| T015 | CXL read timeout | timeout metadata/payload | bounded cancel/cleanup; fallback |
| T016 | CXL link loss mid-copy | fail after N reads | destination treated invalid; never execute partial copy |
| T017 | A/B update success | A confirmed; install B trial | boot B; OS confirm; B becomes confirmed; floor may advance |
| T018 | A/B interrupted payload write | power loss mid-B data | A remains confirmed; B ineligible |
| T019 | A/B interrupted manifest write | torn inactive manifest | A remains confirmed; bad B rejected |
| T020 | interrupted protected-state commit | power loss during trial state | old or new complete record selected by atomic store; never hybrid state |
| T021 | fallback A→B | active trial A fails verification/attempt budget; B confirmed | B selected if eligible |
| T022 | fallback to local recovery | all CXL targets fail | signed local recovery starts |
| T023 | device replacement | new physical device, preserved logical volume and valid signed media | allowed unless policy explicitly pins physical evidence |
| T024 | DSN changed, BootVolume preserved | replacement/clone per policy | semantic selection by `BootVolumeId`; DSN only affects optional constraint/evidence |
| T025 | device index/order changed | permutation each reset | identical semantic selection |
| T026 | two identical physical devices | same model/serial absent, distinct replicas | no reliance on identity-by-position; ambiguity handled safely |
| T027 | stale LSA locator | LSA points to old header offset | validate canonical metadata; reject/fallback; LSA never trust root |
| T028 | stale manifest generation | lower than floor / lower than protected confirmed rule | reject stale candidate |
| T029 | SingNextOS re-discovery after handoff | alter BDF/decoder/HPA before provider admission | fresh discovery/admission; firmware evidence marked diagnostic/stale |
| T030 | device disappears at handoff | remove after kernel copy | kernel can run from RAM; provider admission fails cleanly; no stale OwnedRegion |
| T031 | warm reset after prior CXL boot | preserve physical device link but invalidate firmware generation | ROM starts fresh; prior aperture handle unusable |
| T032 | watchdog reset | inject hung OS | reset reason recorded; protected trial attempt policy updated; fresh boot |
| T033 | recovery reset | explicit recovery request | skip normal target according to policy; enter signed local recovery |
| T034 | malicious capability list | self-loop/overflowing ext capability | bounded parser rejection; no hang |
| T035 | mailbox oversized/late reply | adversarial model | reject response; deadline enforced |
| T036 | invalid entry point | signed manifest points outside loaded executable | reject before jump |
| T037 | overlapping payload ranges | signed manifest overlap not explicitly permitted | reject manifest |
| T038 | BootInfo tamper | mutate checksum/length | SingNext early boot rejects BootInfo and uses safe fallback policy |
| T039 | unknown optional manifest TLV | new optional extension | older firmware skips safely |
| T040 | unknown required manifest feature | required feature bit | older firmware rejects candidate |
| T041 | protected boot policy corrupt | both copies invalid | fail closed to immutable recovery policy, not arbitrary CXL scan |
| T042 | rollback floor store corrupt | unreadable/inconsistent | production fails closed to signed recovery/service mode |
| T043 | preferred DSN missing | logical target exists elsewhere | follow policy semantics: required pin rejects; preference-only may continue |
| T044 | fabric route changes | same device/volume through another route | no change to logical boot authority; OS creates new fabric generation |
| T045 | HPA aperture conflicts | RAM/MMIO reservation collision | platform allocator rejects; no decoder programming |
| T046 | persistent capacity too small | header says out-of-range offsets | reject before mapping/read |
| T047 | malicious `BootVolumeId` duplicate | unsigned/forged locator claims target | signed manifest/canonical header checks prevent selection as trusted image |
| T048 | unsigned development image | dev fuse off/on | reject in production; allow only explicit dev policy with security status exposed |
| T049 | recovery image corrupt | local recovery hash/signature fail | immutable monitor exposes diagnostics/halt; no unverified execute |
| T050 | all primary targets timeout | bounded deadlines | finite boot duration; recovery reached |

## 4. Reset conformance matrix

For cold, warm, watchdog and recovery reset, assert: `PC=RESET_VECTOR`; architectural integer state equals [04](04-RESET-ABI.md); interrupts are not delivered before firmware enables them; MMU/translation is disabled in v1; no pre-reset pipeline/replay result can retire; ROM is executable/readable and not writable; `ResetReason` survives into BootInfo; and temporary CXL mapping generation is new.

Warm reset MAY preserve link/device electrical state as a platform optimization, but tests MUST distinguish physical retention from semantic retention. No software handle, boot aperture token, decoder evidence generation or SingNext provider generation survives by implication.

## 5. Identity metamorphic tests

For every “golden boot” topology, generate permutations:

```text
permute enumeration order
renumber BDFs
renumber ports/decoder indices
move device behind another switch route
change HPA aperture base
change optional DSN (when policy does not pin it)
retain BootVolumeId + signed eligible image
```

The selected logical image and security result MUST remain unchanged. Conversely, changing `BootVolumeId`, signature, rollback eligibility or protected BootState MUST be observable.

## 6. Parser/property tests

Fuzz all externally writable formats: HBLR locator, `BootVolumeHeader`, manifest envelope/TLVs, BootInfo ingestion, PCIe extended capability traversal, CXL mailbox payload parsing. Assertions: no unbounded allocation; no integer overflow; no out-of-bounds memory; finite iteration counts; duplicate required fields rejected; unknown required versions rejected; offsets validated against containing object and physical capacity before use.

Useful properties:

```text
decode(encode(x)) == canonical(x)
verify(tamper(signedManifest)) == false
selection(permutation(devices)) == selection(devices)
acceptedGeneration >= rollbackFloor
executedBytesHash == signedDescriptorHash
OSAuthority ∩ FirmwareEvidenceHandles == empty
```

## 7. Deterministic fault schedule

`ModelBootFaultInjector` should address actions by semantic operation ordinal rather than host timing:

```csharp
FaultPlan {
  FailNth("PciConfigRead", 17, Timeout);
  FailNth("GetLsa", 1, MailboxError);
  FailNth("HdmCommit", 2, CommitRejected);
  FailNth("PersistentRead", 43, Poison);
  PowerLossAfter("BootState.AtomicWrite", step: 2);
}
```

A test serializes topology + policy + media image + fault plan + expected result. These fixtures become ABI regression corpus.

## 8. A/B power-loss matrix

Inject power loss after every persistence barrier in the update sequence: write inactive payload; flush; write inactive manifest copy; flush; write second metadata copy; flush; verify read-back; protected trial record prepare; protected trial commit; first trial boot; OS confirmation; rollback-floor advance. At each restart there MUST be at least one eligible known-good or local recovery path.

The rollback floor MUST NOT be advanced merely because a new image was written or attempted. Confirmation is the commit point for making the new generation minimum, subject to policy.

## 9. Authority handoff tests

Cross-project tests MUST instrument all authority creation. Before SingNext provider admission, BootInfo may contain `BootVolumeId`, `ReplicaId`, DSN/BDF/decoder/HPA diagnostics and a temporary aperture descriptor, but calls requiring `OwnedRegion`/region authority MUST be impossible. After fresh discovery/admission, newly created provider generation and region authority MUST not reuse firmware evidence tokens.

A strong negative test deliberately copies numeric values: make a firmware decoder ID equal a future provider object ID and verify type/domain separation prevents accidental authority equivalence.

## 10. QEMU validation matrix

Where supported by the selected QEMU release, run: one Type-3 persistent device; two devices; switch topology; file-backed persistent memory across QEMU restart; separate LSA backing; serial-number presence/absence; host bridge fixed memory window; endpoint HDM decoder; device removed from launch; corrupt LSA and persistent backing. QEMU is used for transport/model validation, not as proof of HybridCPU instruction execution.

## 11. Hardware qualification

At minimum: cold power-on; AC loss during update; warm reset; watchdog reset; local recovery with CXL physically removed; device replacement; BDF/topology change; CXL link reset; mailbox timeout; decoder resource exhaustion; poison/read failure if injection is available; trust-store lock/provisioning; rollback counter persistence; DMA containment before untrusted devices are enabled; firmware upgrade preserving v1 media/BootInfo compatibility.

## 12. Performance gates

Correctness wins over latency. Nevertheless CI/qualification records: number of config reads, mailbox commands, persistent metadata bytes, mapped aperture bytes, payload bytes, per-device timeout and total discovery deadline. Production policy MUST cap device/candidate probes. Stage-0 reads only discovery metadata before choosing a candidate and MUST not scan entire persistent capacities.

Parallel discovery is optional and only acceptable if deterministic semantic selection and bounded resource use are preserved. Initial implementation may enumerate sequentially.

## 13. Release gate

A release cannot claim v1 conformance until T001–T050 applicable to its backend pass, the cross-project authority tests pass, parser fuzz corpus is clean, ROM binary-size budget is met, signed local recovery is verified with every CXL device unavailable, and an older v1 boot-media fixture remains bootable when policy/rollback permits.
