# v6 Qualification and Test Matrix

## Evidence classes

```text
static admission
unit/property
concurrency/race
fault injection
model checking
provider conformance
differential semantic trace
performance
hardware validation
supply-chain/artifact identity
```

## Minimum matrix by phase

| Phase | Static | Property | Race | Fault | Formal/model | Cross-project | Perf |
|---|---:|---:|---:|---:|---:|---:|---:|
| P01 memory | yes | yes | yes | yes | yes | yes | yes |
| P02 durability | yes | yes | yes | yes | yes | yes | yes |
| P03 temporal | yes | yes | yes | yes | yes | yes | yes |
| P04 DMA | yes | yes | yes | yes | yes | yes | yes |
| P05 refinement | yes | yes | yes | yes | primary | primary | n/a |
| P06 IFC | yes | yes | yes | yes | yes | yes | moderate |
| P07 locality | yes | yes | moderate | yes | optional | yes | primary |
| P08 preemption | yes | yes | primary | primary | yes | primary | primary |
| P09 attestation | yes | yes | yes | primary | optional | primary | low |
| P10 RAS | yes | yes | primary | primary | yes | primary | moderate |
| P11 multi-host | yes | yes | primary | primary | primary | primary | primary |
| P12 energy | yes | yes | moderate | yes | optional | yes | primary |
| PCL | primary | primary | yes | yes | yes | primary | yes |

## Cross-cutting adversarial scenarios

- revoke/close/restart between proof/refinement and provider submit;
- provider generation drift after effect but before publication;
- translation invalidation during DMA;
- coherent writer alias race;
- crash between publication and durability confirmation;
- cancel/preempt/resume races;
- attestation refresh/reset race;
- RAS poison during staged/direct output;
- multi-host partition with in-flight effect;
- energy/thermal throttling invalidating a temporal guarantee;
- compiler proof valid structurally but incompatible with current provider guarantee.

## Performance discipline

Performance claims must compare ordinary/staged reference path with the v6 optimized contour. Security checks are never removed solely because they are expensive; optimization must cache immutable facts, fuse transport-only work or add independently enforceable guarantees.
