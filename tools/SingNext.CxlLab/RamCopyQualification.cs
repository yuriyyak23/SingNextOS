using System.Globalization;
using System.Text.RegularExpressions;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingNext.CxlLab;

internal static class RamCopyQualification
{
    public static void Run(LabVm vm,string output,byte[] capsule,int payloadBytes,Dictionary<string,object?> report)
    {
        var evidence=new List<object>();
        foreach(string mode in new[]{"--ram-copy","--ram-copy-fail-copy","--ram-copy-fail-info"})
        {
            bool positive=mode=="--ram-copy";string name=positive?"valid":mode.EndsWith("copy",StringComparison.Ordinal)?"bad-copy":"bad-info";
            string prefix=vm.BeginShellUntil(@"fs0:\CXLREAD.EFI "+mode,"CXL_RAM_READY");
            File.WriteAllText(Path.Combine(output,"ram-"+name+"-ready.txt"),prefix);
            if(!prefix.Contains("CXL_EFI_SIGNATURE_AND_HASH_VERIFIED"))throw new InvalidDataException("RAM preparation did not authenticate source.");
            var match=Regex.Match(prefix,@"CXL_RAM_READY STATE=(VALID|CLEARED) RAM=([0-9a-f]{16}) PAYLOAD=([0-9a-f]{16}) INFO=([0-9a-f]{16}) PAGES=([0-9a-f]{16})");
            if(!match.Success)throw new InvalidDataException("EFI did not hold allocated RAM for observer.");
            ulong Value(int group)=>ulong.Parse(match.Groups[group].Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
            ulong address=Value(2),length=Value(3),info=Value(4),pages=Value(5);string suffix;
            try
            {
                if((match.Groups[1].Value=="VALID")!=positive||length!=(ulong)payloadBytes||pages!=(length+4095)/4096+1||address%4096!=0||address>ulong.MaxValue-pages*4096||info!=address+(pages-1)*4096)throw new InvalidDataException("RAM allocation geometry/state mismatch.");
                string tree=vm.Hmp("info mtree -f");File.WriteAllText(Path.Combine(output,"ram-"+name+"-flatview.txt"),tree);
                var views=Regex.Split(tree,@"(?m)^FlatView #").Where(x=>x.Contains("AS \"cpu-memory-0\", root: system",StringComparison.Ordinal)).ToArray();
                if(views.Length!=1)throw new InvalidDataException("CPU physical FlatView unavailable or ambiguous.");
                bool systemRam=Regex.Matches(views[0],@"(?m)^\s*([0-9a-f]{16})-([0-9a-f]{16}) \(prio \d+, ram\): pc\.ram(?:\s|$)").Any(m=>{
                    ulong start=ulong.Parse(m.Groups[1].Value,NumberStyles.HexNumber),end=ulong.Parse(m.Groups[2].Value,NumberStyles.HexNumber);
                    return start<=address&&address<=end&&pages*4096-1<=end-address;
                });
                if(!systemRam)throw new InvalidDataException("Destination is not fully inside pc.ram CPU address space.");
                byte[] owned=vm.ReadBytes(address,checked((int)(pages*4096)));File.WriteAllBytes(Path.Combine(output,"ram-"+name+"-owned.bin"),owned);
                if(positive)
                {
                    if(!owned.AsSpan(0,payloadBytes).SequenceEqual(capsule.AsSpan(LabCapsule.PayloadOffset,payloadBytes)))throw new InvalidDataException("Guest RAM copy differs from authenticated payload.");
                    byte[] bootInfo=owned.AsSpan(checked((int)(info-address)),HybridBootInfoCodec.HeaderSize).ToArray();
                    var parsed=HybridBootInfoCodec.Parse(bootInfo);if(!parsed.IsSuccess)throw new InvalidDataException("Guest BootInfo codec failed: "+parsed.Detail);
                    byte[] canonical=HybridBootInfoCodec.Encode(new(0,LabCapsule.Platform,1,1,ResetReason.ColdPowerOn,1,[]));
                    if(!bootInfo.AsSpan().SequenceEqual(canonical))throw new InvalidDataException("Guest BootInfo differs from existing canonical wire contract.");
                    if(owned.AsSpan(payloadBytes,checked((int)(info-address))-payloadBytes).IndexOfAnyExcept((byte)0)>=0||owned.AsSpan(checked((int)(info-address))+120).IndexOfAnyExcept((byte)0)>=0)throw new InvalidDataException("Guest copy overwrote allocation padding.");
                    File.WriteAllBytes(Path.Combine(output,"guest-hybrid-boot-info.bin"),bootInfo);
                }
                else if(owned.Any(x=>x!=0))throw new InvalidDataException("Failed RAM preparation retained data in owned allocation.");
                evidence.Add(new{Mode=mode,Address=$"0x{address:x}",PayloadBytes=length,BootInfoAddress=$"0x{info:x}",Pages=pages,SystemRam=true,OwnedDuringObservation=true,ClearedBeforeObservation=!positive});
            }
            finally{suffix=vm.ContinueShell("c");File.WriteAllText(Path.Combine(output,"ram-"+name+"-release.txt"),suffix);}
            if(!suffix.Contains("CXL_RAM_RELEASED CLEARED=1")||suffix.Contains("CXL_RAM_OBSERVER_TIMEOUT"))throw new InvalidDataException("Guest RAM cleanup/observer handshake failed.");
        }
        string allocation=vm.Shell(@"fs0:\CXLREAD.EFI --ram-copy-fail-alloc",45);
        File.WriteAllText(Path.Combine(output,"ram-alloc-failure.txt"),allocation);
        if(!allocation.Contains("CXL_RAM_ALLOC_FAILURE_INJECTED")||allocation.Contains("CXL_RAM_READY"))throw new InvalidDataException("Allocation failure injection did not fail closed.");
        File.WriteAllText(Path.Combine(output,"guest-ram-preparation.json"),System.Text.Json.JsonSerializer.Serialize(evidence,LabVm.JsonOptions));
        report["GuestAuthenticatedPayloadCopiedToSystemRam"]=true;report["GuestDestinationSha384Verified"]=true;
        report["GuestHybridBootInfoWireCompatible"]=true;report["GuestRamFailureCleanupObserved"]=true;report["GuestRamAllocationFailureInjected"]=true;
        report["GuestResetAwareCopyQualified"]=false;report["GuestBootInfoFullHandoffQualified"]=false;
    }
}
