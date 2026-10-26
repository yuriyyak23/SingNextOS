using System.Security.Cryptography;
using SingNext.Boot.Core;

namespace SingNext.CxlLab;

// Host laboratory observation lifetime only. Not a guest authority or boot permit.
internal sealed class LabRamPreparationWitness
{
    readonly LabVm owner;
    bool invalidated;
    public Guid Id {get;}=Guid.NewGuid();
    public ulong ResetSequence {get;}
    public string SnapshotSha256 {get;}
    public LabRamPreparationWitness(LabVm owner,byte[] verifiedSnapshot,ulong expectedResetSequence)
    {
        this.owner=owner;owner.Qmp("query-status");
        if(owner.ObservedResetSequence!=expectedResetSequence)throw new InvalidDataException("Reset changed during RAM observation.");
        ResetSequence=expectedResetSequence;
        SnapshotSha256=Convert.ToHexString(SHA256.HashData(verifiedSnapshot));
    }
    public BootFailure CheckObservation(LabVm session)
    {
        if(invalidated)return BootFailure.Quarantined;
        if(!ReferenceEquals(owner,session)){invalidated=true;return BootFailure.Quarantined;}
        owner.Qmp("query-status");
        if(owner.ObservedResetSequence!=ResetSequence){invalidated=true;return BootFailure.ResetObserved;}
        return BootFailure.None;
    }
}
