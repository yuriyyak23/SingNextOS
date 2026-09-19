# P13 TCB and Supply-Chain Review

## TCB classification

The authoritative inventory is `eng/singcap-security-profiles-v1.json`. TrustedRuntime comprises the contracts, kernel/runtime/SIP SDKs, drivers, boot/kernel/HAL, platform abstractions, runtime and SIP assemblies listed there. These execute in the same managed process/model trust boundary and are not isolated from one another. Their unavoidable TCB includes CLR/.NET runtime behavior, generated sentries, authority registries, host HAL interop and any platform-provider interop reached by the trusted runtime.

There are **no `NativeIsolated` projects**. Consequently no same-address-space native or provider assembly receives an isolation claim. `PlatformExternal` means an external/provider boundary classification, not proof of a separate process, VM or protection domain.

Unsafe/native surface remains limited to the pre-existing TrustedRuntime/HAL/platform integration inventory. This phase adds no unsafe block, P/Invoke, native library or architectural dependency.

## Exact toolchain tuple

- .NET SDK: `11.0.100-rc.1.26425.128`, commit `3551975be0`.
- Runtime: `11.0.0-rc.1.26425.128`, commit `3551975be0`.
- Roslyn: `5.11.0-1.26425.128`, commit `3551975be08744f0418857c5bed8ab1545c5dd47`.
- Target framework: `net11.0`; language default `13.0` with explicit preview lane.
- NativeAOT: `11.0.0-rc.1.26425.128`, `win-x64`.
- Native linker: MSVC `14.29.30133`, linker file version `14.29.30159.0`.
- AdmissionVerifier source digest: SHA-256 `991A295E05E41F9DFAF1D2C0CAEDD24013321BFEAE135F06DBA0EA6C2F87DA45` (independent 2026-09-19 audit update; ruleset V13 adds recursive local value-type closure for ambient static references).

## Source and package tuple

- SingNextOS baseline: `a67eea1aafc72054d22f1586b62c6883cdc71681` plus the preserved P03-P13 worktree diff.
- HybridCPU source: `794c4a53494f503855ac8cf209efab23fde083b2`.
- `HybridCPU.ExternalRuntime.Contracts` 1.14.0: SHA-256 `B96E99BDA066EE585B26A11CBFA7B68CE6BF44FC0006679483CCC1A4EEB678C2`.
- `HybridCPU.ExternalRuntime` 1.3.0: SHA-256 `191A1976DECAF607425B3F93378BA11446B32AF2EBFD09DFF45A26944E7E765F`.
- Compatibility-only `HybridCPU.ExternalRuntime.Contracts` 1.3.0: SHA-256 `7956596E820F2536542A73171205ED0B7A3366996BBDB2B51D95C2AE1FE3FC92`.
- NuGet dependency versions and content hashes are pinned by every checked-in `packages.lock.json`; CI locked restore is the executable closure gate.

## Schemas

- capability constraints: `CapabilityConstraintSchema.V1`;
- manifest: `ServiceManifestV2` over V1 compatibility projection;
- audit: `SingCapAuditV1`;
- admission: `SingPlusAdmissionProofV1`;
- provider mapping: `SingCapHybridCpuProviderMappingV1`;
- performance: `SingCapPerformanceQualificationV1`;
- claims: `SingCapClaimEvidenceMatrixV1`.

The absent `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` remains a documented limitation; no contents are inferred.
