using System.Buffers.Binary;
using SingNext.Boot.Core;
using SingPlus.Platform.HybridCpu.Boot;

namespace SingNext.CxlLab;

internal static class DiscoveryCompatibility
{
    public static (IReadOnlyList<Dvsec> Headers, ulong ComponentBase, IReadOnlyList<string> Warnings) Decode(byte[] raw, BootPciAddress bdf, bool requireMemoryDevice=true)
    {
        var reset=new BootResetSnapshot(1,1);
        var chain=BoundedPciCapabilityWalker.Walk(new FrozenPci(raw,bdf,reset),bdf,0x100,reset,64);
        if(!chain.IsSuccess)throw new InvalidDataException(chain.Detail);
        var headers=new List<Dvsec>();var warnings=new List<string>();CxlDeviceDvsecInfo? device=null;CxlRegisterLocatorInfo? locator=null;
        foreach(var cap in chain.Value!) {
            if(cap.Id!=CxlPciDvsecParser.PcieDvsecCapabilityId)continue;
            if(cap.Offset>raw.Length-10)throw new InvalidDataException("Truncated DVSEC header.");
            ushort rawId=BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(cap.Offset+8));
            bool consumed=rawId is 0 or 8;
            var header=CxlPciDvsecParser.ParseHeader(raw,cap.Offset,opaqueHeaderOnly:!consumed);
            if(!header.IsSuccess)throw new InvalidDataException(header.Detail);
            var h=header.Value;if(h.NextOverlapsPayload)warnings.Add($"Opaque DVSEC ID {h.DvsecId} at 0x{h.Offset:x} declares length 0x{h.Length:x} overlapping next 0x{h.NextOffset:x}; payload not decoded or trusted.");headers.Add(new(h.Offset,h.VendorId,h.DvsecId,h.Revision,h.Length));
            if(h.VendorId!=CxlPciDvsecParser.CxlVendorId)continue;
            if(h.DvsecId==0){if(device is not null)throw new InvalidDataException("Duplicate CXL Device DVSEC.");var value=CxlPciDvsecParser.ParseDevice(raw,cap.Offset);if(!value.IsSuccess)throw new InvalidDataException(value.Detail);device=value.Value;}
            if(h.DvsecId==8){if(locator is not null)throw new InvalidDataException("Duplicate CXL Register Locator.");var value=CxlPciDvsecParser.ParseRegisterLocator(raw,cap.Offset);if(!value.IsSuccess)throw new InvalidDataException(value.Detail);locator=value.Value;}
        }
        uint pciClass=BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8))>>8;
        if(locator is null || (requireMemoryDevice && (device is null||!device.Value.MemoryCapable||device.Value.CacheCapable||pciClass>>8!=0x0502||!locator.Blocks.Any(x=>x.BlockIdentifier==3))) || (!requireMemoryDevice && pciClass>>8!=0x0604))
            throw new InvalidDataException("Endpoint lacks the qualified memory-only class/capability/register-block combination.");
        var component=locator.Blocks.SingleOrDefault(x=>x.BlockIdentifier==1);
        if(component.BlockIdentifier!=1)throw new InvalidDataException("Component register block missing.");
        foreach(var block in locator.Blocks) {var resolved=CxlPciDvsecParser.ResolveRegisterBlock(raw,block);if(!resolved.IsSuccess)throw new InvalidDataException(resolved.Detail);}
        var address=CxlPciDvsecParser.ResolveRegisterBlock(raw,component);
        if(!address.IsSuccess)throw new InvalidDataException(address.Detail);
        return(headers,address.Value,warnings);
    }
    private sealed class FrozenPci(byte[] bytes,BootPciAddress bdf,BootResetSnapshot reset):IBootPciConfiguration
    {
        public BootResult<uint> Read32(BootPciAddress address,ushort offset,BootResetSnapshot observed) {
            if(observed!=reset)return BootResult<uint>.Fail(BootFailure.ResetObserved,"Snapshot reset mismatch.");
            if(address!=bdf||offset%4!=0||offset>bytes.Length-4)return BootResult<uint>.Fail(BootFailure.BoundsViolation,"Frozen PCI read outside bounds.");
            return BootResult<uint>.Success(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
        }
    }
    internal static void SelfTest()
    {
        byte[] raw=new byte[4096];raw[0x0b]=5;raw[0x0a]=2;
        Put32(raw,0x10,0x81200004);Put32(raw,0x18,0x81210004);
        Put32(raw,0x100,0x14010023);Put32(raw,0x104,(0x3cu<<20)|(3u<<16)|0x1e98);Put16(raw,0x108,0);Put16(raw,0x10a,0x1e);
        Put32(raw,0x140,0x10023);Put32(raw,0x144,(0x24u<<20)|0x1e98);Put16(raw,0x148,8);Put32(raw,0x14c,0x100);Put32(raw,0x154,0x302);
        var bdf=new BootPciAddress(0,13,0,0);if(Decode(raw,bdf).ComponentBase!=0x81200000)throw new Exception("Synthetic wire DVSEC decode failed.");
        foreach(uint revision in new[]{1u,2u}) {byte[] compatible=(byte[])raw.Clone();Put32(compatible,0x104,(0x38u<<20)|(revision<<16)|0x1e98);if(Decode(compatible,bdf).ComponentBase!=0x81200000)throw new Exception("Common revision prefix failed.");}
        byte[] relocated=(byte[])raw.Clone();Array.Copy(raw,0x100,relocated,0x180,0x3c);Array.Copy(raw,0x140,relocated,0x1c0,0x24);Put32(relocated,0x100,0x18010001);Put32(relocated,0x180,0x1c010023);
        if(Decode(relocated,bdf).ComponentBase!=0x81200000)throw new Exception("Relocated DVSEC headers failed.");
        int rejected=0;
        foreach(Action<byte[]> change in new Action<byte[]>[]{
            x=>Put32(x,0x104,(0x3cu<<20)|(4u<<16)|0x1e98),x=>Put32(x,0x104,(0xfffu<<20)|(3u<<16)|0x1e98),
            x=>Put16(x,0x108,3),x=>Put16(x,0x10a,0x1f),x=>Put32(x,0x14c,0x107),x=>Put32(x,0x14c,0x101),
            x=>Put32(x,0x154,0x100),x=>Put32(x,0x144,(0x24u<<20)|(1u<<16)|0x1e98),x=>Put16(x,0x14a,1),
            x=>Put32(x,0x100,0x10210023),x=>Put32(x,0x140,0x10010023),x=>Put32(x,0x10,0x81200001),
            x=>Put32(x,0x154,0x301),x=>Put32(x,0x104,(0x20u<<20)|(3u<<16)|0x1e98),x=>x[0x0a]=0}) {
            byte[] bad=(byte[])raw.Clone();change(bad);try{Decode(bad,bdf);}catch(InvalidDataException){rejected++;}
        }
        if(rejected!=15)throw new Exception($"DVSEC negative checks failed ({rejected}/15).");
        Console.WriteLine("Real-layout DVSEC contract: positive fixture + 15 negative checks passed.");
    }
    static void Put32(byte[] b,int at,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at),v);
    static void Put16(byte[] b,int at,ushort v)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at),v);
}
