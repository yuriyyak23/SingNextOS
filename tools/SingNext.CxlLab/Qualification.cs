using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SingNext.Boot.Core;

namespace SingNext.CxlLab;

internal sealed record PciLocation(byte Bus,byte Device,byte Function)
{
    public override string ToString()=>$"0000:{Bus:x2}:{Device:x2}.{Function}";
}
internal sealed record Dvsec(ushort Offset,ushort Vendor,ushort Id,uint Revision,uint Length);
internal sealed record Component(string Name,ulong Address,ulong? HdmAddress,uint? InitialControl);
internal sealed record Discovery(ulong Hpa,ulong WindowBytes,PciLocation Endpoint,PciLocation RootPort,ulong Ecam,Component Host,Component Port,Component Device,IReadOnlyList<Dvsec> EndpointDvsecs,IReadOnlyList<string>? Warnings=null)
{
    public string Fingerprint=>$"{Hpa:x}:{WindowBytes:x}:{Endpoint}:{RootPort}:{Ecam:x}:{Host.Address:x}:{Port.Address:x}:{Device.Address:x}:{Port.HdmAddress:x}:{Device.HdmAddress:x}";
}

internal static class Qualification
{
    const ulong MapBytes=256UL*1024*1024;
    static byte[] Text(string s)=>Encoding.ASCII.GetBytes(s.Replace("\r","").Replace("\n","\r\n"));
    static readonly byte[] Startup=Text("echo -off\necho CXL_LAB_READY\n");
    public static object Run(string configPath,string output,string? payloadPath,int timeout,string access="MEM",int offsetMiB=0,string? efiBuilder=null,bool efiCrypto=false,string? identityDirectory=null,ulong imageGeneration=1,ulong minimumImageGeneration=1,bool efiRamCopy=false,string? efiPciImage=null,bool managedHdm=false)
    {
        if(access is not ("MEM" or "MMIO") || offsetMiB<0 || offsetMiB>240)throw new ArgumentException("Unsupported access path or volume offset.");
        if(imageGeneration==0||minimumImageGeneration==0||imageGeneration<minimumImageGeneration || (identityDirectory is not null&&!efiCrypto))throw new ArgumentException("Invalid generation policy or identity without guest crypto.");
        if(efiRamCopy&&(!efiCrypto||efiBuilder is null))throw new ArgumentException("RAM copy requires explicit guest crypto builder.");
        if(managedHdm&&!efiRamCopy)throw new ArgumentException("Integrated managed HDM profile requires guest crypto and RAM preparation.");
        ulong volumeOffset=(ulong)offsetMiB<<20;
        output=Path.GetFullPath(output);if(Directory.Exists(output)||File.Exists(output))throw new IOException("Output must be a new directory.");
        using var configDoc=JsonDocument.Parse(File.ReadAllText(configPath));var config=configDoc.RootElement;
        foreach(var (key,value) in new[]{("CxlMiB",256),("LsaMiB",1),("RamMiB",4096),("WindowMiB",4096),("CpuCount",4)})
            if(config.GetProperty(key).GetInt32()!=value)throw new InvalidDataException($"Qualified lab topology requires {key}={value}.");
        if(config.GetProperty("Accel").GetString()!="tcg")throw new InvalidDataException("Qualified lab requires TCG.");
        Directory.CreateDirectory(output);File.Copy(configPath,Path.Combine(output,"source-config.json"),false);InputEvidence.Record(output,configPath,config);
        using var signingIdentity=identityDirectory is null?null:DevelopmentIdentity.Load(identityDirectory);
        if(identityDirectory is not null)
        {
            var publicInputs=new[]{"identity.json","development-public.pem"}.Select(name=>{
                string path=Path.Combine(Path.GetFullPath(identityDirectory),name);byte[] bytes=File.ReadAllBytes(path);
                return new{Path=path,Bytes=bytes.Length,Sha256=Convert.ToHexString(SHA256.HashData(bytes))};
            }).ToArray();
            Save(output,"identity-public-inputs.json",new{Scope="Public identity configuration only; private key intentionally not copied",Files=publicInputs});
        }
        byte[] payload=payloadPath is null?LabCapsule.Fixture():File.ReadAllBytes(payloadPath);
        var capsule=LabCapsule.Create(payload,signingIdentity,imageGeneration);
        ValidateCapsuleRange(volumeOffset,(ulong)capsule.Bytes.Length,MapBytes);
        File.WriteAllBytes(Path.Combine(output,"capsule.bin"),capsule.Bytes);File.WriteAllText(Path.Combine(output,"development-public.pem"),capsule.PublicKey);
        var capsuleEvidence=LabCapsule.Verify(capsule.Bytes,capsule.PublicKey,minimumImageGeneration);
        File.WriteAllText(Path.Combine(output,"capsule.json"),JsonSerializer.Serialize(capsuleEvidence,LabVm.JsonOptions));
        var report=new Dictionary<string,object?>{{"SchemaVersion",1},{"Scope","GuestCapsuleRoundTrip"},{"FullCxlConformanceVerified",false},{"Passed",false},{"Profile","NativeWindowsRam"},{"GuestCxlMemoryAccessible",null},{"HdmConfiguredByGuest",false},{"CapsuleWrittenByGuest",false},{"CapsuleSha384VerifiedFromGuestReadback",false},{"GuestSignatureVerification",false},{"ColdRestartPersistence",false},{"RuntimeKernelExecuted",false},{"BootHandoffPerformed",false},{"PayloadKind",payloadPath is null?"Non-executable qualification fixture":"Caller supplied opaque component; executability not qualified"}};
        try
        {
            Console.WriteLine("Discovering guest PCI and CXL registers with installed EDK2...");
            Discovery discovered;
            using(var vm=new LabVm(config,Path.Combine(output,"discovery"),new Dictionary<string,byte[]>{{"startup.nsh",Startup}},timeout,useQtest:true,hostBridgeHdm:managedHdm))
            {
                discovered=Discover(vm);Save(output,"discovery.json",discovered);
                File.WriteAllText(Path.Combine(vm.DirectoryPath,"logs","shell-pci.txt"),vm.Shell("pci"));
                File.WriteAllText(Path.Combine(vm.DirectoryPath,"logs","shell-memmap.txt"),vm.Shell("memmap"));
            }
            ulong capsuleHpa=checked(discovered.Hpa+volumeOffset);
            var located=discovered with {Hpa=capsuleHpa};
            byte[] changedPayload=capsule.Bytes.AsSpan(LabCapsule.PayloadOffset,capsuleEvidence.PayloadBytes).ToArray();
            for(int i=0;i<changedPayload.Length;i++)changedPayload[i]=(i%32<16)?(byte)0:(byte)(changedPayload[i]^0xa5);
            var replacement=LabCapsule.Create(changedPayload,signingIdentity,imageGeneration);
            File.WriteAllBytes(Path.Combine(output,"replacement.bin"),replacement.Bytes);File.WriteAllText(Path.Combine(output,"replacement-public.pem"),replacement.PublicKey);
            string mapping=MapScript(discovered),write=WriteScript(located,capsule.Bytes,access),rewrite=WriteScript(located,replacement.Bytes,access);
            File.WriteAllText(Path.Combine(output,"map.nsh"),mapping);File.WriteAllText(Path.Combine(output,"qualify.nsh"),write);
            var files=new Dictionary<string,byte[]>{{"startup.nsh",Startup},{"map.nsh",Text(mapping)},{"qualify.nsh",Text(write)},{"rewrite.nsh",Text(rewrite)},{"capsule.bin",capsule.Bytes},{"replace.bin",replacement.Bytes}};
            if(efiPciImage is not null)
            {
                byte[] image=File.ReadAllBytes(efiPciImage);files.Add("CXLDISC.EFI",image);
                Save(output,"native-pci-input.json",new{Path=Path.GetFullPath(efiPciImage),Bytes=image.Length,Sha256=Convert.ToHexString(SHA256.HashData(image))});
                string metadata=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(efiPciImage))!,"build.json");
                if(File.Exists(metadata))File.Copy(metadata,Path.Combine(output,"native-pci-build.json"));
            }
            var trustChallenges=new List<(string Name,byte[] Image,string PublicKey,bool Accept)>();
            if(identityDirectory is not null)
            {
                var wrongSigner=LabCapsule.Create(payload,generation:imageGeneration);trustChallenges.Add(("foreign",wrongSigner.Bytes,wrongSigner.PublicKey,false));
                if(minimumImageGeneration>1){var stale=LabCapsule.Create(payload,signingIdentity,minimumImageGeneration-1);trustChallenges.Add(("stale",stale.Bytes,stale.PublicKey,false));}
                if(imageGeneration>minimumImageGeneration){var floor=LabCapsule.Create(payload,signingIdentity,minimumImageGeneration);trustChallenges.Add(("floor",floor.Bytes,floor.PublicKey,true));}
                foreach(var challenge in trustChallenges)
                {
                    LabCapsule.Verify(challenge.Image,challenge.PublicKey);
                    files.Add(challenge.Name+".nsh",Text(WriteScript(located,challenge.Image,access)));
                    File.WriteAllBytes(Path.Combine(output,challenge.Name+"-signed-capsule.bin"),challenge.Image);
                    File.WriteAllText(Path.Combine(output,challenge.Name+"-public.pem"),challenge.PublicKey);
                }
            }
            if(efiBuilder is not null)
            {
                var build=new System.Diagnostics.ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(string arg in new[]{"-NoProfile","-ExecutionPolicy","RemoteSigned","-File",Path.GetFullPath(efiBuilder),"-ExpectedImage",Path.Combine(output,"capsule.bin"),"-CxlBase",discovered.Hpa.ToString(),"-Offset",volumeOffset.ToString(),"-OutputDirectory",Path.Combine(output,"efi-build")})build.ArgumentList.Add(arg);
                if(efiCrypto)
                {
                    using var provisioned=ECDsa.Create();provisioned.ImportFromPem(identityDirectory is null?capsule.PublicKey:DevelopmentIdentity.ReadPublic(identityDirectory));
                    var point=provisioned.ExportParameters(false);byte[] root=new byte[129];root[0]=4;
                    point.Q.X!.CopyTo(root,1);point.Q.Y!.CopyTo(root,49);SHA256.HashData(provisioned.ExportSubjectPublicKeyInfo()).CopyTo(root,97);
                    string rootPath=Path.Combine(output,"development-trust-root.bin");File.WriteAllBytes(rootPath,root);
                    build.ArgumentList.Add("-TrustRoot");build.ArgumentList.Add(rootPath);
                    build.ArgumentList.Add("-MinImageGeneration");build.ArgumentList.Add(minimumImageGeneration.ToString(CultureInfo.InvariantCulture));
                }
                if(efiRamCopy)build.ArgumentList.Add("-RamCopy");
                using var compiler=System.Diagnostics.Process.Start(build)??throw new IOException("EFI build process failed.");
                var standard=compiler.StandardOutput.ReadToEndAsync();var error=compiler.StandardError.ReadToEndAsync();
                if(!compiler.WaitForExit(60000)){compiler.Kill();throw new TimeoutException("EFI build budget exceeded.");}
                File.WriteAllText(Path.Combine(output,"efi-build.log"),standard.GetAwaiter().GetResult()+error.GetAwaiter().GetResult());
                if(compiler.ExitCode!=0)throw new IOException("EFI build failed; see efi-build.log.");
                files.Add("CXLREAD.EFI",File.ReadAllBytes(Path.Combine(output,"efi-build","CXLREAD.EFI")));
            }
            Console.WriteLine("Programming HDM and writing capsule with guest UEFI Shell...");
            using(var vm=new LabVm(config,Path.Combine(output,"transfer"),files,timeout,hostBridgeHdm:managedHdm))
            {
                var current=Discover(vm);if(current.Fingerprint!=discovered.Fingerprint)throw new InvalidDataException("Topology changed between discovery and guest script; refusing writes.");
                if(efiPciImage is not null)NativePciQualification.Run(vm,output,current,report);
                if(vm.ReadBytes(capsuleHpa,capsule.Bytes.Length).Any(x=>x!=0))throw new InvalidDataException("Unexpected initial bytes before mapping.");
                File.WriteAllText(Path.Combine(vm.DirectoryPath,"logs","fat-list.txt"),vm.Shell(@"ls fs0:\"));
                File.WriteAllText(Path.Combine(vm.DirectoryPath,"logs","map-source.txt"),vm.Shell(@"type fs0:\map.nsh"));
                // First map only and prove RAM is zero; a no-decoder read returning zero is not sufficient.
                LabHdmHardware? managedHardware=managedHdm?new LabHdmHardware(vm,current):null;
                var transaction=managedHardware is null?null:new SingPlus.Platform.HybridCpu.Boot.TransactionalHdmMapping(managedHardware);
                var mappingReset=new BootResetSnapshot(vm.ObservedResetSequence,vm.ObservedResetSequence);
                var mappingRequest=new BootMappingRequest(current.Hpa,MapBytes,0,MapBytes,MapBytes);
                BootMappingEvidence mappingEvidence=default;
                if(transaction is null)vm.Shell(@"fs0:\map.nsh",45,"CXL_HDM_MAP_DONE");
                else{
                    var mapped=transaction.Map(mappingRequest,mappingReset);
                    if(!mapped.IsSuccess)throw new InvalidDataException("Capsule managed Map failed: "+mapped.Failure);
                    mappingEvidence=mapped.Value;
                    Save(output,"managed-mapping-evidence.json",mappingEvidence);
                    report["ManagedTransactionalMappingExecuted"]=true;
                    report["HostBridgeHdmForPassthroughEnabled"]=true;
                    report["DecoderIsolationQualified"]=false;
                    report["TransactionalHdmMappingRetirementQualified"]=false;
                }
                CheckCommitted(vm,current);
                if(vm.ReadBytes(capsuleHpa,capsule.Bytes.Length).Any(x=>x!=0))throw new InvalidDataException("CXL RAM was not initially zero; no overwrite performed.");
                string transcript=vm.Shell(@"fs0:\qualify.nsh",Math.Max(45,timeout-30),"CXL_IMAGE_END");
                if(!transcript.Contains("CXL_IMAGE_BEGIN")||!transcript.Contains("CXL_IMAGE_END"))throw new InvalidDataException("Guest script did not complete.");
                byte[] guest=ParseDump(transcript,capsuleHpa,capsule.Bytes.Length);
                if(!guest.AsSpan().SequenceEqual(capsule.Bytes))throw new InvalidDataException("Guest readback differs from source capsule.");
                if(!vm.ReadBytes(capsuleHpa,capsule.Bytes.Length).AsSpan().SequenceEqual(guest))throw new InvalidDataException("QEMU physical-address observer mismatch.");
                File.WriteAllBytes(Path.Combine(output,"capsule-from-guest.bin"),guest);
                Save(output,"guest-readback-verification.json",LabCapsule.Verify(guest,capsule.PublicKey));
                if(efiBuilder is not null)
                {
                    string accepted=vm.Shell(@"fs0:\CXLREAD.EFI",45);
                    File.WriteAllText(Path.Combine(output,"efi-reader-match.txt"),accepted);
                    if(!accepted.Contains("CXL_EFI_READER_ENTER")||!accepted.Contains("CXL_EFI_IMAGE_MATCH")||accepted.Contains("CXL_EFI_IMAGE_MISMATCH"))throw new InvalidDataException("Guest EFI byte identity check failed.");
                    if(efiCrypto && (!accepted.Contains("CXL_EFI_SHA384_KAT_PASSED")||!accepted.Contains("CXL_EFI_P384_KAT_AND_NEGATIVES_PASSED")||!accepted.Contains("CXL_EFI_SIGNATURE_AND_HASH_VERIFIED")))throw new InvalidDataException("Guest EFI crypto verification failed.");
                    ulong address=checked(capsuleHpa+LabCapsule.PayloadOffset);
                    ulong original=BinaryPrimitives.ReadUInt64LittleEndian(guest.AsSpan(LabCapsule.PayloadOffset));
                    try
                    {
                        vm.Shell($"mm {address:x} {original^1:x} -w 8 -MEM -n");
                        if(BinaryPrimitives.ReadUInt64LittleEndian(vm.ReadBytes(address,8))!=(original^1))throw new InvalidDataException("Payload mutation was not applied.");
                        string rejected=vm.Shell(@"fs0:\CXLREAD.EFI",45);
                        File.WriteAllText(Path.Combine(output,"efi-reader-corrupt.txt"),rejected);
                        if(!rejected.Contains("CXL_EFI_IMAGE_MISMATCH")||rejected.Contains("CXL_EFI_IMAGE_MATCH"))throw new InvalidDataException("Guest EFI accepted damaged bytes.");
                    }
                    finally{vm.Shell($"mm {address:x} {original:x} -w 8 -MEM -n");}
                    report["GuestEfiReaderExecuted"]=true;report["GuestEfiByteIdentityVerified"]=true;report["GuestEfiDamagedImageRejected"]=true;
                    if(efiCrypto)
                    {
                        ulong signatureAddress=checked(capsuleHpa+LabCapsule.SignatureOffset);
                        ulong signatureWord=BinaryPrimitives.ReadUInt64LittleEndian(guest.AsSpan(LabCapsule.SignatureOffset));
                        try
                        {
                            vm.Shell($"mm {signatureAddress:x} {signatureWord^1:x} -w 8 -MEM -n");
                            if(BinaryPrimitives.ReadUInt64LittleEndian(vm.ReadBytes(signatureAddress,8))!=(signatureWord^1))throw new InvalidDataException("Signature mutation was not applied.");
                            string badSignature=vm.Shell(@"fs0:\CXLREAD.EFI",45);
                            File.WriteAllText(Path.Combine(output,"efi-reader-bad-signature.txt"),badSignature);
                            if(!badSignature.Contains("CXL_EFI_SIGNATURE_OR_HASH_REJECTED")||badSignature.Contains("CXL_EFI_SIGNATURE_AND_HASH_VERIFIED"))throw new InvalidDataException("Guest EFI accepted invalid signature.");
                        }
                        finally{vm.Shell($"mm {signatureAddress:x} {signatureWord:x} -w 8 -MEM -n");}
                        report["GuestSignatureVerification"]=true;report["GuestPayloadSha384Verification"]=true;report["GuestSha384KnownAnswerPassed"]=true;report["GuestInvalidSignatureRejected"]=true;
                        report["GuestP384Rfc6979KnownAnswersPassed"]=true;report["GuestP384PrimitiveNegativeCasesPassed"]=9;
                        foreach(var (label,relative,mask) in new[]{("header",0,1UL),("manifest-length",LabCapsule.ManifestOffset+8,1UL<<32),("key-id",LabCapsule.ManifestOffset+168,1UL)})
                        {
                            ulong target=checked(capsuleHpa+(ulong)relative);
                            ulong word=BinaryPrimitives.ReadUInt64LittleEndian(guest.AsSpan(relative));
                            try
                            {
                                vm.Shell($"mm {target:x} {word^mask:x} -w 8 -MEM -n");
                                if(BinaryPrimitives.ReadUInt64LittleEndian(vm.ReadBytes(target,8))!=(word^mask))throw new InvalidDataException("Guest mutation was not applied: "+label);
                                string denied=vm.Shell(@"fs0:\CXLREAD.EFI",45);
                                File.WriteAllText(Path.Combine(output,"efi-reject-"+label+".txt"),denied);
                                if(!denied.Contains("CXL_EFI_SIGNATURE_OR_HASH_REJECTED")||denied.Contains("CXL_EFI_SIGNATURE_AND_HASH_VERIFIED"))throw new InvalidDataException("Guest EFI accepted malformed "+label);
                            }
                            finally{vm.Shell($"mm {target:x} {word:x} -w 8 -MEM -n");}
                        }
                        report["GuestHeaderLengthAndKeyIdNegativesPassed"]=true;
                        report["GuestEfiByteIdentityVerified"]=false;report["GuestTrustProfile"]="Compiled development P384 public key, separate from CXL; generated for this run, no production provisioning";
                        foreach(var challenge in trustChallenges)
                        {
                            try
                            {
                                string transport=vm.Shell($@"fs0:\{challenge.Name}.nsh",Math.Max(45,timeout-30),"CXL_IMAGE_END");
                                byte[] received=ParseDump(transport,capsuleHpa,challenge.Image.Length);
                                if(!received.AsSpan().SequenceEqual(challenge.Image))throw new InvalidDataException("Trust challenge was not written: "+challenge.Name);
                                string result=vm.Shell(@"fs0:\CXLREAD.EFI",45);
                                File.WriteAllText(Path.Combine(output,"efi-trust-"+challenge.Name+".txt"),result);
                                bool success=result.Contains("CXL_EFI_SIGNATURE_AND_HASH_VERIFIED"),failure=result.Contains("CXL_EFI_SIGNATURE_OR_HASH_REJECTED");
                                if(success!=challenge.Accept || failure==challenge.Accept)throw new InvalidDataException("Guest trust policy challenge failed: "+challenge.Name);
                            }
                            finally
                            {
                                string restored=vm.Shell(@"fs0:\qualify.nsh",Math.Max(45,timeout-30),"CXL_IMAGE_END");
                                if(!ParseDump(restored,capsuleHpa,capsule.Bytes.Length).AsSpan().SequenceEqual(capsule.Bytes))throw new InvalidDataException("Original capsule restore failed.");
                            }
                            report["GuestTrustChallenge_"+challenge.Name]=true;
                        }
                        if(identityDirectory is not null)
                        {
                            report["DevelopmentIdentityReused"]=true;report["GuestTrustProfile"]="Reusable separately stored development public key compiled into EFI; static generation floor, no production provisioning";
                            report["DevelopmentTrustSpkiSha256"]=Convert.ToHexString(SHA256.HashData(signingIdentity!.ExportSubjectPublicKeyInfo()));
                            report["ImageGeneration"]=imageGeneration;report["CompiledMinimumImageGeneration"]=minimumImageGeneration;
                            Save(output,"development-policy.json",new{Scope="Static compiled development policy, not durable anti-rollback",PublicKeySha256=report["DevelopmentTrustSpkiSha256"],ImageGeneration=imageGeneration,MinimumImageGeneration=minimumImageGeneration});
                        }
                    }
                }
                if(efiRamCopy)RamCopyQualification.Run(vm,output,capsule.Bytes,capsuleEvidence.PayloadBytes,report);
                using(var anchor=ECDsa.Create())
                {
                    anchor.ImportFromPem(capsule.PublicKey);
                    byte[] pin=SHA256.HashData(anchor.ExportSubjectPublicKeyInfo());
                    var source=new ShellCapsuleSource(vm,current);
                    var admitted=CapsuleAdmission.Verify(source,volumeOffset,capsule.Bytes.Length,capsule.PublicKey,pin,minimumImageGeneration);
                    Save(output,"shell-source-admission.json",new{Evidence=admitted,source.ReadCount,TrustAnchorSha256=Convert.ToHexString(pin),TrustAnchorOrigin="Host-generated development key; not independent guest provisioning",GenerationScope="QMP observed reset sequence and sampled HDM registers; transient mapping changes and DMA not excluded",GuestCryptography=false});
                    report["HostAdmissionFromBoundedGuestReadsVerified"]=true;
                }
                report["HdmConfiguredByGuest"]=true;report["CapsuleWrittenByGuest"]=true;report["CapsuleSha384VerifiedFromGuestReadback"]=true;report["GuestCxlMemoryAccessible"]=true;
                report["SignatureVerifiedOnHostFromGuestReadback"]=true;report["QtestAttachedDuringGuestTransfer"]=false;report["GuestDmemLabelsLow32Normalized"]=true;report["Hpa"]=$"0x{capsuleHpa:x}";report["VolumeOffsetMiB"]=offsetMiB;report["GuestWriteAccess"]=access=="MEM"?"CPU memory stores (UEFI Shell -MEM)":"PCI Root Bridge I/O protocol (-MMIO)";report["GuestReadAccess"]="CPU memory loads (UEFI Shell dmem)";report["PhysicalReadObserver"]="QEMU physical address space, same emulated route; not independent CXL hardware";report["EndpointBdf"]=current.Endpoint.ToString();report["PciSnapshotWarnings"]=current.Warnings;report["UsedDvsecWireContractsValidated"]=true;report["FullPciLayoutConsistent"]=current.Warnings is null||current.Warnings.Count==0;report["DecoderRetirementQualified"]=false;report["GuestTrustRootIndependentlyProvisioned"]=false;report["GuestAntiRollbackVerified"]=false;report["BootIntegrationReady"]=false;report["Capsule"]=capsuleEvidence;
                string rewriteText=vm.Shell(@"fs0:\rewrite.nsh",Math.Max(45,timeout-30),"CXL_IMAGE_END");
                byte[] replaced=ParseDump(rewriteText,capsuleHpa,replacement.Bytes.Length);
                if(!replaced.AsSpan().SequenceEqual(replacement.Bytes)||!vm.ReadBytes(capsuleHpa,replaced.Length).AsSpan().SequenceEqual(replaced))throw new InvalidDataException("Second capsule retained stale bytes.");
                File.WriteAllBytes(Path.Combine(output,"replacement-from-guest.bin"),replaced);
                Save(output,"replacement-verification.json",LabCapsule.Verify(replaced,replacement.PublicKey));
                report["SecondCapsuleOverwriteVerified"]=true;report["ZeroWordsExplicitlyWritten"]=true;
                CheckCommitted(vm,current);
                // Decoder gate test: a write made while EP is uncommitted must NOT reach DPA.
                ulong sentinel=current.Hpa+0x00f00000;const ulong first=0x619b32d4e7a058cf,second=0xbfa0314965d2e807;
                vm.Shell($"mm {sentinel:x} {first:x} -w 8 -MMIO -n");
                if(BinaryPrimitives.ReadUInt64LittleEndian(vm.ReadBytes(sentinel,8))!=first)throw new InvalidDataException("Sentinel write failed.");
                if(transaction is not null){
                    if(transaction.Retire(mappingEvidence,mappingReset)!=BootFailure.None)throw new InvalidDataException("Capsule managed Retire failed.");
                    string retiredTree=vm.Hmp("info mtree");
                    File.WriteAllText(Path.Combine(output,"managed-retired-mtree.txt"),retiredTree);
                    if(retiredTree.Contains("cxl-direct-mapping-alias-",StringComparison.Ordinal))throw new InvalidDataException("Alias retained after capsule managed Retire.");
                    vm.Shell($"mm {sentinel:x} {second:x} -w 8 -MEM -n");
                    var remapped=transaction.Map(mappingRequest,mappingReset);
                    if(!remapped.IsSuccess)throw new InvalidDataException("Capsule managed remap failed.");
                    bool blocked=BinaryPrimitives.ReadUInt64LittleEndian(vm.ReadBytes(sentinel,8))==first;
                    if(!blocked||!vm.ReadBytes(capsuleHpa,replacement.Bytes.Length).AsSpan().SequenceEqual(replacement.Bytes))throw new InvalidDataException("Retirement gate or retained capsule content failed.");
                    if(transaction.Retire(remapped.Value,mappingReset)!=BootFailure.None)throw new InvalidDataException("Final capsule managed Retire failed.");
                    report["ManagedRetireCpuWriteBlocked"]=true;
                    report["ManagedRetireCapsulePreserved"]=true;
                    report["ManagedMappingGenerationAdvanced"]=remapped.Value.BootMappingGeneration>mappingEvidence.BootMappingGeneration;
                    report["ManagedFinalRetireSucceeded"]=true;
                    Save(output,"managed-hdm-operations.json",managedHardware!.Operations);
                    report["ManagedHdmScope"]="Host-managed existing transaction contract; guest Shell MMIO; not native SingBoot or DMA/cache isolation.";
                }else{
                ulong ctrl=current.Device.HdmAddress!.Value+0x20;
                vm.Shell($"mm {ctrl:x} 1000 -w 4 -MMIO -n");if((vm.Read32(ctrl)&0x400)!=0)throw new InvalidDataException("Endpoint failed to uncommit.");
                vm.Shell($"mm {sentinel:x} {second:x} -w 8 -MMIO -n");
                vm.Shell($"mm {ctrl:x} 1200 -w 4 -MMIO -n");CheckCommitted(vm,current);
                ulong observed=BinaryPrimitives.ReadUInt64LittleEndian(vm.ReadBytes(sentinel,8));
                report["DecoderGateNegativeTestPassed"]=observed==first;
                report["DecoderGateDiagnostic"]=new{Original=$"0x{first:x}",AttemptWhileUncommitted=$"0x{second:x}",ObservedAfterRecommit=$"0x{observed:x}",QtestAttached=false};
                if(observed!=first) {
                    report["Limitations"]=new[]{"Guest write reached CXL backing while endpoint COMMITTED was clear on this local QEMU build; decoder disable enforcement is not qualified."};
                    Console.WriteLine("Diagnostic limitation: uncommitted endpoint did not block sentinel write; retained in result, not treated as CXL compliance success.");
                }
                }
                File.WriteAllText(Path.Combine(vm.DirectoryPath,"logs","memory-tree.txt"),vm.Hmp("info mtree"));
                vm.Qmp("screendump",new{filename="guest-cxl.ppm"});
            }
            Console.WriteLine("Checking cold restart: remap new CXL RAM and reject absent capsule...");
            using(var vm=new LabVm(config,Path.Combine(output,"cold-restart"),new Dictionary<string,byte[]>{{"startup.nsh",Startup},{"map.nsh",Text(mapping)}},timeout,hostBridgeHdm:managedHdm))
            {
                var current=Discover(vm);if(current.Fingerprint!=discovered.Fingerprint)throw new InvalidDataException("Cold topology mismatch.");
                vm.Shell(@"fs0:\map.nsh",45,"CXL_HDM_MAP_DONE");CheckCommitted(vm,current);
                byte[] guest=ParseDump(vm.Shell($"dmem {capsuleHpa:x} {capsule.Bytes.Length:x}",45),capsuleHpa,capsule.Bytes.Length);
                if(guest.Any(x=>x!=0))throw new InvalidDataException("New RAM retained unexpected capsule bytes.");
                bool rejected=false;try{LabCapsule.Verify(guest,capsule.PublicKey);}catch(InvalidDataException){rejected=true;}
                if(!rejected)throw new InvalidDataException("Absent capsule was accepted.");
                report["ColdRestartLossVerified"]=true;report["AbsentCapsuleRejected"]=true;
                File.WriteAllBytes(Path.Combine(output,"cold-restart-readback.bin"),guest);
            }
            report["ConfigurationValid"]=true;report["QemuDeviceCreated"]=true;report["Passed"]=true;
            Save(output,"result.json",report);return report;
        }
        catch(Exception e){report["Error"]=e.Message;Save(output,"result.json",report);throw;}
    }
    static void Save(string root,string name,object value)=>File.WriteAllText(Path.Combine(root,name),JsonSerializer.Serialize(value,LabVm.JsonOptions));
    static PciLocation FindPci(JsonElement root,string id)
    {
        PciLocation? found=null;
        void Walk(JsonElement e)
        {
            if(e.ValueKind==JsonValueKind.Array){foreach(var x in e.EnumerateArray())Walk(x);return;}
            if(e.ValueKind!=JsonValueKind.Object)return;
            if(e.TryGetProperty("qdev_id",out var qid)&&qid.GetString()==id){if(found is not null)throw new InvalidDataException("Duplicate QMP PCI identity.");found=new(e.GetProperty("bus").GetByte(),e.GetProperty("slot").GetByte(),e.GetProperty("function").GetByte());}
            foreach(var p in e.EnumerateObject())if(p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)Walk(p.Value);
        }
        Walk(root);return found??throw new InvalidDataException("PCI device not found: "+id);
    }
    public static Discovery Discover(LabVm vm)
    {
        var pci=vm.Qmp("query-pci");var ep=FindPci(pci,"cxl0");var rp=FindPci(pci,"cxl-rp0");
        uint pciex=vm.PciRead(0,0,0,0x60);if((pciex&1)==0)throw new InvalidDataException("ECAM disabled.");ulong ecam=(pciex&0xf0000000u)|((ulong)vm.PciRead(0,0,0,0x64)<<32);
        byte[] raw=vm.ReadBytes(ecam+((ulong)ep.Bus<<20)+((ulong)ep.Device<<15)+((ulong)ep.Function<<12),4096);
        File.WriteAllBytes(Path.Combine(vm.DirectoryPath,"logs","endpoint-pci-config.bin"),raw);
        var compatible=DiscoveryCompatibility.Decode(raw,new BootPciAddress(0,ep.Bus,ep.Device,ep.Function));
        var dvsecs=compatible.Headers;
        File.WriteAllText(Path.Combine(vm.DirectoryPath,"logs","dvsec-contract.json"),JsonSerializer.Serialize(new{Parser="SingPlus.Platform.HybridCpu.Boot.CxlPciDvsecParser",DeviceHeaders=dvsecs,OpaquePayloadWarnings=compatible.Warnings,ComponentBase=compatible.ComponentBase,Scope="Wire header/capability compatibility, not production boot trust"},LabVm.JsonOptions));
        byte[] portRaw=vm.ReadBytes(ecam+((ulong)rp.Bus<<20)+((ulong)rp.Device<<15)+((ulong)rp.Function<<12),4096);
        File.WriteAllBytes(Path.Combine(vm.DirectoryPath,"logs","rootport-pci-config.bin"),portRaw);
        var portCompatible=DiscoveryCompatibility.Decode(portRaw,new BootPciAddress(0,rp.Bus,rp.Device,rp.Function),requireMemoryDevice:false);
        string tree=vm.Hmp("info mtree");
        (ulong Start,ulong Bytes) Region(string name){var a=Regex.Matches(tree,@"([0-9a-fA-F]{16})-([0-9a-fA-F]{16}) \([^\r\n]+\): "+Regex.Escape(name)+@"\r?$",RegexOptions.Multiline).Select(m=>(Start:ulong.Parse(m.Groups[1].Value,NumberStyles.HexNumber),End:ulong.Parse(m.Groups[2].Value,NumberStyles.HexNumber))).Distinct().ToArray();if(a.Length!=1)throw new InvalidDataException("Ambiguous memory region: "+name);return(a[0].Start,a[0].End-a[0].Start+1);}
        var window=Region("cxl-fixed-memory-region");if(window.Bytes!=4096UL*1024*1024||window.Start%MapBytes!=0)throw new InvalidDataException("Unexpected CFMW geometry.");
        Component ReadComponent(string name,ulong address,bool required)
        {
            ulong cache=address+0x1000;uint header=vm.Read32(cache);if((header&0xffff)!=1)throw new InvalidDataException("Invalid component capability header.");int count=(int)(header>>24);if(count>32)throw new InvalidDataException("Component capability array bound exceeded.");
            ulong? hdm=null;for(int i=1;i<=count;i++){uint cap=vm.Read32(cache+(uint)i*4);if((cap&0xffff)==5){if(hdm is not null)throw new InvalidDataException("Duplicate HDM capability.");hdm=cache+(cap>>20);}}
            if(required&&hdm is null)throw new InvalidDataException("Required HDM capability absent: "+name);
            uint? control=hdm is null?null:vm.Read32(hdm.Value+0x20);if(control is not null&&(control.Value&0x700)!=0)throw new InvalidDataException("Decoder already active or locked; refusing overwrite.");
            return new(name,address,hdm,control);
        }
        return new(window.Start,window.Bytes,ep,rp,ecam,ReadComponent("HostBridge",Region("pxb-cxl-host").Start,false),ReadComponent("RootPort",portCompatible.ComponentBase,true),ReadComponent("Endpoint",compatible.ComponentBase,true),dvsecs,compatible.Warnings.Concat(portCompatible.Warnings).ToArray());
    }
    static string MapScript(Discovery d)
    {
        var s=new StringBuilder("echo -off\n");
        foreach(var c in new[]{d.Device,d.Port,d.Host})if(c.HdmAddress is ulong h)
        {
            foreach(var (offset,value) in new (uint,uint)[]{(0x10,(uint)d.Hpa),(0x14,(uint)(d.Hpa>>32)),(0x18,(uint)MapBytes),(0x1c,0),(0x24,0),(0x28,0),(4,2),(0x20,0x1200)})s.AppendLine($"mm {h+offset:x} {value:x} -w 4 -MMIO -n");
        }
        s.AppendLine("echo CXL_HDM_MAP_DONE");return s.ToString();
    }
    static string WriteScript(Discovery d,byte[] image,string access)
    {
        var s=new StringBuilder("echo -off\necho CXL_WRITE_BEGIN\n");
        for(int i=0;i<image.Length;i+=8){ulong word=BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(i));s.AppendLine($"mm {d.Hpa+(uint)i:x} {word:x} -w 8 -{access} -n");}
        s.AppendLine("echo CXL_IMAGE_BEGIN");s.AppendLine($"dmem {d.Hpa:x} {image.Length:x}");s.AppendLine("echo CXL_IMAGE_END");return s.ToString();
    }
    static void CheckCommitted(LabVm vm,Discovery d)
    {
        foreach(var c in new[]{d.Device,d.Port,d.Host})if(c.HdmAddress is ulong h)
        {
            uint ctrl=vm.Read32(h+0x20);ulong b=((ulong)vm.Read32(h+0x14)<<32)|(vm.Read32(h+0x10)&0xf0000000u),size=((ulong)vm.Read32(h+0x1c)<<32)|(vm.Read32(h+0x18)&0xf0000000u);
            if((ctrl&0x400)==0||(ctrl&0x800)!=0||(ctrl&0xff)!=0||(vm.Read32(h+4)&2)==0||b!=d.Hpa||size!=MapBytes)throw new InvalidDataException("HDM commit/readback mismatch: "+c.Name);
        }
    }
    internal static void ValidateCapsuleRange(ulong offset,ulong bytes,ulong capacity)
    {
        if(bytes==0 || offset>capacity || bytes>capacity-offset)
            throw new InvalidDataException("CapsuleOffset + CapsuleImageBytes exceeds backing capacity.");
    }
    internal static void TestDumpParser()
    {
        GuestMemoryDump.SelfTest();
        ValidateCapsuleRange(MapBytes-8,8,MapBytes);
        ValidateCapsuleRange(0,MapBytes,MapBytes);
        foreach(var (offset,bytes) in new[]{(MapBytes-7,8UL),(MapBytes,1UL),(ulong.MaxValue,2UL),(1UL,ulong.MaxValue),(0UL,0UL)})
        {
            bool rejected=false;try{ValidateCapsuleRange(offset,bytes,MapBytes);}catch(InvalidDataException){rejected=true;}
            if(!rejected)throw new Exception("Capsule range bounds rejection failed.");
        }
        Console.WriteLine("Capsule range: exact-end accepted; 5 bounds/overflow cases rejected.");
    }
    internal static byte[] ParseDump(string text,ulong address,int length)=>GuestMemoryDump.Parse(text,address,length);
}
