# Code verification and English Whitebook revision log

**Reviewed baseline:** `yuriyyak23/SingNextOS` `master` commit `4cbc8e36a54da7cf193e13e7c18d773b36da2c04`, inspected on 18 October 2026. Documentation-only change; no source-code mutation. Every positive correction below has an exact current source anchor.

## Verified corrections

| ID | Corrected assertion | Source-level evidence |
| --- | --- | --- |
| C01 | The current `master` no longer contains `docs/whitebook`; this PR restores an English edition | Pinned Git tree + parent commit `5fcd7bae3d6c86ce8279e865c158dbf8f5d8f363` contained `docs/whitebook/SingNextOS-architecture/` |
| C02 | SDK is `11.0.100-rc.2.26504.105`, roll-forward disabled | [global.json](../../../global.json) |
| C03 | Runtime targets `net11.0` and references `HybridCPU.ExternalRuntime.Contracts` `[1.36.4]` | [Runtime.csproj](../../../src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj) |
| C04 | C# 13 is the default; preview is an explicit property | [Directory.Build.props](../../../Directory.Build.props) |
| C05 | Runtime owns separate registries/authorities, not one all-purpose ledger | [RuntimeKernel.cs](../../../src/Runtime/SingPlus.Runtime/RuntimeKernel.cs) |
| C06 | `DestroyAsync` is session correlated | [RuntimeVirtualizationServiceHost.cs](../../../src/Runtime/SingPlus.Runtime/Virtualization/RuntimeVirtualizationServiceHost.cs) |
| C07 | `DestroySessionVirtualDomain` binds an invocation before dependent drain | [RuntimeKernel.Virtualization.cs](../../../src/Runtime/SingPlus.Runtime/Virtualization/RuntimeKernel.Virtualization.cs) |
| C08 | Host original-model revoke is one-original/canonical-raw checked | [HostPlatformAuthorityProvider.Virtualization.cs](../../../src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.Virtualization.cs), [bridge path](../../../src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.OriginalModelRevokes.cs) |
| C09 | `V6FeatureGates.IsEnabled` evaluates to false for recognized names | [V6FeatureGates.cs](../../../src/Runtime/SingPlus.Runtime/V6/V6FeatureGates.cs) |
| C10 | `VNextFeatureGates.IsEnabled` evaluates to false for recognized names | [VNextFeatureGates.cs](../../../src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs) |
| C11 | Budget recovery replays authenticated history and stages conservative charge | [ResourceBudgetRecoveryJournal.cs](../../../src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetRecoveryJournal.cs), [RuntimeKernel.cs](../../../src/Runtime/SingPlus.Runtime/RuntimeKernel.cs) |
| C12 | File/network SIP interfaces exist, but interfaces alone cannot prove full service implementations | [IFileService.cs](../../../src/Sip/SingPlus.Sip/FileSystem/IFileService.cs), [INetworkService.cs](../../../src/Sip/SingPlus.Sip/Networking/INetworkService.cs) |
| C13 | Kernel boot entry staged overload labels readiness as `host-debug` | [KernelEntryPoint.cs](../../../src/Kernel/SingPlus.Kernel/Boot/KernelEntryPoint.cs) |
| C14 | Static admission has exactly one project compilation owner linking five specific tool files | [StaticAdmission.csproj](../../../src/Admission/SingPlus.StaticAdmission/SingPlus.StaticAdmission.csproj) |
| C15 | Remote lease journaled submit describes itself as a one-shot managed seam dependent on supplied store | [V6ManagedRemoteLeaseJournaledSubmit.cs](../../../src/Runtime/SingPlus.Runtime/V6/V6ManagedRemoteLeaseJournaledSubmit.cs) |

## Inventory audit

The exact pinned tree was enumerated to generate [the project catalog](08-PROJECT-CATALOG.md) and [the C# atlas](09-SOURCE-ATLAS.md). Entries are created from actual repository paths and link directly to their source. Counts must be regenerated when the tree changes.

## Deliberately removed unsupported statements

- The earlier Whitebook's 15 October focused/main passing-test numbers are **not current measurements**; its report path is absent from present `master`.
- The earlier note that `DestroyAsync` lacks session invocation correlation is contradicted by live source.
- The older `rc.1` SDK and external contracts `1.35.0` tuple is stale.
- Historical development policy, normative roadmap and evidence-index relative links pointed at directories absent from the pinned current tree. This edition does not present those missing paths as valid references.
- A class, model, interface or passing test is not promoted into physical, security, time-bounded or production qualification without exact external evidence.

## Reviewer checklist

- [ ] Confirm baseline ref is still suitable and compare any subsequent source changes.
- [ ] Check Markdown relative links against the proposed tree and current source tree.
- [ ] Verify architecture claims at cited methods (not just filenames).
- [ ] Run relevant tests only if desired for code changes; this PR is documentation only.
- [ ] Keep `ProductionQualified`, secure physical memory isolation and cold rollback-safe recovery explicitly unclaimed until independently evidenced.
