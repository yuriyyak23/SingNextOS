using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingNext.CxlLab;

internal static class NativeRamQualification
{
    public static string Run(LabVm vm,string output,Discovery discovery,byte[] capsule,Dictionary<string,object?> report,string mode="--ram-copy")
    {
        if(mode is not ("--ram-copy" or "--ram-copy-fail-copy" or "--ram-copy-fail-info" or "--ram-copy-fail-alloc" or "--ram-copy-reset-held" or "--ram-copy-fail-retire" or "--ram-copy-no-ack" or "--ram-copy-fail-commit" or "--ram-copy-fail-clear"))throw new ArgumentException("Unknown native RAM mode.");
        bool resetHeld=mode=="--ram-copy-reset-held",noAck=mode=="--ram-copy-no-ack",clearFailure=mode=="--ram-copy-fail-clear",positive=mode is "--ram-copy" or "--ram-copy-reset-held" or "--ram-copy-no-ack" or "--ram-copy-fail-clear";
        report["NativeRamMode"]=mode;report["ExpectedRamFailure"]=!positive;
        if(mode=="--ram-copy-fail-alloc"){
            string text=vm.Shell(@"fs0:\CXLHDM.EFI "+mode,60);
            File.WriteAllText(Path.Combine(output,"native-ram-allocation-rejection.txt"),text);
            if(!text.Contains("CXL_EFI_SIGNATURE_AND_HASH_VERIFIED")||!text.Contains("CXL_RAM_ALLOC_FAILURE_INJECTED")||!text.Contains("CXL_NATIVE_HDM_FAILED")||text.Contains("CXL_RAM_READY")||text.Contains("CXL_NATIVE_CAPSULE_AUTHENTICATED"))throw new InvalidDataException("Native allocation injection did not fail before READY.");
            AssertRetired(vm,discovery,output);
            report["NativeAllocationFailureBeforeAllocateVerified"]=true;report["GuestAuthenticatedPayloadCopiedToSystemRam"]=false;
            report["GuestRamFailureCleanupObserved"]=true;report["GuestRamFailureRejected"]=true;
            return text;
        }
        vm.Qmp("query-status");
        using var preparation=new NativePreparationSession(vm,discovery,vm.ObservedResetSequence);
        string prefix="",suffix="";
        bool acknowledgeOldApplication=!resetHeld;
        LabRamPreparationWitness? witness=null;
        try{
            prefix=preparation.BeginPreparation(vm,resetHeld||noAck?"--ram-copy":mode);
            File.WriteAllText(Path.Combine(output,"native-ram-ready.txt"),prefix);
            if(mode=="--ram-copy-fail-commit"){
                acknowledgeOldApplication=false;
                if(!prefix.Contains("CXL_NATIVE_HDM_PARTIAL_COMMIT_INJECTED COMMITTED_DECODERS=1")||
                   !prefix.Contains("CXL_NATIVE_HDM_FAILED")||prefix.Contains("CXL_EFI_READER_ENTER")||
                   prefix.Contains("CXL_NATIVE_CAPSULE_FILE_STAGED")||prefix.Contains("CXL_RAM_READY"))
                    throw new InvalidDataException("Partial commit did not fail before source access/RAM allocation.");
                NativeOwnerEvidence.Observe(vm,preparation,discovery,prefix,capsule,output,report,true,false,true);
                preparation.Quarantine(SingNext.Boot.Core.BootFailure.AmbiguousState,"Partial native commit: original mapping record retained.");
                AssertRetired(vm,discovery,output);
                report["NativePartialCommitQuarantinedBeforeSourceRead"]=true;
                report["GuestRamFailureRejected"]=true;
                report["GuestAuthenticatedPayloadCopiedToSystemRam"]=false;
                report["GuestRamReleaseCleared"]=false;
                return prefix;
            }
            if(mode=="--ram-copy-fail-retire"){
                acknowledgeOldApplication=false;
                if(!prefix.Contains("CXL_RAM_QUARANTINED RETAINED=1 FREE_ATTEMPTS=0") ||
                    prefix.Contains("CXL_RAM_READY") || prefix.Contains("CXL_RAM_RELEASED") ||
                    !prefix.Contains("CXL_NATIVE_HDM_FAILED"))throw new InvalidDataException("Partial retire did not retain RAM without publication/free.");
                NativeOwnerEvidence.Observe(vm,preparation,discovery,prefix,capsule,output,report,true,true);
                preparation.Quarantine(SingNext.Boot.Core.BootFailure.AmbiguousState,"Native partial retire: allocation retained until exact owned VM teardown.");
                AssertRetired(vm,discovery,output); // local CPU route only, never upgrades the quarantine
                report["NativePartialRetireQuarantined"]=true;
                report["GuestRamFailureRejected"]=true;
                report["GuestRamFailureCleanupObserved"]=false;
                report["GuestAuthenticatedPayloadCopiedToSystemRam"]=false;
                report["GuestRamReleaseCleared"]=false;
                return prefix;
            }
            if(!prefix.Contains("CXL_EFI_SIGNATURE_AND_HASH_VERIFIED")||!prefix.Contains("CXL_NATIVE_RAM_SOURCE_RETIRED"))throw new InvalidDataException("Native RAM preparation lacks authentication or source retirement.");
            if(prefix.Contains("CXL_NATIVE_CAPACITY_GATE_PASSED")){
                NativePciQualification.Run(vm,output,discovery,report,prefix);
                NativeMailboxDiagnostic.Verify(vm,output,discovery,report,prefix);
            }
            string expectedState=positive?"VALID":"CLEARED";
            if(!positive&&!prefix.Contains(mode=="--ram-copy-fail-copy"?"CXL_RAM_COPY_HASH_REJECTED":"CXL_RAM_BOOTINFO_CRC_REJECTED"))throw new InvalidDataException("Wrong native RAM failure disposition.");
            var m=Regex.Match(prefix,@"CXL_RAM_READY STATE="+expectedState+@" RAM=([0-9a-f]{16}) PAYLOAD=([0-9a-f]{16}) INFO=([0-9a-f]{16}) PAGES=([0-9a-f]{16})");
            if(!m.Success)throw new InvalidDataException("Native RAM allocation not held valid.");
            ulong Value(int n)=>ulong.Parse(m.Groups[n].Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
            ulong address=Value(1),length=Value(2),info=Value(3),pages=Value(4);
            ulong expected=BinaryPrimitives.ReadUInt64LittleEndian(capsule.AsSpan(4096+216));
            if(length!=expected||length==0||length>65536||capsule.Length<8192+(long)length||pages!=(length+4095)/4096+1||address%4096!=0||address>ulong.MaxValue-pages*4096||info!=address+(pages-1)*4096)throw new InvalidDataException("Native RAM allocation geometry invalid.");
            ulong snapshotReset=preparation.ResetSequence;
            vm.Qmp("stop");try{
                foreach(var c in new[]{discovery.Device,discovery.Port,discovery.Host})if((vm.Read32(c.HdmAddress!.Value+0x20)&0x600)!=0)throw new InvalidDataException("CXL source still committed during RAM observation.");
                string tree=vm.Hmp("info mtree"),flat=vm.Hmp("info mtree -f");
                File.WriteAllText(Path.Combine(output,"native-ram-retired-mtree.txt"),tree);File.WriteAllText(Path.Combine(output,"native-ram-flatview.txt"),flat);
                if(tree.Contains("cxl-direct-mapping-alias-",StringComparison.Ordinal))throw new InvalidDataException("CXL alias retained while RAM held.");
                var views=Regex.Split(flat,@"(?m)^FlatView #").Where(x=>x.Contains("AS \"cpu-memory-0\", root: system",StringComparison.Ordinal)).ToArray();
                if(views.Length!=1||!Regex.Matches(views[0],@"(?m)^\s*([0-9a-f]{16})-([0-9a-f]{16}) \(prio \d+, ram\): pc\.ram(?:\s|$)").Any(row=>{
                    ulong start=ulong.Parse(row.Groups[1].Value,NumberStyles.HexNumber),end=ulong.Parse(row.Groups[2].Value,NumberStyles.HexNumber);
                    return start<=address&&address<=end&&pages*4096-1<=end-address;
                }))throw new InvalidDataException("Native destination not inside CPU System RAM.");
                if(preparation.ObserveMappedAndLocallyRetired(vm,discovery,true)!=SingNext.Boot.Core.BootFailure.None)
                    throw new InvalidDataException("Native preparation owner rejected the local retire receipt.");
                preparation.BindOwnedRam(vm,address,pages);
                NativeOwnerEvidence.Observe(vm,preparation,discovery,prefix,capsule,output,report,false,true);
                if(preparation.CheckCurrent(null!)==SingNext.Boot.Core.BootFailure.None || preparation.OwnedRamReadEffects!=0)
                    throw new InvalidDataException("Foreign VM identity was accepted.");
                report["NativePreparationAbsentSessionDenied"]=true;
                // A foreign address must have zero read effects and cannot displace the original allocation.
                try { preparation.ReadOwnedRam(vm,address+4096,pages); throw new InvalidOperationException("Foreign address admitted."); }
                catch(InvalidDataException) { if(preparation.OwnedRamReadEffects!=0)throw; }
                report["NativePreparationForeignAddressDeniedBeforeRead"]=true;
                byte[] owned=preparation.ReadOwnedRam(vm,address,pages);File.WriteAllBytes(Path.Combine(output,"native-owned-ram.bin"),owned);
                if(positive){
                if(!owned.AsSpan(0,(int)length).SequenceEqual(capsule.AsSpan(8192,(int)length)))throw new InvalidDataException("Native RAM payload differs after CXL retirement.");
                int bootInfoBytes=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(owned.AsSpan((int)(info-address)+12)));
                byte[] bootInfo=owned.AsSpan((int)(info-address),bootInfoBytes).ToArray();var parsed=HybridBootInfoCodec.Parse(bootInfo,new HashSet<BootEvidenceKind>{BootEvidenceKind.Security});
                if(!parsed.IsSuccess) throw new InvalidDataException("Native BootInfo codec failed: "+parsed.Failure+" / "+parsed.Detail);
                if(bootInfoBytes!=160) throw new InvalidDataException("Native BootInfo size is not the expected security envelope.");
                if(parsed.Value!.Records.Count!=1) throw new InvalidDataException("Native BootInfo record count is invalid.");
                if(owned.AsSpan((int)length,(int)(info-address-length)).IndexOfAnyExcept((byte)0)>=0||owned.AsSpan((int)(info-address)+bootInfoBytes).IndexOfAnyExcept((byte)0)>=0)throw new InvalidDataException("Native BootInfo allocation padding invalid.");
                File.WriteAllBytes(Path.Combine(output,"native-hybrid-boot-info.bin"),bootInfo);
                BootInfoConsumerQualification.VerifyDevelopmentEvidence(bootInfo,output,report);
                if(resetHeld){
                    witness=new LabRamPreparationWitness(vm,owned,snapshotReset);
                    File.WriteAllText(Path.Combine(output,"pre-reset-ram-witness.json"),System.Text.Json.JsonSerializer.Serialize(new{Scope="Host lab observation, not guest reset authority",witness.Id,witness.ResetSequence,witness.SnapshotSha256,Address=address,Pages=pages,InfoAddress=info},LabVm.JsonOptions));
                }
                }else if(owned.Any(x=>x!=0))throw new InvalidDataException("Failed native preparation retained bytes in allocation.");
                var publication=positive
                    ? preparation.PublishVerifiedOwnedRam(vm,address,pages,owned)
                    : preparation.ObserveClearedRamForCleanup(vm,address,pages,owned);
                if(publication!=SingNext.Boot.Core.BootFailure.None)
                    throw new InvalidDataException(positive
                        ? "Native preparation owner rejected verified System RAM publication."
                        : "Native preparation owner rejected cleared RAM failure cleanup.");
                report["NativePreparationSessionId"]=preparation.Id;
                report["NativePreparationResetSequence"]=preparation.ResetSequence;
                report["NativePreparationMappingReceiptGeneration"]=preparation.MappingReceiptGeneration;
                report["NativePreparationSnapshotSha256"]=preparation.SnapshotSha256;
                report["NativePreparationLocalCpuRouteRetired"]=preparation.LocalCpuRouteRetired;
                report["NativePreparationRemoteClosureProven"]=preparation.RemoteClosureProven;
            }finally{vm.Qmp("cont");}
            if(clearFailure){
                acknowledgeOldApplication=false;
                suffix=preparation.ReleaseIntoExpectedClearQuarantine(vm,receipt=>
                    NativeOwnerEvidence.Observe(vm,preparation,discovery,prefix+receipt,capsule,output,report,true,true));
                File.WriteAllText(Path.Combine(output,"native-clear-quarantine.txt"),suffix);
                report["NativeClearFailureRetainedOriginalRam"]=true;
                report["GuestRamFailureRejected"]=true;
                report["GuestRamReleaseCleared"]=false;
                report["GuestAuthenticatedPayloadCopiedToSystemRam"]=true;
                AssertRetired(vm,discovery,output);
                return prefix+suffix;
            }
            if(noAck){
                acknowledgeOldApplication=false;
                suffix=preparation.ObserveMissingAckQuarantine(vm,receipt=>
                    NativeOwnerEvidence.Observe(vm,preparation,discovery,prefix+receipt,capsule,output,report,true,true));
                File.WriteAllText(Path.Combine(output,"native-ram-no-ack.txt"),suffix);
                if(!suffix.Contains("CXL_RAM_QUARANTINED ACK_MISSING FREE_ATTEMPTS=0") ||
                    suffix.Contains("CXL_RAM_RELEASED") || preparation.FirmwareAckEffects!=0)
                    throw new InvalidDataException("Missing ACK freed RAM or failed to quarantine.");
                report["NativeObserverTimeoutRetainedRam"]=true;
                report["GuestAuthenticatedPayloadCopiedToSystemRam"]=true;
                report["GuestRamReleaseCleared"]=false;
                AssertRetired(vm,discovery,output);
                return prefix+suffix;
            }
            if(resetHeld){
                if(witness is null||witness.CheckObservation(vm)!=SingNext.Boot.Core.BootFailure.None)throw new InvalidDataException("Verified RAM observation already stale before injected reset.");
                acknowledgeOldApplication=false; // From here, no ACK or FreePages against old firmware context.
                string firmware=CoreResetContractQualification.Run(vm,output,discovery,capsule,report);File.WriteAllText(Path.Combine(output,"reset-firmware.txt"),firmware);
                var disposition=witness.CheckObservation(vm);
                var second=witness.CheckObservation(vm);
                var sessionDisposition=preparation.CheckCurrent(vm);
                if(disposition!=SingNext.Boot.Core.BootFailure.ResetObserved||second!=SingNext.Boot.Core.BootFailure.Quarantined)throw new InvalidDataException("Old RAM observation survived reset.");
                if(sessionDisposition!=SingNext.Boot.Core.BootFailure.ResetObserved||preparation.State!=NativePreparationState.Quarantined)throw new InvalidDataException("Native preparation owner survived reset.");
                int priorReads=preparation.OwnedRamReadEffects;
                try { preparation.ReadOwnedRam(vm,address,pages); throw new InvalidOperationException("Stale allocation admitted."); }
                catch(InvalidDataException) { if(preparation.OwnedRamReadEffects!=priorReads)throw; }
                if(preparation.AdmitFirmwareRamRelease(vm)==SingNext.Boot.Core.BootFailure.None)throw new InvalidDataException("Stale release admitted.");
                var after=Qualification.Discover(vm);
                File.WriteAllText(Path.Combine(output,"post-reset-discovery.json"),System.Text.Json.JsonSerializer.Serialize(after,LabVm.JsonOptions));
                if(after.Fingerprint!=discovery.Fingerprint)throw new InvalidDataException("Topology changed across RAM-held reset.");
                AssertRetired(vm,after,output);
                report["RealResetObserved"]=true;report["ResetSequenceBefore"]=witness.ResetSequence;report["ResetSequenceAfter"]=vm.ObservedResetSequence;
                report["OldRamWitnessFailure"]=disposition.ToString();report["OldRamWitnessRecheck"]=second.ToString();
                report["OldRamWitnessRejected"]=true;report["OldApplicationAckSentAfterReset"]=false;
                report["NativePreparationSessionResetRejected"]=true;report["NativePreparationStateAfterReset"]=preparation.State.ToString();
                report["OldAllocationReadAfterReset"]=false;report["OldAllocationFreeAttemptedAfterReset"]=false;
                report["NativeRamPreparationCurrent"]=false;report["HostRamObservationQuarantined"]=true;
                report["GuestAuthenticatedPayloadCopiedToSystemRam"]=true;report["GuestDestinationSha384Verified"]=true;
                report["GuestHybridBootInfoWireCompatible"]=true;report["NativeRamHeldAfterCxlRetireVerified"]=true;
                report["GuestRamReleaseCleared"]=false;report["GuestResetAwareCopyQualified"]=false;report["GuestBootInfoFullHandoffQualified"]=false;
                return prefix+firmware;
            }
        }catch{
            acknowledgeOldApplication=false;
            preparation.Quarantine(SingNext.Boot.Core.BootFailure.AmbiguousState,"Preparation validation failed; no ACK/free/consume retry.");
            throw;
        }finally{try{if(acknowledgeOldApplication){
            suffix=preparation.ReleaseFirmwareRam(vm);File.WriteAllText(Path.Combine(output,"native-ram-release.txt"),suffix);
            NativeOwnerEvidence.Observe(vm,preparation,discovery,prefix+suffix,capsule,output,report,true,false);
        }}finally{
            preparation.Dispose();
            report["NativePreparationFinalState"]=preparation.State.ToString();
            report["NativePreparationQuarantineReason"]=preparation.QuarantineReason;
            report["NativePreparationOwnedRamReads"]=preparation.OwnedRamReadEffects;
            report["NativePreparationFirmwareAckEffects"]=preparation.FirmwareAckEffects;
            report["NativePreparationPinsRetainedUntilVmTeardown"]=preparation.State==NativePreparationState.Quarantined;
        }}
        if(!suffix.Contains("CXL_RAM_RELEASED CLEARED=1")||suffix.Contains("CXL_RAM_OBSERVER_TIMEOUT")||
            (positive?(!suffix.Contains("CXL_NATIVE_RAM_PIPELINE_PASS FINAL_RETIRED=1")||suffix.Contains("CXL_NATIVE_HDM_FAILED")):
            (!suffix.Contains("CXL_NATIVE_HDM_FAILED")||suffix.Contains("CXL_NATIVE_CAPSULE_AUTHENTICATED"))))throw new InvalidDataException("Native RAM release or pipeline cleanup failed.");
        AssertRetired(vm,discovery,output);
        report["GuestAuthenticatedPayloadCopiedToSystemRam"]=positive;report["GuestDestinationSha384Verified"]=positive;report["GuestHybridBootInfoWireCompatible"]=positive;
        if(!positive){report["NativeRamClearedBeforeFreeVerified"]=true;report["GuestRamFailureCleanupObserved"]=true;report["GuestRamFailureRejected"]=true;}
        report["NativeRamHeldAfterCxlRetireVerified"]=true;report["GuestRamReleaseCleared"]=true;report["GuestResetAwareCopyQualified"]=false;report["GuestBootInfoFullHandoffQualified"]=false;
        report["NativePreparationStateAfterRelease"]=NativePreparationState.RamReleased.ToString();
        return prefix+suffix;
    }
    static void AssertRetired(LabVm vm,Discovery discovery,string output)
    {
        foreach(var c in new[]{discovery.Device,discovery.Port,discovery.Host})if((vm.Read32(c.HdmAddress!.Value+0x20)&0x600)!=0)throw new InvalidDataException("Native RAM cleanup left active decoder.");
        string tree=vm.Hmp("info mtree");File.WriteAllText(Path.Combine(output,"native-ram-final-mtree.txt"),tree);
        if(tree.Contains("cxl-direct-mapping-alias-",StringComparison.Ordinal))throw new InvalidDataException("Native RAM cleanup retained alias.");
    }
}
