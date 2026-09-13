# Phase 11 Implementation Evidence

## Result

Complete for the software/model scope. Real hardware and QEMU execution are not claimed. The model backend is explicitly incapable of satisfying a hardware-attestation policy.

## Traceability

- Authority and `RegionUse`: `RegionUseTests`, `CxlAuthorityBridgeTests`, and `CxlSecurityAndMultiHostTests` cover stale authority/use, writer exclusion, MOVE/borrow/reclaim, evidence separation, idempotent release, and public identity hygiene. Whole-region exclusivity is intentional; subdivision remains future-gated, so the conditional disjoint-use case is not advertised.
- Lifecycle: `ExternalOperationLifecycleTests` covers all seven states, illegal skips at every state, duplicate completion/publication, cancellation before and after submit, stale operation/binding/dependency generations, visibility failure, provider loss, teardown drain, and idempotent release.
- Generation matrix: region mutation/MOVE, platform/device/fabric/backing/provider/security and operation generations are covered by the focused test classes. Fabric tests prove narrow invalidation leaves unrelated bindings current.
- Type 3: `CxlType3MemoryProviderTests` covers ordinary CPU memory, normal platform DMA mapping, hot-remove/migration, backing generation, fallback, capability honesty, and absence of public DPA/decoder identity.
- Type 2 and publication/replay: `CxlType2AcceleratorServiceTests` and `ExternalEffectPolicyTests` cover staged publication, mutation/reset fail-closed behavior, direct-policy gating, irreversible barriers, and read-only behavior.
- Fabric/pooling: `CxlFabricManagerAuthorityTests` drains Prepared through Visible operations, rejects old bindings, preserves unrelated bindings, prevents implicit ownership transfer, and gates P2P on platform isolation.
- Security/multi-host: `CxlSecurityAndMultiHostTests` covers missing/stale evidence, evidence/authority separation, model-only assurance, writable exclusion, explicit read-only support, fenced host-loss reclaim, and no silent reassignment.

## Qualification commands

```powershell
dotnet restore SingNextOS.slnx --force --no-cache
dotnet test SingNextOS.slnx --no-restore --logger "console;verbosity=minimal"
```

Superseded qualification after fabric-exactness Phase 16 on 2026-09-13: 869/869 tests (739 `SingPlus.Tests`, 60 platform tests, 58 neutral-runtime tests, and 12 adapter tests), with zero failures and zero skips. The focused Phase 11/14/15/16 selection passed 111/111.

## Explicit non-claims

- No real hardware coherence, IDE, isolation, Fabric Manager enforcement, zero-copy, or multi-host writable sharing is claimed.
- QEMU/FPGA work remains skipped by user direction.
- Range subdivision is not implemented; whole-region conservative exclusion remains the contract.
