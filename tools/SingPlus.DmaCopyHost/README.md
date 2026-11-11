# Bounded DMA-copy software host

Runs actual neutral software DMA through the HybridCpu provider and kernel with
separate source/destination process owners, admitted device/mapping/grant capabilities,
owned buffers and attached budgets. MoveProviderDmaBuffers uses existing original
submission, completion/visibility, exact grant/mapping closure and Region release.

The client publishes measured host-access locality observations to the existing
managed freshness cache. Those observations are advisory and grant no authority.
This is a software byte-copy contour; physical DMA, locality benefit, machine execution
and production qualification require their own producers and evidence.

After restoring this project when dependencies change:

```powershell
dotnet build tools/SingPlus.DmaCopyHost/SingPlus.DmaCopyHost.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet tools/SingPlus.DmaCopyHost/bin/Debug/net11.0/SingPlus.DmaCopyHost.dll
```

The executable checks all 64 destination bytes, original output identity, released
source lifetime, source64->0/destination64->64->0 quantitative consequences, and
zero native grants/mappings/devices/roots after teardown. Failure exits nonzero.
No reflection or test-only provider callback. Gates OFF; ISA NONE;
ProductionQualified false; Java skipped.
