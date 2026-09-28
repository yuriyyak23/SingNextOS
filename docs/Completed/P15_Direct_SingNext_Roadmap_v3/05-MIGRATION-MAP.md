# Corrected Migration Map — `tools/HybridCpu_ExecutableAdapter/Boot/`

The current adapter files are specification/oracle assets unless direct executable ownership is proven. No `*Model` file becomes production merely by moving namespace/project.

| File | Current responsibility | Current value/dependencies | Target | Action |
|---|---|---|---|---|
| `AbRecoveryModel.cs` | deterministic A/B/trial/fallback oracle | model state + fault injection | Boot.Core state machine; model retained | `RewriteBehindInterface` |
| `BootBackendCapabilityProfile.cs` | model environment/capability description | mixes required behaviors with modeled availability | Core requirements + platform evidence descriptor | `Split` |
| `CxlBootSelectionModel.cs` | candidate/BootVolume selection oracle | duplicate/conflict/permutation semantics | Boot.Core selector | `RewriteBehindInterface` |
| `PciCxlTransportModel.cs` | modeled PCI/CXL protocol/fault transport | simulator behavior, not hardware transport | BootPlatformAdapter | `RewriteBehindInterface` |
| `ResetAndMemoryMapModel.cs` | reset + mapping lifecycle oracle | two distinct semantic domains | Core mapping state + platform reset operations | `Split` |
| `Stage0RecoveryModel.cs` | local recovery oracle | bounded recovery/failure precedence | Core recovery policy | `KeepAsOracle` + rewrite production policy |
| `Stage1LoadModel.cs` | verified copy/load/entry oracle | range/hash/publication semantics | Boot.Core verified loader | `RewriteBehindInterface` |
| `TemporaryApertureModel.cs` | transactional HDM aperture oracle | commit/readback/reverse compensation/reset staleness | Core mapping FSM + platform executor | `Split` |
| `TrustAndProtectedStateModel.cs` | trust/rollback/protected-state oracle | monotonic floor/torn-state behavior | Core policy + `IBootProtectedState` | `Split` |

## Migration rules

- Existing Boot.Contracts codecs remain contract evidence; they are not migrated into Core merely because models use them.
- Model cryptography or storage fakes are not production protected-store implementations.
- Synthetic model generations are never production generation sources.
- Any code copied from a model must first be classified as pure deterministic logic and covered by differential vectors.
- Production code must not reference the ExecutableAdapter project.

## Retirement gate

A model can be deleted only after production equivalent + golden/differential proof + replacement negative/fault tests + no remaining qualification dependency. Normal deletion point is P15-15.
