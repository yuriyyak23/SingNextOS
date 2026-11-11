namespace SingNext.CxlLab;

// CPU reads execute in the installed Shell. Authentication still executes on host.
// Register observations detect sampled changes, not transient disable/re-enable or DMA.
internal sealed class ShellCapsuleSource:ICapsuleSource
{
    readonly LabVm vm;
    readonly Discovery discovery;
    readonly uint[] initial;
    public ulong Capacity=>256UL<<20;
    public int ReadCount {get;private set;}
    public ShellCapsuleSource(LabVm vm,Discovery discovery)
    {
        this.vm=vm;this.discovery=discovery;initial=Registers();
    }
    uint[] Registers()
    {
        var values=new List<uint>();
        foreach(var c in new[]{discovery.Device,discovery.Port})
        {
            ulong h=c.HdmAddress??throw new InvalidDataException("HDM absent.");
            foreach(uint offset in new uint[]{4,0x10,0x14,0x18,0x1c,0x20,0x24,0x28})values.Add(vm.Read32(h+offset));
            if((values[^3]&0x600)!=0x600 || (values[^8]&2)==0)
                throw new InvalidDataException("Source mapping is not enabled and committed.");
            ulong start=((ulong)values[^6]<<32)|(values[^7]&0xf0000000u);
            ulong size=((ulong)values[^4]<<32)|(values[^5]&0xf0000000u);
            if(start!=discovery.Hpa || size!=Capacity || values[^2]!=0 || values[^1]!=0)
                throw new InvalidDataException("Source mapping geometry differs from qualified topology.");
        }
        return values.ToArray();
    }
    public (ulong Reset,ulong Mapping) Observe()
    {
        var status=vm.Qmp("query-status");
        if(!status.GetProperty("running").GetBoolean())throw new InvalidDataException("Guest is not running.");
        if(!Registers().AsSpan().SequenceEqual(initial))throw new InvalidDataException("Sampled HDM mapping changed.");
        return(vm.ObservedResetSequence,1);
    }
    public int Read(ulong offset,Span<byte> destination)
    {
        Qualification.ValidateCapsuleRange(offset,(ulong)destination.Length,Capacity);
        ulong hpa=checked(discovery.Hpa+offset);
        byte[] bytes=Qualification.ParseDump(vm.Shell($"dmem {hpa:x} {destination.Length:x}",45),hpa,destination.Length);
        try{bytes.CopyTo(destination);ReadCount++;return bytes.Length;}
        finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);}
    }
}
