using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SingNext.Boot.Core;
using SingNext.Boot.Capsule;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingNext.CxlLab;

// Exercises the actual managed contract. Destinations are HOST arrays, not guest pages.
internal static class CoreResetContractQualification
{
    sealed class QmpResetObserver(LabVm vm,bool injectAtSecondObservation) : IBootResetControl
    {
        int observations;
        public string Firmware {get;private set;}="";
        public BootResetSnapshot Observe()
        {
            observations++;
            if(injectAtSecondObservation&&observations==2)Firmware=vm.WarmResetToShell();
            vm.Qmp("query-status");return new(vm.ObservedResetSequence,vm.ObservedResetSequence);
        }
        public BootFailure RequestTerminalReset()=>BootFailure.Unsupported;
    }
    public static string Run(LabVm vm,string output,Discovery discovery,byte[] capsule,Dictionary<string,object?> report)
    {
        int length=checked((int)BinaryPrimitives.ReadUInt64LittleEndian(capsule.AsSpan(4096+216)));
        byte[] captured=File.ReadAllBytes(Path.Combine(output,"native-owned-ram.bin"));
        byte[] bootInfo=File.ReadAllBytes(Path.Combine(output,"native-hybrid-boot-info.bin"));
        var parsed=HybridBootInfoCodec.Parse(bootInfo,new HashSet<BootEvidenceKind>{BootEvidenceKind.Security});
        if(!parsed.IsSuccess||length<1||length>65536||captured.Length<length||!captured.AsSpan(0,length).SequenceEqual(capsule.AsSpan(8192,length)))throw new InvalidDataException("Core contract source is not the observed verified guest RAM.");
        byte[] payload=captured.AsSpan(0,length).ToArray();
        byte[] destination=new byte[length],infoDestination=new byte[160],scratch=new byte[48];
        try{
            var stable=new QmpResetObserver(vm,false);var expected=stable.Observe();
            // Explicit fixture generations. Native transport has no production ownership authority.
            BootMappingEvidence fixtureMapping=new(1,expected.ResetEpoch,discovery.Hpa,256UL<<20,0,256UL<<20);
            var positive=DirectBootCapsule.PrepareKernelResetAware(1,1,fixtureMapping,payload,capsule.AsSpan(4096+224,48),destination,parsed.Value!,infoDestination,scratch,expected,stable);
            if(!positive.IsSuccess||!destination.AsSpan().SequenceEqual(payload)||!infoDestination.AsSpan().SequenceEqual(bootInfo))throw new InvalidDataException("Actual reset-aware core contract failed stable-epoch input.");
            string positiveHash=Convert.ToHexString(SHA384.HashData(destination));
            destination.AsSpan().Fill(0xa5);infoDestination.AsSpan().Fill(0x5a);
            var late=new QmpResetObserver(vm,true);
            var reset=DirectBootCapsule.PrepareKernelResetAware(1,1,fixtureMapping,payload,capsule.AsSpan(4096+224,48),destination,parsed.Value!,infoDestination,scratch,expected,late);
            if(reset.Failure!=BootFailure.ResetObserved||destination.Any(x=>x!=0)||infoDestination.Any(x=>x!=0)||late.Firmware.Length==0)throw new InvalidDataException("Actual core contract retained host destinations after observed reset.");
            File.WriteAllBytes(Path.Combine(output,"core-reset-host-destination.bin"),destination);
            File.WriteAllBytes(Path.Combine(output,"core-reset-host-boot-info.bin"),infoDestination);
            destination.AsSpan().Fill(0xcc);infoDestination.AsSpan().Fill(0xdd);
            var stale=DirectBootCapsule.PrepareKernelResetAware(1,1,fixtureMapping,payload,capsule.AsSpan(4096+224,48),destination,parsed.Value!,infoDestination,scratch,expected,new QmpResetObserver(vm,false));
            if(stale.Failure!=BootFailure.ResetObserved||destination.Any(x=>x!=0xcc)||infoDestination.Any(x=>x!=0xdd))throw new InvalidDataException("Core precondition behavior differs from unchanged API contract.");
            var evidence=new{
                Scope="Existing managed PrepareKernelResetAware on captured guest data; host arrays only",
                SyntheticGenerationFixture=true,FixtureBootCapsuleGeneration=1,FixtureImageGeneration=1,FixtureMappingGeneration=1,
                ExpectedReset=expected,ObservedResetSequence=vm.ObservedResetSequence,
                StableEpochSucceeded=positive.IsSuccess,StableDestinationSha384=positiveHash,
                LateResetFailure=reset.Failure.ToString(),HostDestinationsClearedAfterLateReset=true,
                StalePreconditionFailure=stale.Failure.ToString(),StalePreconditionLeavesDestinationsUntouched=true,
                GuestDestinationClearingAfterResetVerified=false,ProductionBootAuthorityEstablished=false
            };
            File.WriteAllText(Path.Combine(output,"core-reset-contract.json"),JsonSerializer.Serialize(evidence,LabVm.JsonOptions));
            report["ExistingPrepareKernelResetAwareExecuted"]=true;report["CoreHostDestinationsClearedAfterReset"]=true;
            report["CoreStableEpochPreparationVerified"]=true;report["CoreSyntheticGenerationFixture"]=true;
            report["CoreStaleResetRejectedBeforeCopy"]=true;
            return late.Firmware;
        }finally{
            CryptographicOperations.ZeroMemory(captured);CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(destination);CryptographicOperations.ZeroMemory(infoDestination);CryptographicOperations.ZeroMemory(scratch);
        }
    }
}
