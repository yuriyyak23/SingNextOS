# P15.4 — PR slices and implementation sequence

Each slice is intended to be independently reviewable, buildable, and revertible. No PR may claim a higher evidence class than it actually executes.

## PR P15-00 — Baseline lock and stale-pin reconciliation

**Files**

- `tools/SingPlus.HybridCpuQualification/*`
- `eng/qualify-hybridcpu-aot.*`
- new `docs/P15/.../BASELINE.md`

**Work**

- record current SingNextOS and HybridCPU revisions;
- remove or explicitly version the stale `9e001bf...` assumption;
- update qualification report schema to distinguish `ExternalBlocked`, `ToolchainAvailable`, `ImageProduced`, `IseExecuted`;
- no boot behavior change.

**Exit**: current master-to-master baseline is reproducibly identified; qualification cannot accidentally certify an older HybridCPU revision.

## PR P15-01 — Extract `HybridCpu.Boot.Contracts`

**Work**

- move `Boot.Contracts` to `contracts/HybridCpu.Boot.Contracts`;
- update project references and architecture policy tests;
- no wire-format changes in the move;
- add golden-vector regression ensuring byte identity before/after move.

**Exit**: all existing boot contract tests pass; no executable-adapter dependency is needed by runtime or future capsule.

## PR P15-02 — Introduce `SingNext.Boot.Core`

**Work**

- promote selector, rollback/trust decisions, verified loader and A/B state machine;
- preserve old model tests as differential oracle;
- no platform IO.

**Exit**: production-named Boot Core contains no model/provider/host references; differential tests are green.

## PR P15-03 — Boot Capsule security profile

**Files**

- `Directory.Build.props/targets`
- `eng/singcap-security-profiles-v1.json`
- analyzer/admission tests
- `SingNext.Boot.Capsule.csproj`

**Work**

- introduce `BootCapsule` profile;
- forbid reflection, process, filesystem, arbitrary networking, dynamic loading, thread pool and unbounded ambient services;
- choose `BootCapsuleNoHeap` or bounded-boot-heap policy.

**Exit**: a trivial capsule entrypoint builds only if it satisfies the profile; negative fixtures prove forbidden APIs fail the build/admission gate.

## PR P15-04 — HybridCPU executable artifact bridge

**Cross-repo**

- integrate current HybridCPU managed compiler/bootstrap image path;
- produce a deterministic HybridCPU executable image from `SingNext.Boot.Capsule` and `SingPlus.Kernel` candidates;
- update `SingPlus.HybridCpuQualification`.

**Exit**: `ManagedAssemblyToHybridCpuAot` is no longer `ExternalBlocked` for the explicit qualification profile; image digest is deterministic across two clean builds.

## PR P15-05 — `HybridPlatformDescriptorV1` and boot entry ABI

**Work**

- add fixed-width descriptor and parser/validator;
- add capsule entry ABI and kernel entry ABI;
- add ISE injection source for descriptor;
- no CXL yet.

**Exit**: ISE can enter capsule, validate descriptor, emit deterministic debug evidence, and enter a trivial kernel image with a minimal BootInfo.

## PR P15-06 — `SingPlus.Platform.HybridCpu.Boot` skeleton

**Work**

- create project;
- implement boot clock/reset/debug/local-image services for ISE/model profile;
- define narrow PCI/CXL/HDM/protected-state interfaces;
- reject missing production capabilities fail-closed.

**Exit**: capsule composition uses only these interfaces; host-debug profile remains separate.

## PR P15-07 — Executable PCI/CXL discovery

**Work**

- bounded PCI enumeration;
- CXL capability parsing;
- Type-3 classification;
- optional locator hint;
- canonical BootVolume header scan fallback;
- no HDM mapping yet beyond backend test fixture.

**Exit**: malformed capability chains, duplicates, timeout and unsupported locator are deterministic and fail closed; endpoint enumeration order does not affect semantic selection.

## PR P15-08 — Executable temporary HDM aperture

**Work**

- program single-target decoder path;
- reverse compensation;
- mapping generation;
- readback/commit validation;
- explicit `DestroyOrQuarantine`;
- retain no-interleave baseline.

**Exit**: ISE test demonstrates map/read/remap/destroy, injected partial-commit failure and reset-staleness.

## PR P15-09 — Verified CXL load + BootInfo

**Work**

- stream component bytes from persistent capacity;
- copy to system RAM;
- hash destination;
- validate entry;
- build immutable BootInfo;
- jump kernel.

**Exit**: link loss after completed copy does not modify loaded bytes; link loss before verification publishes no executable component.

## PR P15-10 — Kernel early boot importer wiring

**Work**

- real kernel entrypoint accepts `KernelEntryAbiV1`;
- validate/copy BootInfo;
- instantiate `HybridBootInfoImporter`;
- production `IFreshCxlBootDiscovery` for ISE/HybridCPU provider;
- production aperture retirement.

**Exit**: kernel refuses to derive authority from BootInfo and succeeds only through fresh provider admission.

## PR P15-11 — Protected state, A/B and anti-rollback

**Work**

- production/profile-specific protected store interface;
- capsule A/B and OS A/B remain separate generation domains;
- trial nonce/attempts;
- `ConfirmBoot` authenticated kernel control path;
- floor advances only at confirmation.

**Exit**: power loss at every durable barrier preserves either old confirmed route or a valid recovery route.

## PR P15-12 — Local recovery and boot-failure matrix

**Work**

- signed local recovery capsule/image;
- watchdog and trial-failure reset reason;
- recovery ordering: trial -> confirmed -> replica -> local recovery -> immutable monitor/halt.

**Exit**: failure of all CXL paths still boots local signed recovery without a CXL dependency.

## PR P15-13 — End-to-end ISE qualification

**Scenario**

```text
reset surrogate -> ROM model -> capsule image -> Type-3 boot volume -> kernel -> fresh CXL provider -> RegionAuthority
```

**Exit**: one deterministic command produces evidence for the complete path, including negative matrix.

## PR P15-14 — QEMU companion lane (optional but recommended)

Validate protocol-visible PCI/CXL behavior without promoting it to silicon evidence.

## PR P15-15 — Real hardware backend

Implement and independently qualify ROM/reset, PCI/CXL, HDM, protected store, DMA/IOMMU and recovery media. This is the first slice eligible to claim `HardwareValidated` for the exact platform profile.
