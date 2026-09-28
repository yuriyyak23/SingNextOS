# P17 migration, cleanup and cutover evidence

## Disposition

P17 is **CLOSED FOR THE EXACT HOST CONTOUR** at `RuntimeEnforced`, not `ProductionQualified`. The normative baseline is `6227ea7cf258ef6ffce52001d4d2ffee07355b35`; qualification ran from HEAD `800027893dcf1ed23d8d7fe775841dd9d01a9fff` while preserving the pre-existing P16 evidence worktree.

The exact contour is Windows x64, .NET 11 JIT, host provider, `ComputeTime/Nanoseconds`. All feature gates remain default OFF. Migration changes caller selection only; `CapabilityAuthority`, `ResourceBudgetAuthority`, Region, ExternalOperation, provider and publication owners do not move.

## Contract and behavior chain

`VNextMigrationCoordinator` accepts only migration request v1, legacy SIP v1 or resource-aware SIP v2, and provider resource contract absent/v0 or exact v1. Legacy callers cannot smuggle resource intent. Optional new callers fall back to ordinary SIP against an old provider; required resource semantics are denied rather than weakened. Unknown provider or request versions fail closed.

The exact v1 provider path additionally requires a current `VNextFeatureGateLease`. ON→OFF before submit returns optional work to ordinary SIP. ON→OFF after possible submit returns `Quarantined`, never fallback/resubmit/refund. Existing provider and budget owners still perform all authoritative transitions.

Old manifests remain byte-shape compatible because canonical serialization omits `ResourceUseRequirements` when empty. Resource-aware manifests add that field without changing the V1 base schema and admission records it only as requested metadata. The old generated/ordinary SIP transport remains present and is exercised by the compatibility tests.

`VNextCompatibilityUsageRegistry` is cleanup evidence only. It uses exact consumer and registry generations. A no-live-consumer proof cannot be issued while a legacy manifest/generated SIP/provider/fallback consumer is live, becomes stale after any registration, and has no removal or authority mutation API. No compatibility path was removed in P17.

## Executed evidence

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase17MigrationCutoverTests" --verbosity minimal
  Exit 0; passed 11, failed 0, skipped 0
```

Canonical build, focused/full-suite, parser, manifest, tuple and diff-check results are pinned by `P17_QUALIFICATION_TUPLE.json` and the final closure tests.

```text
.\eng\qualify-vnext.ps1
  solution build: Exit 0; 0 warnings, 0 errors
  focused vNext lane: passed 210, failed 0, skipped 0
  full non-GUI SingPlus.Tests: passed 1553, failed 0, skipped 2
  full non-GUI supporting projects: passed 90 + 58 + 60, failed 0
  aggregate full non-GUI: passed 1761, failed 0, skipped 2
  git diff --check: Exit 0; line-ending warnings only
```

The two skips are the existing suspended-child qualification tests. They were neither hidden nor relabelled.

## Claim boundary and exclusions

This evidence proves `RuntimeEnforced` migration selection and cleanup-proof behavior for the exact host/JIT contour. It does not make the default-OFF gates production rollout, and `ProductionQualified remains false`. It does not qualify HybridCPU, another provider, NativeAOT, temporal upper bounds, guaranteed capacity, throughput, occupancy, hardware, QEMU, firmware or CXL boot.

No HybridCPU ISA, VLIW, pointer width, register model, typed lane, opcode, compiler-to-ISE, pipeline, scheduler-legality, memory-controller, retire or microarchitecture work was performed.
