# R10 Implementation Evidence — Multi-Device, Replica, Fabric Semantics

Baseline HEAD `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty worktree preserved. R4 selector, R8 importer, host Type-3 provider, and authority bridge were audited.

Extended deterministic tests for two-plus candidates, exact duplicate replica idempotence, same volume with different generations, highest eligible generation, physical replacement, MLD/endpoint renumbering, fabric path changes, order permutations, optional DSN preference, required physical pin, and same-generation conflicting image/digest ambiguity. Selection remains based on signed logical `BootVolumeId`/`ReplicaId`/`ImageId` and rollback policy, never enumeration order, BDF, DSN, port, route, decoder, HPA, DPA, MLD, or fabric identity.

R8 regressions prove each handoff produces a fresh OS admission sequence and current provider device generation. These domains are not compared to firmware mapping or image generations. Actual `OwnedRegion`, `RegionUse`, device leases, fabric bindings, and their generations remain owned by existing SingNext authorities and provider bridges.

Boot striping/interleave, fabric-manager authority dependency, and provider-private route authority are excluded. Classification: selector behavior `ModelValidated`; local OS importer/provider boundary remains `AdapterQualified`.

Qualification: focused selector 8/0; related R8/Type-3 regressions 21/0; solution build 0 warnings/errors. First full run had one unrelated known-flaky GUI ownership assertion; exact retry passed 1/0 and the subsequent complete run passed adapter 68/0, neutral 58/0, platform 60/0, main 1205 passed/0 failed/2 skipped. No test was weakened. Changed: `CxlBootSelectionModelTests.cs`, this evidence and traceability matrix. No HybridCPU core/ISE/ISA/compiler/architecture implementation changed.

