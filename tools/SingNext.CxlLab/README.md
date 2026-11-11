# CXL / BootCapsule laboratory

.NET 11 host orchestration and standalone EFI diagnostics for installed Windows
QEMU/EDK2. Canonical documentation: **[docs/QEMU_CXL](../../docs/QEMU_CXL/README.md)**.

```powershell
# From the repository root, after checking shared runner ownership:
dotnet build tools/SingNext.CxlLab/SingNext.CxlLab.csproj -c Release
dotnet tools/SingNext.CxlLab/bin/Release/net11.0/SingNext.CxlLab.dll selftest
./tools/qemu-cxl/Test-CxlNativeCapsule.ps1 -WithRamPreparation -WithMailboxGate -WithRelocation
```

The opt-in native path performs PCI/ACPI discovery, Identify/capacity gate, HDM,
FAT provisioning into fresh CXL RAM, guest SHA384/ECDSA and owned System RAM
preparation. It retires the CXL source before observing RAM, then clears/frees
those pages. No kernel execution or ExitBootServices handoff occurs.

The Shell/managed-mapping path remains separate. RAM-backed persistent-memdev
loses contents after process termination, as accepted for this lab. Backup files
do not represent active CXL RAM contents.

The 120-byte lab BootInfo has zero records; the existing runtime importer requires
Security evidence. BootCapsuleEntry.Run remains Unsupported. Production DMA/reset
authority and native runtime composition are open; original contracts/firmware
were not weakened.

* [Code and actual consumers](../../docs/QEMU_CXL/SOURCE-MAP.md)
* [Current state and evidence scopes](../../docs/QEMU_CXL/CURRENT-STATE-RU.md)
* [Native Identify/capsule gate](../../docs/QEMU_CXL/NATIVE-CAPACITY-GATE-RU.md)
* [RAM/reset contract](../../docs/QEMU_CXL/CORE-RESET-CONTRACT-RU.md)
* [Modernization plan](../../docs/QEMU_CXL/MODERNIZATION-PLAN-RU.md)
