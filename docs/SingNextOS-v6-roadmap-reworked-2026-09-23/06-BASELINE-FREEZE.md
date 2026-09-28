# Iteration 0 — Live Baseline Freeze

## BASELINE_STATUS

`DRIFT_NON_MATERIAL`

The v6 documents originally recorded SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb` and HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`. Current SingNextOS master is `690913e3500956aeb18d9823b35e9e64d40e42d7`. The compare from the recorded SingNextOS baseline to current master contains one commit whose changed files are the newly added `docs/SingNextOS-v6-architectural-roadmap/*` package only. Runtime/source behavior is therefore unchanged by this one-commit drift. The exact qualification tuple must still be refreshed to `690913e3500956aeb18d9823b35e9e64d40e42d7`.

## Frozen tuple

| Item | Frozen value | Claim role |
|---|---|---|
| SingNextOS source | `690913e3500956aeb18d9823b35e9e64d40e42d7` | exact source identity |
| HybridCPU-v2 source | `794c4a53494f503855ac8cf209efab23fde083b2` | exact source identity |
| SingNextOS SDK | `11.0.100-rc.1.26425.128` | build/toolchain identity |
| SingNextOS runtime | `11.0.0-rc.1.26425.128` | runtime identity |
| SingNextOS target framework | `net11.0` | ABI/runtime contour |
| Roslyn | `5.11.0-1.26425.128`, commit `3551975be08744f0418857c5bed8ab1545c5dd47` | compiler identity |
| language version | `13.0` | source semantics |
| NativeAOT compiler | `11.0.0-rc.1.26425.128` | AOT contour |
| `HybridCPU.ExternalRuntime.Contracts` | `1.14.0` | external contract ABI |
| `HybridCPU.ExternalRuntime` | `1.3.0` | executable adapter/runtime |
| HybridCPU ISE target framework | `net11.0` | runtime contour |
| HybridCPU repo SDK | `10.0.201` | repository build pin |
| HybridCPU compiler/runtime contract | `v6` | compiled-program compatibility |

## Current feature-gate posture

All v6 gates are treated as **OFF at baseline**. Existing V1/staged paths remain the only qualified fallback. A gate must be enabled per exact contour; there is no global "v6 on" switch.

## Qualification pin rule

Every stronger claim must bind at minimum:

```text
SingNextOS SHA
HybridCPU-v2 SHA
package IDs + exact versions + package digests
compiler/runtime contract version
compiler/toolchain digest
semantic schema version/digest
provider adapter identity + provider generation
relevant firmware/device/IOMMU/CXL profile when physical claims are made
feature-gate set
qualification artifact digest
```

A changed tuple is not automatically invalid, but a relied-on semantic change requires requalification.

## Verdict

- Source drift: `DRIFT_NON_MATERIAL`.
- Artifact identity drift: real; pins are refreshed.
- Runtime behavior drift caused by the roadmap commit: none observed from the compare inventory.
- Roadmap stale enough to stop work: **no**.
- Roadmap allowed to reuse the old SHA in new evidence: **no**.
