# Audit Findings Resolution Map

This file records how the completed audit changed the implementation roadmap and where earlier audit overreach is corrected.

## Resolved roadmap defects

| Finding/theme | Required roadmap correction |
|---|---|
| repository drift / stale baselines | `P15-00` revalidates current master and external pins; SHAs are metadata, not permanent baseline |
| wrong dependency direction | Capsule is composition root: `Capsule -> Core + Platform`; Platform never references Capsule |
| wire contracts mixed with service interfaces | Boot.Contracts is wire-only; in-process boot ports live in Core or reuse existing abstractions |
| model code treated as promotion candidate | production logic is rewritten/split; models remain oracle until differential proof |
| BootInfo authority risk | BootInfo is evidence-only; kernel owns/copies it; runtime mints fresh authority |
| HDM mapping treated as ownership risk | mapping transaction and Region ownership are separate; no `OwnedRegion` before runtime admission |
| fresh discovery not explicit enough | liveness/generation revalidation and new provider generation are mandatory |
| aperture retirement owner unclear | explicit retirement interface only if no authoritative owner exists; result is Released/Stale/Quarantined |
| architectural reset mixed with runtime backend reset | `11-RESET-SEMANTICS.md` separates domains and generation invalidation |
| protected-state A/B incomplete | separate capsule/image domains, trial nonce, attempts, confirmation, rollback floor, torn-write/split-brain semantics |
| NativeAOT overclaimed | AOT is evidence/attack-surface reduction only, not authority/isolation |
| HybridCPU changes hidden in plan | all missing external behaviors are explicit gates; no HybridCPU source PR in P15 |
| hardware claim inflation | no `HardwareValidated` without direct named-platform evidence |
| qualification gaps | mandatory fault matrix and claim gates added |
| premature model deletion | deletion deferred to `P15-15` after production equivalent + differential proof |

## Corrections to prior audit assertions

The updated roadmap explicitly avoids repeating insufficiently grounded negative claims:

1. **`RegionAuthority` / `OwnedRegion` / `RegionUse`** — do not assume absent. `P15-00` must locate current definitions/consumers and P15 must reuse the authoritative owner.
2. **Reset epochs** — do not assume absent. Reuse `ObservePlatformBackendReset()` / `BackendEpoch` semantics if present, or the current equivalent.
3. **`SingPlus.Boot` MMIO/DMA behavior** — do not claim it without source-level proof. Inventory actual responsibilities first.
4. **HybridCPU BootBlob / bootstrap / static linker** — absence is not assumed. Current external source/contracts are verified and gates are behavioral.
5. **Invented external API names/versions** — removed from the plan.
6. **Boot service ports in Boot.Contracts** — corrected; Boot.Contracts is wire-only.
7. **`PromoteAndRename` for `*Model`** — rejected as default; rewrite/split behind interfaces.

## Implementation-phase GO criteria

`GO for roadmap-driven SingNextOS refactor` is justified only after:

- `P15-00` completes with no unowned authority responsibility;
- `P15-01` policy/DAG gates pass;
- all external blockers are explicit;
- no hidden dependency on later slices exists.

`GO for ISE Direct SingNext implementation` additionally requires required HybridCPU image/entry/bootstrap/reset/ISE-CXL gates to be satisfied.

`GO for real hardware Direct SingNext` additionally requires the hardware CXL/reset/DMA/order gates and direct `P15-14` evidence.
