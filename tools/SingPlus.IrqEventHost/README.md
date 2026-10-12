# Software IRQ/event host

Runs original native software signals through existing provider, kernel event delivery
and exact cleanup, followed by component-scoped generated driver-session delivery.
The executable checks original replay after acknowledgement produces no second event,
Region release, independent direct/component charge512->0 and zero native IRQ/device/root
users. It has no reflection, fake producer or test-only callback.

After restore when dependencies change:

```powershell
 dotnet build tools/SingPlus.IrqEventHost/SingPlus.IrqEventHost.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
 dotnet tools/SingPlus.IrqEventHost/bin/Debug/net11.0/SingPlus.IrqEventHost.dll
```

The optional signal API is a trusted software input-source producer. Semantic names
select an unambiguous already admitted original IRQ; they confer no Sing authority.
The component session transports only committed kernel event evidence. The admission
image is metadata, not machine-executed driver code. Physical IRQ requires independent
producer/qualification. Gates OFF; ISA NONE; Java skipped; no ProductionQualified.
