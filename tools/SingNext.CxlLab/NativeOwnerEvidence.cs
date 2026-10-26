using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SingNext.Boot.Core;

namespace SingNext.CxlLab;

// Bounded host witness of the native diagnostic owner. Never endpoint/reset authority.
internal static class NativeOwnerEvidence
{
    public static void Observe(LabVm vm, NativePreparationSession session, Discovery discovery,
        string transcript, byte[] capsule, string output, Dictionary<string,object?> report,
        bool finished, bool retainedRam, bool beforeRam = false)
    {
        var begin=Regex.Matches(transcript,@"CXL_NATIVE_OWNER_BEGIN RECORD=([0-9a-f]{16}) PAGES=([0-9a-f]{16}) RESET_AUTHORITY=0");
        if(begin.Count!=1)throw new InvalidDataException("Missing or ambiguous original native owner.");
        ulong Number(string value)=>ulong.Parse(value,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        ulong address=Number(begin[0].Groups[1].Value),pages=Number(begin[0].Groups[2].Value);
        if(address==0||address%4096!=0||pages!=19||address>ulong.MaxValue-pages*4096)
            throw new InvalidDataException("Native owner record geometry invalid.");
        if(finished&&(!transcript.Contains("CXL_NATIVE_OWNER_PINNED")||!transcript.Contains("CXL_NATIVE_OWNER_STALE_SESSION_DENIED")))
            throw new InvalidDataException("Native owner has no pin/stale-session receipt.");
        if(session.CheckCurrent(vm)!=BootFailure.None)throw new InvalidDataException("Native owner metadata observation is stale.");
        // Header only on quarantine/completion: never read the old RAM allocation,
        // consume its snapshot, or infer that a RAM address is still allocated.
        byte[] bytes=vm.ReadBytes(address,128);
        if(session.CheckCurrent(vm)!=BootFailure.None)throw new InvalidDataException("Reset during native owner metadata observation.");
        ulong Field(int index)=>BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(index*8));
        ulong flags=Field(11),allocations=Field(13),frees=Field(14);
        ulong expectedSourceBytes=beforeRam?0UL:(ulong)capsule.Length;
        ulong expectedFrees=beforeRam||retainedRam?0UL:finished?1UL:0UL;
        ulong requiredFlags=beforeRam?5UL:0x35UL;
        if(Field(0)!=0x31524e574f4c5843UL||Field(1)!=address||Field(2)==0||Field(3)==0||
           Field(4)!=discovery.Hpa||Field(5)!=discovery.Device.HdmAddress||Field(6)!=discovery.Port.HdmAddress||Field(7)!=discovery.Host.HdmAddress||
           Field(10)!=expectedSourceBytes||Field(12)!=1||allocations!=(beforeRam?0UL:1UL)||Field(15)!=expectedSourceBytes||
           (flags&requiredFlags)!=requiredFlags||((flags&1024)!=0)!=finished||((flags&64)!=0)!=retainedRam||frees!=expectedFrees)
            throw new InvalidDataException("Native owner identity, original effects or lifetime flags differ.");
        if((!beforeRam&&!NativePreparationSession.ValidGeometry(Field(8),Field(9)))||
           (beforeRam&&(Field(8)!=0||Field(9)!=0||(flags&(16|32|64|256))!=0))||
           (session.OwnedRamAddress is ulong bound&&(bound!=Field(8)||session.OwnedRamPages!=Field(9))))
            throw new InvalidDataException("Native allocation differs from the original bound RAM tuple.");
        byte[] endpoint=vm.ReadBytes(address+128+73728,24);
        ulong Packed(PciLocation location)=>((ulong)location.Bus<<16)|((ulong)location.Device<<8)|location.Function;
        if(session.CheckCurrent(vm)!=BootFailure.None||BinaryPrimitives.ReadUInt64LittleEndian(endpoint)!=Packed(discovery.Endpoint)||
           BinaryPrimitives.ReadUInt64LittleEndian(endpoint.AsSpan(8))!=Packed(discovery.RootPort)||
           BinaryPrimitives.ReadUInt64LittleEndian(endpoint.AsSpan(16))==0)
            throw new InvalidDataException("Native owner's discovery/mailbox identity differs from current diagnostic discovery.");
        if(finished){
            var ends=Regex.Matches(transcript,@"CXL_NATIVE_OWNER_END RECORD=([0-9a-f]{16}) FLAGS=([0-9a-f]{16}) RAM=([0-9a-f]{16}) PAGES=([0-9a-f]{16}) ALLOCS=([0-9a-f]{16}) FREES=([0-9a-f]{16}) REMOTE_CLOSURE=0");
            if(ends.Count!=1||Number(ends[0].Groups[1].Value)!=address||Number(ends[0].Groups[2].Value)!=flags||
               Number(ends[0].Groups[3].Value)!=Field(8)||Number(ends[0].Groups[4].Value)!=Field(9)||
               Number(ends[0].Groups[5].Value)!=allocations||Number(ends[0].Groups[6].Value)!=frees||(flags&512)==0)
                throw new InvalidDataException("Native owner terminal receipt is not its retained original record.");
        }else{
            if((flags&512)!=0||(flags&128)==0||(flags&8)!=0)throw new InvalidDataException("Native RAM publication lacks local retirement.");
            byte[] snapshot=vm.ReadBytes(address+128,capsule.Length);
            if(session.CheckCurrent(vm)!=BootFailure.None||!snapshot.AsSpan().SequenceEqual(capsule))
                throw new InvalidDataException("Native owner's authenticated snapshot is stale or differs from original input.");
            File.WriteAllBytes(Path.Combine(output,"native-owner-snapshot.bin"),snapshot);
        }
        File.WriteAllBytes(Path.Combine(output,finished?"native-owner-terminal.bin":"native-owner-ready.bin"),bytes);
        File.WriteAllText(Path.Combine(output,finished?"native-owner-terminal.json":"native-owner-ready.json"),
            JsonSerializer.Serialize(new{Scope="Native diagnostic owner metadata; not guest reset/DMA authority",Address=address,Pages=pages,Flags=flags,
                Ram=Field(8),RamPages=Field(9),MapAttempts=Field(12),RamAllocations=allocations,RamFreeAttempts=frees,
                SourceReadBytes=Field(15),Finished=finished,RamRetained=retainedRam,RemoteClosureProven=false},LabVm.JsonOptions));
        report["NativeGuestOwnerAdopted"]=true;
        report["NativeGuestOwnerRecordAddress"]=$"0x{address:x}";
        if(finished){
            report["NativeGuestOwnerPinnedAfterImageReturn"]=true;
            report["NativeGuestOwnerRamRetained"]=retainedRam;
            report["NativeGuestOwnerRamFreeAttempts"]=frees;
            report["NativeGuestOwnerStaleSessionDenied"]=true;
        }
    }
}
