# Compiler-produced guest host

Runs the existing finite-branch HCEXE (result42) through RuntimeKernel, the executable external adapter, ExternalRuntime and the CPU model. This is host orchestration, not an OS image for HybridCPU-v2.

```powershell
dotnet run --project tools/SingPlus.GuestHost --no-restore
dotnet run --project tools/SingPlus.GuestHost --no-restore -- --lost-release-reply
dotnet run --project tools/SingPlus.GuestHost --no-restore -- --service-bind-loss --lost-release-reply
dotnet run --project tools/SingPlus.GuestHost --no-restore -- --service-start-loss --lost-release-reply
dotnet run --project tools/SingPlus.GuestHost --no-restore -- --service-map-loss
dotnet run --project tools/SingPlus.GuestHost --no-restore -- --service-create-loss
dotnet run --project tools/SingPlus.GuestHost --no-restore -- --service-child-create-loss --lost-child-close-reply
dotnet run --project tools/SingPlus.GuestHost --no-restore -- --service-create-source-refusal
dotnet run --project tools/SingPlus.GuestHost --no-restore -- --service-create-source-refusal --revoke-create-source --early-create-teardown
dotnet run --project tools/SingPlus.GuestHost --no-restore -- --service-create-capacity-refusal --lost-capacity-reply
dotnet run --project tools/SingPlus.GuestHost --no-restore -- --service-parent-map-capacity-refusal --lost-parent-map-reply
```

The default package is copied from the existing compiler-produced fixture. An optional positional package path must have compatible mapping/quota/step requirements and return42; this bounded host is not a general launcher.

The normal path closes the executable mapping while preserving a separate guest mapping and its Region/data/charges, then closes that independent user. The loss mode hides only Release reply/query, retains all charges until original reconciliation, and verifies Start/Release are not repeated. It uses the existing adapter injection constructor for fault observation; the real external runtime produces execution and closure.

Service modes use the generated client and actual RuntimeVirtualizationServiceHost. The additive BindExecutablePackageAsync request carries an immutable bounded copy up to1MiB; the existing4096-byte request remains unchanged. Accepted Bind/Start response loss retains the original invocation until exact artifact/guest/parent cleanup. The optional Release loss first retains all charges, then reconciles without repeated effects; the service remains alive after caller teardown.

Map loss mode hides original native guest Map reply/query. Original staged mapping and quota remain pinned until exact guest+parent closure; caller teardown consumes its original invocation, with Map1 and no Bind/Start. It does not cover lost parent V1 admission before guest Map.

Create loss mode uses the actual generated Create client and loses native root Bind reply/query before any child is returned. It checks Bind1, exact original root cleanup, Region/charge reclaim and independent service survival. Cached-root child admission and model-only creation remain separate consumers.

Child-create loss mode uses a fresh root and loses external original ChildCreate reply/query. Optional child-close loss holds the root and2MiB Region charges through partial retirement, then continues the same original Close. It verifies ChildCreate1/ChildClose1 and service survival. Existing-root Create remains refused before acceptance.

Source-refusal mode wraps the actual HybridCPU provider and faults source observation before root Bind, or revokes the caller source with `--revoke-create-source`. `--early-create-teardown` overlaps caller teardown with that query. The accepted generated Create keeps the original root intent before observation; exact never-invoked retirement releases both caller Regions and2MiB charges, while the independent service Region survives. RootBind0/ChildCreate0 are checked. This is bounded fault/adoption verification; provider execution is covered by the normal guest path.

Capacity-refusal mode places the actual native allocator at its exhausted bound before Create. Native original Bind produces a retained explicit no-effect disposition before registration. Optional `--lost-capacity-reply` hides reply/query and verifies that the caller remains pinned until same-original lookup returns that disposition. It checks Bind1/no repeat, nativeRoots0, both caller Regions Released,2MiB charges reclaimed and independent service backing survival. Faulted/default lease alone is insufficient.

Parent-map capacity mode creates an independent guest/parent mapping first, then exhausts the native resource allocator before accepted service Map. Original no-effect consumes only the failed staged reservation/quota; independent mapping/data/acquisition guard remain live. Optional `--lost-parent-map-reply` retains the failed stage and charges until same-original lookup. Caller teardown then closes its original users, with nativeMap2 (independent1/refusal1), no repeat, all charges0 and foreign service Region Owned. Cleanup under root quarantine grants no new mapping/execution permission.

Mapped Region-use acquisition remains denied. Completed/result read does not reclaim or permit another Start. Gates OFF; ISA NONE; no ProductionQualified or physical/cold-recovery claim.
