# Corrected Migration Map — `tools/HybridCpu_ExecutableAdapter/Boot/`

The adapter models are **oracles/test assets**, not production code waiting for a namespace change. Production logic is rewritten or split behind explicit interfaces. Existing models remain until differential proof and replacement tests exist.

| File | CURRENT RESPONSIBILITY | CURRENT DEPENDENCIES / VALUE | TARGET PROJECT | ACTION |
|---|---|---|---|---|
| `AbRecoveryModel.cs` | A/B recovery oracle | deterministic slot/trial/fallback semantics | `SingNext.Boot.Core` production state machine; model retained in tooling | `RewriteBehindInterface` |
| `BootBackendCapabilityProfile.cs` | model/profile of backend capabilities | useful capability taxonomy, but mixes requirements and environment facts | Core policy requirements + platform capability evidence | `Split` |
| `CxlBootSelectionModel.cs` | BootVolume/candidate-selection oracle | ordering, ambiguity, duplicate handling | `SingNext.Boot.Core` selection logic | `RewriteBehindInterface` |
| `PciCxlTransportModel.cs` | modeled PCI/CXL transport/fault behavior | useful protocol/fault oracle, not production transport | `SingPlus.Platform.HybridCpu.Boot` | `RewriteBehindInterface` |
| `ResetAndMemoryMapModel.cs` | modeled reset and memory-map lifecycle | contains two semantic domains that must not share authority | Core reset/mapping states + platform operations | `Split` |
| `Stage0RecoveryModel.cs` | early recovery oracle | recovery precedence/fail-closed semantics | retained oracle; production policy in Core | `KeepAsOracle` |
| `Stage1LoadModel.cs` | verified-load/copy model | range/hash/copy semantics | `SingNext.Boot.Core` verified loader | `RewriteBehindInterface` |
| `TemporaryApertureModel.cs` | aperture lifecycle oracle | transactional/compensation semantics | Core mapping state machine + platform HDM executor | `Split` |
| `TrustAndProtectedStateModel.cs` | rollback/protected-state oracle | trust, monotonic floor, torn-state semantics | Core durable policy + `IBootProtectedState` | `Split` |

## Rules for all other current files in `Boot/`

`P15-00` must enumerate every file present at implementation time and add it to this table before changing/removing it.

Classification rules:

- pure deterministic parser/range/state logic may be reimplemented in Core and compared differentially;
- fake transport/state containers remain tooling;
- model identity objects never become runtime authority objects;
- synthetic generations in tests never become production generation sources;
- permissive defaults must be replaced with explicit fail-closed production policy.

## Retirement rule

A model may move to `RetireAfterDifferentialProof` only after:

1. production equivalent exists;
2. golden vectors run against both paths;
3. fault permutation/differential suite passes for supported scope;
4. replacement tests cover security-relevant transitions;
5. no roadmap/qualification lane still depends on the model as an oracle.

No earlier PR may delete the oracle merely because production code compiles.
