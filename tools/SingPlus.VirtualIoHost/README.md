# Bounded Virtual-I/O software host

Runs the actual versioned HybridCPU external runtime through the executable adapter,
SingNextOS provider, kernel admission and generated virtualization session client.
The host creates admitted process/device/domain owners, executes bounded write/read,
closes the original IO and verifies native device retirement and quantitative reclaim.
It does not execute a whole OS/guest syscall data plane or physical device DMA.

From the repository root, restore this project when dependencies change, then build:

```powershell
dotnet build tools/SingPlus.VirtualIoHost/SingPlus.VirtualIoHost.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet tools/SingPlus.VirtualIoHost/bin/Debug/net11.0/SingPlus.VirtualIoHost.dll
```

Expected output: bytes17/19/23/29, OwnedMemory charge512->516->0 and nativeDevices0.
Failure throws and exits nonzero. No reflection, fake producer or test-only runtime
callback is required. Gates OFF; ProductionQualified false; Java skipped; ISA NONE.
