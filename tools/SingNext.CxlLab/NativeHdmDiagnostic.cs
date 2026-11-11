using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SingNext.CxlLab;

internal static class NativeHdmDiagnostic
{
    public static object Run(string configPath,string output,string efiImage,int timeout,int ramMiB=4096,int bus=12,int edu=0,string? capsulePath=null,bool expectRejected=false,bool nativeRam=false,string nativeRamOption="--ram-copy",string? expectedFileFailure=null,ulong capsuleOffset=0,bool expectMailboxRejection=false)
    {
        output=Path.GetFullPath(output);
        if(capsuleOffset>0x10000000)throw new ArgumentException("Capsule offset outside backing.");
        if(capsulePath is not null&&expectedFileFailure is null)Qualification.ValidateCapsuleRange(capsuleOffset,(ulong)new FileInfo(capsulePath).Length,0x10000000);
        if(expectedFileFailure is not null && (expectedFileFailure is not ("Missing" or "Truncated" or "ExtraByte")||expectRejected))throw new ArgumentException("Invalid or ambiguous file failure profile.");
        if((expectRejected||nativeRam)&&capsulePath is null&&expectedFileFailure!="Missing")throw new ArgumentException("Expected rejection or native RAM requires capsule input.");
        if(Directory.Exists(output)||File.Exists(output))throw new IOException("Native HDM output must be new.");
        using var doc=JsonDocument.Parse(File.ReadAllText(configPath));var config=doc.RootElement;
        if(config.GetProperty("CxlMiB").GetInt32()!=256||config.GetProperty("LsaMiB").GetInt32()!=1||config.GetProperty("WindowMiB").GetInt32()!=4096||config.GetProperty("Accel").GetString()!="tcg")throw new InvalidDataException("Native HDM requires the 256 MiB / 4 GiB TCG lab profile.");
        byte[] image=File.ReadAllBytes(efiImage);Directory.CreateDirectory(output);File.Copy(configPath,Path.Combine(output,"source-config.json"));InputEvidence.Record(output,configPath,config);
        File.WriteAllBytes(Path.Combine(output,"CXLHDM.EFI"),image);
        string metadata=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(efiImage))!,"build.json");if(File.Exists(metadata))File.Copy(metadata,Path.Combine(output,"efi-build.json"));
        var report=new Dictionary<string,object?>{{"Scope","NativeEfiHdmScratchDiagnostic"},{"Passed",false},{"QtestAttached",false},{"HostShellMmioProgrammingUsed",false},{"GuestCxlMemoryAccessible",null},{"DecoderIsolationQualified",false},{"TransactionalHdmMappingRetirementQualified",false},{"GuestDmaIsolationQualified",false},{"RuntimeKernelExecuted",false},{"BootHandoffPerformed",false},{"EfiSha256",Convert.ToHexString(SHA256.HashData(image))},{"ActualRamMiB",ramMiB},{"ActualCxlBus",bus},{"EduDeviceCount",edu}};
        report["EfiImagePath"]=Path.GetFullPath(efiImage);
        report["CapsuleOffsetBytes"]=capsuleOffset;report["CxlBackingBytes"]=0x10000000UL;
        byte[]? capsule=capsulePath is null?null:File.ReadAllBytes(capsulePath);
        if(capsule is not null){
            if(capsule.Length==0||capsule.Length>73728)throw new InvalidDataException("Native capsule input outside bounded profile.");
            File.WriteAllBytes(Path.Combine(output,"capsule-input.bin"),capsule);
            report["Scope"]="NativeEfiHdmCapsuleDiagnostic";report["CapsuleInputSha256"]=Convert.ToHexString(SHA256.HashData(capsule));
            report["CapsuleImageBytes"]=capsule.Length;
        }
        try{
            var files=new Dictionary<string,byte[]>{{"startup.nsh",Encoding.ASCII.GetBytes("echo -off\r\necho CXL_LAB_READY\r\n")},{"CXLHDM.EFI",image}};
            if(capsule is not null)files.Add("capsule.bin",capsule);
            using(var vm=new LabVm(config,Path.Combine(output,"vm"),files,timeout,ramMiB:ramMiB,cxlBusNumber:bus,eduCount:edu,hostBridgeHdm:true)){
                var d=Qualification.Discover(vm);File.WriteAllText(Path.Combine(output,"discovery.json"),JsonSerializer.Serialize(d,LabVm.JsonOptions));
                if(d.Host.HdmAddress is null)throw new InvalidDataException("Host decoder missing.");
                if(expectMailboxRejection){
                    if(expectRejected||expectedFileFailure is not null)throw new ArgumentException("Ambiguous expected rejection.");
                    string denied=vm.Shell(@"fs0:\CXLHDM.EFI",60);File.WriteAllText(Path.Combine(output,"mailbox-capacity-rejected.txt"),denied);
                    if(!denied.Contains("CXL_NATIVE_CAPACITY_GATE EXPECTED=0000000020000000 ACTUAL=0000000010000000")||!denied.Contains("CXL_NATIVE_MAILBOX_FAILED STAGE=00000007")||denied.Contains("CXL_NATIVE_HDM_LAB_ENTER")||denied.Contains("CXL_NATIVE_CAPSULE_FILE_STAGED")||denied.Contains("CXL_EFI_READER_ENTER")||denied.Contains("CXL_RAM_READY"))throw new InvalidDataException("Capacity mismatch did not stop before HDM.");
                    var registers=new List<object>();
                    foreach(var c in new[]{d.Device,d.Port,d.Host}){
                        ulong h=c.HdmAddress!.Value;uint[] values=new uint[]{4,0x10,0x14,0x18,0x1c,0x20,0x24,0x28}.Select(offset=>vm.Read32(h+offset)).ToArray();
                        registers.Add(new{c.Name,Address=h,Values=values});
                        if(values.Any(value=>value!=0))throw new InvalidDataException("Capacity rejection left programmed HDM registers.");
                    }
                    File.WriteAllText(Path.Combine(output,"mailbox-rejected-registers.json"),JsonSerializer.Serialize(registers,LabVm.JsonOptions));
                    string tree=vm.Hmp("info mtree");File.WriteAllText(Path.Combine(output,"retired-mtree.txt"),tree);
                    if(tree.Contains("cxl-direct-mapping-alias-",StringComparison.Ordinal))throw new InvalidDataException("Capacity rejection retained alias.");
                    report["ExpectedMailboxCapacityRejection"]=true;report["MailboxMismatchRejectedBeforeHdm"]=true;
                    report["GuestSignatureVerification"]=false;report["GuestAuthenticatedPayloadCopiedToSystemRam"]=false;
                    report["GuestHdmProgrammingByEfiVerified"]=false;report["ConfigurationValid"]=true;report["QemuDeviceCreated"]=true;report["Passed"]=true;return report;
                }
                if(expectedFileFailure is not null){
                    if((expectedFileFailure=="Missing")!=(capsule is null))throw new InvalidDataException("File failure input does not match Missing profile.");
                    string denied=vm.Shell(@"fs0:\CXLHDM.EFI",60);
                    File.WriteAllText(Path.Combine(output,"native-file-rejected.txt"),denied);
                    int reason=expectedFileFailure=="Missing"?5:expectedFileFailure=="Truncated"?6:7;
                    ulong read=expectedFileFailure=="Missing"?0:expectedFileFailure=="Truncated"?(ulong)capsule!.Length:(ulong)capsule!.Length-1;
                    if(!denied.Contains($"CXL_NATIVE_CAPSULE_FILE_FAILED REASON={reason:x8} READ={read:x16}")||!denied.Contains("CXL_NATIVE_HDM_FAILED")||denied.Contains("CXL_EFI_READER_ENTER")||denied.Contains("CXL_RAM_READY")||denied.Contains("CXL_NATIVE_CAPSULE_AUTHENTICATED"))throw new InvalidDataException("Unexpected native file failure disposition.");
                    foreach(var c in new[]{d.Device,d.Port,d.Host})if((vm.Read32(c.HdmAddress!.Value+0x20)&0x600)!=0)throw new InvalidDataException("File failure retained active decoder.");
                    string tree=vm.Hmp("info mtree");File.WriteAllText(Path.Combine(output,"retired-mtree.txt"),tree);
                    if(tree.Contains("cxl-direct-mapping-alias-",StringComparison.Ordinal))throw new InvalidDataException("File failure retained alias.");
                    report["Scope"]="NativeCapsuleFileFailureDiagnostic";report["ExpectedFileFailure"]=expectedFileFailure;
                    report["GuestFileInputRejected"]=true;report["GuestFileReadBytesBeforeRejection"]=read;report["GuestFileFailureReason"]=reason;
                    report["GuestSignatureVerification"]=false;report["GuestAuthenticatedPayloadCopiedToSystemRam"]=false;
                    report["GuestNativeHdmFinalRetireVerified"]=true;report["ConfigurationValid"]=true;report["QemuDeviceCreated"]=true;report["Passed"]=true;return report;
                }
                if(expectRejected){
                    string denied=vm.Shell(@"fs0:\CXLHDM.EFI",60);
                    File.WriteAllText(Path.Combine(output,"native-capsule-rejected.txt"),denied);
                    if(!denied.Contains("CXL_NATIVE_CAPSULE_FILE_STAGED")||!denied.Contains("CXL_EFI_SIGNATURE_OR_HASH_REJECTED")||!denied.Contains("CXL_NATIVE_HDM_FAILED")||denied.Contains("CXL_NATIVE_CAPSULE_AUTHENTICATED")||denied.Contains("CXL_NATIVE_HDM_MAPPED"))throw new InvalidDataException("Damaged native capsule did not fail closed.");
                    foreach(var c in new[]{d.Device,d.Port,d.Host})if((vm.Read32(c.HdmAddress!.Value+0x20)&0x600)!=0)throw new InvalidDataException("Rejected capsule left committed mapping.");
                    string tree=vm.Hmp("info mtree");File.WriteAllText(Path.Combine(output,"retired-mtree.txt"),tree);
                    if(tree.Contains("cxl-direct-mapping-alias-",StringComparison.Ordinal))throw new InvalidDataException("Rejected capsule retained alias.");
                    report["ExpectedCapsuleRejection"]=true;report["GuestDamagedCapsuleRejected"]=true;report["GuestSignatureVerification"]=false;
                    report["GuestNativeHdmFinalRetireVerified"]=true;report["ConfigurationValid"]=true;report["QemuDeviceCreated"]=true;
                    report["Passed"]=true;return report;
                }
                if(nativeRam){
                    string ramTranscript=NativeRamQualification.Run(vm,output,d,capsule!,report,nativeRamOption);
                    if(nativeRamOption=="--ram-copy")NativePciQualification.Run(vm,output,d,report,ramTranscript);
                    bool reachedSource=nativeRamOption!="--ram-copy-fail-commit";
                    report["GuestHdmProgrammingByEfiVerified"]=true;report["GuestCxlMemoryAccessible"]=reachedSource?true:null;
                    report["CapsuleStagedByNativeEfiFromFat"]=reachedSource;report["GuestSignatureVerification"]=reachedSource;report["GuestPayloadSha384Verification"]=reachedSource;
                    report["NativeCapsuleSourceBaseDiscovered"]=reachedSource;report["GuestNativeHdmFinalRetireVerified"]=true;
                    report["ConfigurationValid"]=true;report["QemuDeviceCreated"]=true;report["Passed"]=true;return report;
                }
                string prefix=vm.BeginShellUntil(@"fs0:\CXLHDM.EFI","CXL_NATIVE_HDM_MAPPED",60),suffix;
                File.WriteAllText(Path.Combine(output,"native-hdm-mapped.txt"),prefix);
                try{
                    if(!prefix.Contains("CXL_NATIVE_HDM_MAPPED HPA="))throw new InvalidDataException("Native map did not reach observer handshake.");
                    if(capsule is not null&&(!prefix.Contains("CXL_NATIVE_CAPSULE_FILE_STAGED")||!prefix.Contains("CXL_NATIVE_CAPSULE_AUTHENTICATED")||!prefix.Contains("CXL_EFI_SIGNATURE_AND_HASH_VERIFIED")))throw new InvalidDataException("Native capsule authentication did not complete.");
                    vm.Qmp("stop");try{
                        string tree=vm.Hmp("info mtree");File.WriteAllText(Path.Combine(output,"mapped-mtree.txt"),tree);
                        if(!tree.Contains("cxl-direct-mapping-alias-",StringComparison.Ordinal))throw new InvalidDataException("No alias observed under native mapping.");
                        foreach(var c in new[]{d.Device,d.Port,d.Host}){
                            ulong h=c.HdmAddress!.Value;
                            if((vm.Read32(h+0x20)&0xfff)!=0x600||vm.Read32(h+0x10)!=(uint)d.Hpa||vm.Read32(h+0x14)!=(uint)(d.Hpa>>32)||vm.Read32(h+0x18)!=0x10000000||vm.Read32(h+0x1c)!=0||vm.Read32(h+0x24)!=0||vm.Read32(h+0x28)!=0)throw new InvalidDataException("Native register readback mismatch.");
                        }
                        byte[] witness=vm.ReadBytes(d.Hpa+0x100000,8);File.WriteAllBytes(Path.Combine(output,"mapped-witness.bin"),witness);
                        if(BinaryPrimitives.ReadUInt64LittleEndian(witness)!=0x619b32d4e7a058cf)throw new InvalidDataException("Native CPU write not observed.");
                        if(capsule is not null){
                            ulong capsuleHpa=checked(d.Hpa+capsuleOffset);
                            byte[] observed=vm.ReadBytes(capsuleHpa,capsule.Length);File.WriteAllBytes(Path.Combine(output,"native-cxl-observed.bin"),observed);
                            if(!observed.AsSpan().SequenceEqual(capsule))throw new InvalidDataException("Native file-to-CXL bytes differ from input.");
                            report["CapsuleHpa"]=$"0x{capsuleHpa:x}";report["CapsuleLastByteHpa"]=$"0x{checked(capsuleHpa+(ulong)capsule.Length-1):x}";
                        }
                    }finally{vm.Qmp("cont");}
                }finally{suffix=vm.ContinueShell("c",60);}
                string transcript=prefix+suffix;File.WriteAllText(Path.Combine(output,"native-hdm-transcript.txt"),transcript);
                if(!suffix.Contains("CXL_NATIVE_HDM_PASS WRITE_BLOCKED=1 FINAL_RETIRED=1")||transcript.Contains("CXL_NATIVE_HDM_FAILED")||transcript.Contains("CXL_NATIVE_HDM_OBSERVER_TIMEOUT"))throw new InvalidDataException("Native HDM exercise failed.");
                NativePciQualification.Run(vm,output,d,report,transcript);
                if(transcript.Contains("CXL_NATIVE_CAPACITY_GATE_PASSED"))NativeMailboxDiagnostic.Verify(vm,output,d,report,transcript);
                foreach(var c in new[]{d.Device,d.Port,d.Host})if((vm.Read32(c.HdmAddress!.Value+0x20)&0x600)!=0)throw new InvalidDataException("Native final retire left committed state.");
                string finalTree=vm.Hmp("info mtree");File.WriteAllText(Path.Combine(output,"retired-mtree.txt"),finalTree);
                if(finalTree.Contains("cxl-direct-mapping-alias-",StringComparison.Ordinal))throw new InvalidDataException("Native final retire retained alias.");
                report["GuestHdmProgrammingByEfiVerified"]=true;report["GuestCxlMemoryAccessible"]=true;
                report["GuestNativeHdmSentinelWriteObserved"]=true;report["GuestNativeHdmRetireWriteBlocked"]=true;report["GuestNativeHdmFinalRetireVerified"]=true;
                report["ConfigurationValid"]=true;report["QemuDeviceCreated"]=true;
                if(capsule is not null){report["CapsuleStagedByNativeEfiFromFat"]=true;report["GuestSignatureVerification"]=true;report["GuestPayloadSha384Verification"]=true;report["NativeCapsuleSourceBaseDiscovered"]=true;report["GuestAuthenticatedPayloadCopiedToSystemRam"]=false;}
            }
            report["Passed"]=true;return report;
        }catch(Exception e){report["Error"]=e.Message;throw;}
        finally{File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(report,LabVm.JsonOptions));}
    }
}
