# CI and Repository Integration

P15 is incomplete unless repository integration is explicit.

## Required integration points

- `SingNextOS.slnx`;
- `Directory.Build.props`;
- `Directory.Build.targets`;
- architecture policy tests;
- dependency/project classification registry;
- SingPlus.Admission / SingCap analyzers;
- security profile registry;
- package lock files;
- test project placement rules;
- HybridCPU qualification scripts and pins;
- generated qualification artifacts;
- README/docs references;
- traceability matrix.

## Proposed additions

Exact locations follow current repository conventions discovered by `P15-00`:

- `eng/qualify-direct-singnext-boot.sh`;
- PowerShell counterpart if required by current CI;
- schema/output for `DirectSingNextBootQualificationV1.json`;
- `GoldenVectors/HybridBootInfoV1/`;
- `GoldenVectors/ProtectedBootStateV1/`;
- model-vs-production differential test project/lane;
- end-to-end HybridCPU ISE Direct Boot lane.

## CI gates

1. **Architecture gate** — forbidden project/package edges fail.
2. **Boot.Contracts surface gate** — only approved wire/ABI surface.
3. **BootCapsule admission gate** — full executable closure admitted.
4. **Differential gate** — retained models and production semantics agree for supported scope.
5. **ABI vector gate** — stable serialization/entry layout.
6. **Pin coherence gate** — HybridCPU pin change requires qualification rerun.
7. **Fault-traceability gate** — every mandatory fault has test coverage.
8. **Claim gate** — artifact cannot claim `HardwareValidated` without hardware evidence record.

## Artifact outputs

Every qualification run must emit machine-readable results under the repository's standard artifact directory. CI should preserve the artifact long enough for audit/release traceability.
