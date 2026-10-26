using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SingPlus.Platform.HybridCpu.Boot;

namespace SingNext.CxlLab;

internal static class NativeMailboxDiagnostic
{
    public static object Run(string configPath,string output,string efiImage,int timeout,int ram=4096,int bus=12,int edu=0)
    {
        output=Path.GetFullPath(output);if(Directory.Exists(output)||File.Exists(output))throw new IOException("Mailbox diagnostic output must be new.");
        using var doc=JsonDocument.Parse(File.ReadAllText(configPath));var config=doc.RootElement;
        if(config.GetProperty("CxlMiB").GetInt32()!=256||config.GetProperty("LsaMiB").GetInt32()!=1)throw new InvalidDataException("Mailbox lab profile requires 256 MiB / 1 MiB LSA.");
        byte[] image=File.ReadAllBytes(efiImage);Directory.CreateDirectory(output);File.Copy(configPath,Path.Combine(output,"source-config.json"));InputEvidence.Record(output,configPath,config);
        File.WriteAllBytes(Path.Combine(output,"CXLMBX.EFI"),image);
        string metadata=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(efiImage))!,"build.json");if(File.Exists(metadata))File.Copy(metadata,Path.Combine(output,"efi-build.json"));
        var result=new Dictionary<string,object?>{{"Scope","NativeMailboxIdentifyDiagnostic"},{"Passed",false},{"EfiSha256",Convert.ToHexString(SHA256.HashData(image))},{"ActualRamMiB",ram},{"ActualCxlBus",bus},{"EduDeviceCount",edu},{"GuestCxlMemoryAccessible",null},{"QtestAttached",false},{"GuestDmaIsolationQualified",false},{"BootIntegrationReady",false},{"RuntimeKernelExecuted",false},{"BootHandoffPerformed",false}};
        try{
            var files=new Dictionary<string,byte[]>{{"startup.nsh",Encoding.ASCII.GetBytes("echo -off\r\necho CXL_LAB_READY\r\n")},{"CXLMBX.EFI",image}};
            using(var vm=new LabVm(config,Path.Combine(output,"vm"),files,timeout,ramMiB:ram,cxlBusNumber:bus,eduCount:edu)){
                var d=Qualification.Discover(vm);File.WriteAllText(Path.Combine(output,"discovery.json"),JsonSerializer.Serialize(d,LabVm.JsonOptions));
                byte[] beforePci=File.ReadAllBytes(Path.Combine(vm.DirectoryPath,"logs","endpoint-pci-config.bin"));
                var beforeLocator=CxlPciDvsecParser.ParseRegisterLocator(beforePci,d.EndpointDvsecs.Single(x=>x.Id==8).Offset);
                if(!beforeLocator.IsSuccess)throw new InvalidDataException("Pre-command locator invalid.");
                var beforeBase=CxlPciDvsecParser.ResolveRegisterBlock(beforePci,beforeLocator.Value!.Blocks.Single(x=>x.BlockIdentifier==3));
                if(!beforeBase.IsSuccess)throw new InvalidDataException("Pre-command device BAR invalid.");
                File.WriteAllBytes(Path.Combine(output,"device-registers-before.bin"),vm.ReadBytes(beforeBase.Value,4096));
                string text=vm.Shell(@"fs0:\CXLMBX.EFI",60);File.WriteAllText(Path.Combine(output,"native-mailbox.txt"),text);
                File.WriteAllBytes(Path.Combine(output,"device-registers-after.bin"),vm.ReadBytes(beforeBase.Value,4096));
                if(!text.Contains("CXL_NATIVE_MAILBOX_SELFTEST_PASSED NEGATIVES=8")||!text.Contains("CXL_NATIVE_MAILBOX_PASS")||text.Contains("CXL_NATIVE_MAILBOX_FAILED"))throw new InvalidDataException("Native mailbox failed; inspect native-mailbox.txt.");
                NativePciQualification.Run(vm,output,d,result,text);
                Verify(vm,output,d,result,text);
            }
            result["Passed"]=true;return result;
        }catch(Exception e){result["Error"]=e.Message;throw;}
        finally{File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(result,LabVm.JsonOptions));}
    }

    internal static void Verify(LabVm vm,string output,Discovery d,Dictionary<string,object?> result,string text)
    {
        if(!text.Contains("CXL_NATIVE_MAILBOX_PASS"))throw new InvalidDataException("Mailbox completion missing.");
                Match One(string pattern){var matches=Regex.Matches(text,pattern,RegexOptions.Multiline);return matches.Count==1?matches[0]:throw new InvalidDataException("Missing/duplicate mailbox marker.");}
                ulong Hex(Group group)=>ulong.Parse(group.Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
                var regs=One(@"^CXL_NATIVE_MAILBOX_REGS DEVICE=([0-9a-f]{16}) MB=([0-9a-f]{16}) STATUS=([0-9a-f]{16})\s*$");
                ulong device=Hex(regs.Groups[1]),mailbox=Hex(regs.Groups[2]);
                byte[] pci=File.ReadAllBytes(Path.Combine(output,"native-pci-EP.bin"));
                var locator=CxlPciDvsecParser.ParseRegisterLocator(pci,d.EndpointDvsecs.Single(x=>x.Id==8).Offset);
                if(!locator.IsSuccess)throw new InvalidDataException("Managed locator rejected native mailbox device.");
                var resolved=CxlPciDvsecParser.ResolveRegisterBlock(pci,locator.Value!.Blocks.Single(x=>x.BlockIdentifier==3));
                if(!resolved.IsSuccess||resolved.Value!=device||mailbox<device||mailbox-device>4096-0x120)throw new InvalidDataException("Native mailbox register address disagrees with managed locator.");
                byte[] raw=Convert.FromHexString(One(@"^CXL_NATIVE_IDENTIFY_RAW ([0-9a-f]+)\s*$").Groups[1].Value);
                if(raw.Length<67||raw.Length>128)throw new InvalidDataException("Identify response size invalid.");
                byte[] observed=vm.ReadBytes(mailbox+0x20,raw.Length);
                if(!observed.AsSpan().SequenceEqual(raw)||vm.Read32(mailbox+4)!=0||vm.Read32(mailbox+8)%65536!=0x4000)throw new InvalidDataException("Mailbox response/control differs from QMP snapshot.");
                File.WriteAllBytes(Path.Combine(output,"identify-response.bin"),raw);
                File.WriteAllBytes(Path.Combine(output,"device-registers.bin"),vm.ReadBytes(device,4096));
                ulong total=BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(16)),vol=BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(24)),pmem=BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(32));
                uint lsa=BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(56));
                if(total!=1||vol!=0||pmem!=1||lsa!=1048576)throw new InvalidDataException("Identify capacity/LSA disagree with requested persistent-memdev lab profile.");
                var values=One(@"^CXL_NATIVE_IDENTIFY TOTAL=([0-9a-f]{16}) VOLATILE=([0-9a-f]{16}) PERSISTENT=([0-9a-f]{16}) LSA=([0-9a-f]{16}) LEN=([0-9a-f]{8})\s*$");
                if(Hex(values.Groups[1])!=(total<<28)||Hex(values.Groups[2])!=(vol<<28)||Hex(values.Groups[3])!=(pmem<<28)||Hex(values.Groups[4])!=lsa||Hex(values.Groups[5])!=(ulong)raw.Length)throw new InvalidDataException("Native Identify decode differs from host.");
                string tree=vm.Hmp("info mtree");File.WriteAllText(Path.Combine(output,"mailbox-mtree.txt"),tree);
                if(tree.Contains("cxl-direct-mapping-alias-",StringComparison.Ordinal))throw new InvalidDataException("Unexpected HDM alias in mailbox-only run.");
                result["MailboxRegisterBase"]=$"0x{mailbox:x}";result["DeviceRegisterBase"]=$"0x{device:x}";
                result["IdentifyOpcode"]=0x4000;result["MailboxOpcodeBits"]=16;result["BootTransportOpcodeBits"]=16;
                result["IdentifyTotalBytes"]=total<<28;result["IdentifyVolatileBytes"]=vol<<28;result["IdentifyPersistentBytes"]=pmem<<28;result["IdentifyLsaBytes"]=lsa;
                result["IdentifyResponseBytes"]=raw.Length;result["GuestMailboxResponseMatchesQmp"]=true;result["GuestMailboxIdentifyVerified"]=true;result["MailboxParserNegativeCasesPassed"]=8;
                result["ConfigurationValid"]=true;result["QemuDeviceCreated"]=true;
        if(text.Contains("CXL_NATIVE_CAPACITY_GATE_PASSED"))result["NativeMailboxCapacityGatePassed"]=true;
    }
}
