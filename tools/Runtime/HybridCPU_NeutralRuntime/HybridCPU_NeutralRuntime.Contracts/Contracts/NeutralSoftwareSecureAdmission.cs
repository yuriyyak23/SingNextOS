namespace YAKSys_Hybrid_CPU.Core;

/// <summary>Explicit software admission profile, never a ProductionSecure property.</summary>
public enum NeutralSoftwareSecureProfile { NoEffectRegisterLocal = 1, RegisterLocalExecution = 2, ProductionSecure = 3 }
public enum NeutralSoftwareSecureAdmissionStatus { ExpectedDeny = 1, Denied, Unsupported, CleanupRequired, RegisterLocalRetired }

/// <summary>Trusted neutral host intent; does not grant semantic or machine permission.</summary>
public sealed class NeutralSoftwareSecureAdmissionRequest
{
    public NeutralSoftwareSecureAdmissionRequest(NeutralChildDomainLease child, Guid operation, ulong generation,
        ulong left, ulong right, NeutralSoftwareSecureProfile profile = NeutralSoftwareSecureProfile.NoEffectRegisterLocal,
        uint schemaVersion = 1)
    {
        if (operation == Guid.Empty || generation == 0) throw new ArgumentException("Original operation required.");
        Child = child; Operation = operation; Generation = generation; Left = left; Right = right;
        Profile = profile; SchemaVersion = schemaVersion;
    }
    public NeutralChildDomainLease Child { get; }
    public Guid Operation { get; }
    public ulong Generation { get; }
    public ulong Left { get; }
    public ulong Right { get; }
    public NeutralSoftwareSecureProfile Profile { get; }
    public uint SchemaVersion { get; }
}

/// <summary>Historical admission/retire facts, never a reusable execution permission or OS publication receipt.</summary>
public sealed class NeutralSoftwareSecureAdmissionObservation
{
    public NeutralSoftwareSecureAdmissionObservation(NeutralSoftwareSecureAdmissionRequest request,
        NeutralSoftwareSecureAdmissionStatus status, ulong machineAttempt = 0, ulong noEffectCommit = 0,
        bool resourcesClosed = false, ulong? registerLocalValue = null, ulong registerLocalCommit = 0)
    { Request = request; Status = status; MachineAttempt = machineAttempt; NoEffectCommit = noEffectCommit; ResourcesClosed = resourcesClosed;
      RegisterLocalValue = registerLocalValue; RegisterLocalCommit = registerLocalCommit; }
    public NeutralSoftwareSecureAdmissionRequest Request { get; }
    public NeutralSoftwareSecureAdmissionStatus Status { get; }
    public ulong MachineAttempt { get; }
    public ulong NoEffectCommit { get; }
    public bool ResourcesClosed { get; }
    public ulong? RegisterLocalValue { get; }
    public ulong RegisterLocalCommit { get; }
    public bool IsExecutionAllowed => false;
}

public interface INeutralSoftwareSecureAdmissionProvider
{
    NeutralSoftwareSecureAdmissionObservation ObserveSoftwareSecureAdmission(NeutralSoftwareSecureAdmissionRequest request);
    NeutralSoftwareSecureAdmissionObservation ResolveSoftwareSecureAdmission(NeutralSoftwareSecureAdmissionRequest originalRequest);
}

/// <summary>Explicit software arithmetic request. Returned facts are not OS publication authority.</summary>
public interface INeutralSoftwareSecureRegisterLocalProvider
{
    NeutralSoftwareSecureAdmissionObservation ExecuteSoftwareSecureRegisterLocal(NeutralSoftwareSecureAdmissionRequest request);
    NeutralSoftwareSecureAdmissionObservation ResolveSoftwareSecureRegisterLocal(NeutralSoftwareSecureAdmissionRequest original);
}
