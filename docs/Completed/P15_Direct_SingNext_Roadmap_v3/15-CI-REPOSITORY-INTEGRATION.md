# CI and Repository Integration

## Existing integration points that must be extended

- `SingNextOS.slnx`;
- `Directory.Build.props` / `Directory.Build.targets`;
- `tests/SingPlus.Tests/Architecture/RepositoryArchitecturePolicyTests.cs`;
- `eng/singcap-security-profiles-v1.json`;
- `tools/SingPlus.Admission` and analyzers;
- package lock files;
- `tools/SingPlus.HybridCpuQualification`;
- `eng/qualify-hybridcpu-aot.sh` and platform variants;
- `PlatformExternalGateTable` where existing requirements cover P15 behaviors.

## Required additions

- `eng/qualify-direct-singnext-boot.sh` and repository-standard PowerShell counterpart;
- `artifacts/direct-singnext-boot/DirectSingNextBootBaselineV2.json`;
- `artifacts/direct-singnext-boot/DirectSingNextBootQualificationV1.json`;
- exact V1 BootInfo golden vectors shared between existing codec and capsule writer;
- protected-state golden/torn-write vectors;
- model-vs-production differential suite;
- ISE end-to-end lane;
- hardware evidence schema/lane without default enablement.

## CI gates

1. architecture DAG/forbidden refs;
2. Boot.Contracts public-surface/ABI vectors;
3. capsule dependency/admission closure;
4. selected memory-profile gate;
5. differential model gate;
6. mandatory fault traceability;
7. external pin coherence — any SHA change invalidates prior qualification;
8. existing `PlatformExternalGateTable` mapping coherence;
9. claim promotion gate.

No CI job may update an external SHA constant and call the old artifact valid.
