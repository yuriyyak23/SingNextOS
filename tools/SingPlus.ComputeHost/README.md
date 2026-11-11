# Bounded ComputeService software host

Executes admitted component discovery and generated capability-gated ComputeService
session. Source is borrowed, destination moves to service and returns through the
correlated response. Checks32 bytes0x5A, unchanged source handle, same destination
RegionId/+2 ownership generations, consumed old destination wrapper and exact
caller64->0/service0/Regions Released after cleanup.

After restore when dependencies change:

```powershell
 dotnet build tools/SingPlus.ComputeHost/SingPlus.ComputeHost.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
 dotnet tools/SingPlus.ComputeHost/bin/Debug/net11.0/SingPlus.ComputeHost.dll
```

Host DSC1 provider supplies lifecycle evidence only. Runtime performs local reference
copy; this is ModelOnly ownership/session execution, not external compute bytes,
physical DMA, zero-copy or guest-machine execution. Component image is admission
metadata. No reflection/fake/test callback. Gates OFF; ISA NONE; Java skipped;
no ProductionQualified.
