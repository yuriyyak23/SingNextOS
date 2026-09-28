# R0 Implementation Evidence — Boot Contract Freeze

## Baseline and worktree preservation

- Local baseline HEAD: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`.
- The pre-existing dirty worktree was recorded with `git status --short` before edits. Existing SingCap/runtime/generator/test changes and untracked independent-audit documents were preserved; no reset, checkout, clean, commit, push, deletion, or remote Git operation was used.
- The requested local prerequisite `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` is absent from this worktree. The available adapter `README.md`, `TODO.md`, project boundary tests, and all roadmap documents were audited instead. This absence does not authorize a wider dependency edge.

## Phase classification

| Requirement | Classification | Result |
|---|---|---|
| Versioned dedicated boot contracts and codecs | `ExecutableInScope` | Implemented in `HybridCpu.Boot.Contracts` under the executable-adapter boundary. |
| Golden vectors, malformed corpus, checked ranges, optional/required extensions | `ExecutableInScope` | Runnable xUnit coverage added. |
| Actual reset PC/register/pipeline state | `FutureGatedRequiresCore` | Not changed; owner is HybridCPU core/reset subsystem. Missing prerequisite is an approved core reset hook and executable backend. |
| ROM/Stage-0 execution, OTP/NVRAM, HDM behavior | `FutureGatedRequiresCore` / `FutureGatedRequiresHardware` | No execution or hardware claim in R0. |

## Audited boundaries

Audited roadmap `00` through `26`, existing `HybridCpu_ExecutableAdapter` project/README/TODO, neutral runtime contracts/authority separation, `CxlAuthorityBridge`, `CxlType3PlacementPlanner`, repository architecture policy, and current SingCap security profile inventory. The new contract assembly has no HybridCPU core, SingNext runtime, Region, SIP, capability, provider, or hardware dependency.

## Implemented contract surface

- `ResetReason`, `HybridBootPolicyV1`, `BootTargetV1`, `BootVolumeHeaderV1`, `SingNextBootManifestV1`, `HybridBootInfoV1`, `BootSecurityStatus`, `BootEvidenceRecord`, and `TemporaryApertureDescriptor`.
- Explicit v1 parser limits for policy targets, manifest/components/TLVs, BootInfo records, evidence payloads, and the 4 KiB volume header.
- Little-endian fixed-width integer codec and RFC-4122/network-order UUID fields.
- CRC32C and SHA-384 structural sealing, explicit hash/signature algorithm registries, checked range/alignment helpers, and static header-size constants.
- Unknown optional TLVs/records are skippable; unknown required features/records, duplicate required fields, nonzero reserved fields, overflow, truncation, malformed lengths, and unsupported algorithms fail closed with typed `BootParseFailure`.

The wire codecs do not use C# object serialization or in-memory struct layout as ABI.

## Identity, authority, and generation split

The contract DTOs carry boot identity/evidence only. They do not reference `OwnedRegion`, `RegionUse`, provider leases, RegionAuthority, SIP, live device handles, or provider-private runtime objects. `BootVolumeId`, image generation, firmware mapping generation, and future provider/region generations remain separately named domains; no numeric equivalence rule exists.

## Changed files/projects

- New `tools/HybridCpu_ExecutableAdapter/Boot.Contracts/HybridCpu.Boot.Contracts.csproj` and four contract/codec source files.
- New `tools/HybridCpu_ExecutableAdapter.Tests/BootContractV1Tests.cs`.
- Project wiring in `SingNextOS.slnx`, adapter/test project references, architecture classification, and SingCap security-profile inventory.
- Restore updated the existing adapter `packages.lock.json` for the new project graph; no remote repository source was required.

## Qualification

Commands and observed results:

1. `dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter FullyQualifiedName~BootContractV1Tests --logger "console;verbosity=minimal"` — passed 10, failed 0.
2. `dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --logger "console;verbosity=minimal"` — passed 22, failed 0.
3. `dotnet build tools/HybridCpu_ExecutableAdapter/Boot.Contracts/HybridCpu.Boot.Contracts.csproj --no-restore` — succeeded, 0 warnings, 0 errors.
4. Initial `dotnet build SingNextOS.slnx --no-restore` — succeeded, 0 warnings, 0 errors.
5. Initial solution test exposed two inventory/policy failures caused by the new project: missing security-profile ownership and an unclassified contract edge. Both were corrected in the authoritative inventories/policy.
6. Focused architecture/toolchain rerun — passed 21, failed 0.
7. Final `dotnet build SingNextOS.slnx --no-restore --verbosity minimal` — succeeded, 0 warnings, 0 errors.
8. Final `dotnet test SingNextOS.slnx --no-build --no-restore --logger "console;verbosity=minimal"` — adapter 22/0, neutral runtime 58/0, platform 60/0, main 1200 passed/0 failed/2 intentionally skipped.
9. `git diff --check` — exit 0; only existing line-ending notices were emitted.

## Claim and exclusions

Claim level: `ContractOnly` (the project security inventory remains conservatively `ModelOnly`). No claim is made for reset execution, ROM fetch, signature security strength, protected storage, CXL transport, HDM mapping, QEMU, or hardware.

No HybridCPU core/ISE/ISA/opcode/compiler/scheduler/pipeline/replay/memory-controller/retire/register implementation file was modified.
