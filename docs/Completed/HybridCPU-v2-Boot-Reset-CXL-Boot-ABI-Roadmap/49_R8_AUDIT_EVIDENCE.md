# R8 Independent Audit Evidence — SingNextOS Fresh Takeover

Baseline HEAD before R8: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty status recorded/preserved. Optional adapter plan absent. No destructive/remote Git, network, hardware, core, or QEMU action was used.

## Audit and remediation

- `HybridBootInfoCodec` validates structure/version/length/count/reserved/CRC/SHA-384. The importer requires exactly one required/evidence-only Security record and validates its status/trust epoch/floor/generation.
- Fresh endpoint candidates come only from a new OS discovery pass and are re-queried through the existing provider-neutral `ICxlDiscoveryProvider`. BootInfo BDF/DSN/HPA/DPA/decoder/route values never select the endpoint.
- Defect fixed: the importer advertised every known record kind as a supported required record while only Security had a semantic decoder. The supported-required set is now Security only; required Physical/Temporary/Diagnostic records fail before discovery.
- Defect fixed: physical/selection/diagnostic/aperture records could lack `EvidenceOnly`; aperture could lack `Temporary|MustNotUseAsRam`. Misclassified records now fail before discovery.
- Firmware aperture outcomes remain only `Absent/Released/Stale/Quarantined`. No failure means reclaim/release/closure, and device/provider failure still invokes retirement/quarantine when an aperture was reported.
- Runtime project references only versioned provider-neutral Boot.Contracts, not adapter implementation. Existing `CxlAuthorityBridge`/Region path remains the sole authority creation path.

The takeover result carries provider snapshot/evidence and an importer-local sequence only. It contains no `OwnedRegion`, `RegionUse`, capability, provider lease, live firmware mapping or provider-private authority. Boot image, firmware mapping, provider device and admission sequences remain separate domains.

## Command and result

`dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~HybridBootInfoImporterTests|FullyQualifiedName~CxlAuthorityBridgeTests|FullyQualifiedName~RepositoryArchitecturePolicyTests" --verbosity minimal` — passed 28, failed 0.

Claim level: `AdapterQualified` for the local SingNext importer/provider boundary only. Physical firmware/HDM/hardware behavior remains unclaimed.

No HybridCPU core/ISE/ISA/compiler/register/fetch/pipeline/replay/memory-controller/retire/scheduler/runtime-legality/microarchitecture implementation changed. SingNextOS did not adopt firmware evidence as authority.
