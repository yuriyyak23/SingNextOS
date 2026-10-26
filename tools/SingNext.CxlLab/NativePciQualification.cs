using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SingNext.Boot.Core;

namespace SingNext.CxlLab;

internal static class NativePciQualification
{
    public static void Run(LabVm vm,string output,Discovery d,Dictionary<string,object?> report,string? transcript=null)
    {
        string text=transcript??vm.Shell(@"fs0:\CXLDISC.EFI",60);
        File.WriteAllText(Path.Combine(output,"native-pci-discovery.txt"),text);
        if(!text.Contains("CXL_NATIVE_PCI_SELFTEST_PASSED NEGATIVES=13")||!text.Contains("CXL_NATIVE_PCI_PASS")||text.Contains("CXL_NATIVE_PCI_UNSUPPORTED_OR_INVALID"))throw new InvalidDataException("Native EFI PCI discovery did not complete; inspect native-pci-discovery.txt.");
        bool bi=NativeAcpiQualification.Verify(vm,output,text,d);
        int warnings=0;
        foreach(var (tag,location,memory,expectedBase) in new[]{("EP",d.Endpoint,true,d.Device.Address),("RP",d.RootPort,false,d.Port.Address)})
        {
            var identity=Regex.Matches(text,@"(?m)^CXL_NATIVE_"+tag+@" BDF=([0-9a-f]{16}) BASE=([0-9a-f]{16})\s*$");
            if(identity.Count!=1)throw new InvalidDataException("Missing/duplicate native PCI identity: "+tag);
            ulong packed=ulong.Parse(identity[0].Groups[1].Value,NumberStyles.HexNumber),address=ulong.Parse(identity[0].Groups[2].Value,NumberStyles.HexNumber);
            if(packed!=(((ulong)location.Bus<<16)|((ulong)location.Device<<8)|location.Function)||address!=expectedBase)throw new InvalidDataException("Native PCI identity/register address disagrees with host discovery.");
            var rows=Regex.Matches(text,@"(?m)^PCIRAW "+tag+@" ([0-9a-f]{4}) ([0-9a-f]{32})\s*$");
            if(rows.Count!=256)throw new InvalidDataException("Native PCI configuration rows incomplete.");
            byte[] raw=new byte[4096];
            for(int i=0;i<rows.Count;i++)
            {
                if(int.Parse(rows[i].Groups[1].Value,NumberStyles.HexNumber)!=i*16)throw new InvalidDataException("Native PCI rows duplicated, reordered or out of range.");
                Convert.FromHexString(rows[i].Groups[2].Value).CopyTo(raw,i*16);
            }
            File.WriteAllBytes(Path.Combine(output,"native-pci-"+tag+".bin"),raw);
            var parsed=DiscoveryCompatibility.Decode(raw,new BootPciAddress(0,location.Bus,location.Device,location.Function),memory);
            if(parsed.ComponentBase!=address)throw new InvalidDataException("Native and managed DVSEC/BAR decoders disagree.");
            warnings+=parsed.Warnings.Count;
            ulong configAddress=checked(d.Ecam+((ulong)location.Bus<<20)+((ulong)location.Device<<15)+((ulong)location.Function<<12));
            byte[] physical=vm.ReadBytes(configAddress,4096);
            if(!physical.AsSpan().SequenceEqual(raw))throw new InvalidDataException("EFI PCI read differs from QMP ECAM snapshot.");
        }
        var overlap=Regex.Matches(text,@"(?m)^CXL_NATIVE_PCI_PASS OVERLAPS=([0-9a-f]{16})\s*$");
        if(overlap.Count!=1||ulong.Parse(overlap[0].Groups[1].Value,NumberStyles.HexNumber)!=(ulong)warnings)throw new InvalidDataException("Native and managed opaque overlap policies disagree.");
        report["GuestReadOnlyPciDiscoveryVerified"]=true;report["GuestNativeDvsecParserMatchesManaged"]=true;
        report["GuestPciIoRawSnapshotsMatchQmp"]=true;report["GuestNativePciParserNegativeCasesPassed"]=13;
        report["GuestNativePciDiscoveryUsedPinnedBdf"]=false;report["GuestCfmwDiscoveryVerified"]=true;report["GuestNativeAcpiParserNegativeCasesPassed"]=10;
        report["GuestHdmProgrammingByEfiVerified"]=false;report["GuestDmaIsolationQualified"]=false;
        report["GuestCfmwBiAdvertised"]=bi;report["GuestCfmwAccessPolicyQualified"]=false;report["GuestCxlBiOperationVerified"]=false;
        File.WriteAllText(Path.Combine(output,"native-pci-verification.json"),JsonSerializer.Serialize(new{Scope="Read-only diagnostic via firmware PCI I/O, not a production DMA-isolated boot session",RawConfigurations=2,NativeNegativeCases=13,RequiredCxlDiscoveryValid=true,FullPciCapabilityChainValid=warnings==0,OpaqueOverlapWarnings=warnings,GuestDmaIsolationQualified=false,GuestCfmwDiscoveryVerified=true},LabVm.JsonOptions));
    }
}
