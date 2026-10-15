# P09 evidence — HybridCPU provider-neutral integration

## Disposition

- Baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Phase-entry/qualification HEAD: `8c3f55e47555b2db99356b861ee404211d072edc`.
- Claim: `ExecutableAdapter` only for the existing ordinary, staged, provider-neutral ExternalOperation semantic contour on the local host/JIT model.
- `FG-VNX-HCPU-RESOURCE-CONTRACT` and `FG-VNX-HCPU-USAGE-EVIDENCE`: **OFF**. Package 1.14.0 contains neither a resource envelope nor provider usage evidence, and the ordinary adapter is not a substitute for P04/P07 resource admission and settlement.
- Existing P05–P08 changes and user-owned P14/historical dirty state were preserved.

## Exact local artifact and source boundary

The repository-local `.packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg` and the NuGet-cache copy have SHA-256 `b96e99bda066ee585b26a11cbfa7b68ce6bf44fc0006679483ccc1a4eeb678c2`. `SingPlus.Runtime.csproj` requests exact `[1.14.0]`; `packages.lock.json` resolves `1.14.0` with content hash `wLH8suR5xnj9C0CmqKTvUBSp2fLQsnS18ELZbILGosnGU3KIRJTZykX6bfvL/IWNOFOzGmQVvKI3q7irmjeM/Q==`. The cached compiled contract DLL SHA-256 is `bf490de31f8347fa6d145e43adbb12335d81442022a2dc5855d5c872dda9165b`.

The package README explicitly says its source worktree is unavailable and no source SHA is asserted. Consequently roadmap reference SHA `794c4a53494f503855ac8cf209efab23fde083b2` is **not locally verified** and is not used as executable evidence. No remote lookup was performed.

## Owner and behavior chain

`HybridCpuExternalOperationProvider` maps only semantic effect, visibility, cancellation, replay class and opaque exact correlation into live SingNext `ExternalOperationAuthority` operations. Region use, provider generation, completion, visibility, publication and release are independently checked. Provider receipts remain evidence: they do not mint effect permission, resource grants, leases, Region ownership or publication authority. P08 keeps CPU/runtime legality independent; the provider cannot override a failed local gate.

Existing executable coverage proves exact admission/submission/completion/visibility/publication/release ordering, duplicate and cross-operation rejection, generation reconfiguration drift, receipt replay rejection, provider-loss ambiguity, cancellation, callback re-entry containment and ABI negative space. P08 coverage supplies the independent effect/resource/budget/Region/provider/CPU truth table; it is not transferred into the ordinary HybridCPU adapter claim.

## Defect and remediation

The executable-adapter project and lock file already referenced Contracts 1.14.0, but `HybridCpuExternalRuntimePrerequisite` and its hash regression still named 1.3.0 and the old artifact digest. The prerequisite now records exact 1.14.0 and SHA-256 `B96E...78C2`; the regression hashes the repository-local 1.14.0 nupkg. The separately pinned ExternalRuntime implementation remains 1.3.0 and was not changed.

Applicable invariants reviewed: VNX-002, VNX-007 through VNX-012, VNX-014, VNX-016 through VNX-019, VNX-021 through VNX-024, VNX-027 and VNX-028.

## Commands and actual results

```text
dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~ExternalChildContractQualificationTests" --verbosity minimal
  Passed 5, Failed 0, Skipped 0

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~HybridCpuExternalOperationProviderTests|FullyQualifiedName~HybridCpuCxlDeploymentConformanceTests|FullyQualifiedName~VNextPhase08ComputePlanIndependentGatesTests" --verbosity minimal
  Passed 15, Failed 0, Skipped 0

dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  0 warnings, 0 errors

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Passed 1636, Failed 9, Skipped 2
```

The same unrelated nine failures remain: seven missing historical SingCap/HybridBoot artifacts, one stale user-owned P14 tuple, and one security-profile project-list drift.

## FutureGated and exclusions

The resource-contract and usage-evidence gates remain FutureGated. Owner: a future additive provider-neutral ExternalRuntime contract plus SingNext adapter binding to P04/P07. Missing prerequisite: an exact semantic resource request/usage-evidence schema, exact provider-generation correlation, executable resource lease/settlement integration, and locally attributable source/artifact qualification. Bypassing this would allow an ordinary provider receipt to impersonate resource authority or settle an unbound lease.

Ordinary SIP and ordinary provider paths remain intact. No compiler plan, cache, receipt, telemetry or manifest became authoritative. No HybridCPU ISA, VLIW, opcode, lane, register, retire, scheduler, pipeline, memory-controller or microarchitecture change was made. No NativeAOT, hardware, QEMU, firmware, CXL boot, guaranteed capacity, upper-bound, production or real-HybridCPU claim is made.
