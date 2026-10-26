namespace SingPlus.Platform;

/// <summary>Limited software register-local intent. Never a ProductionSecure execution request.</summary>
public sealed record PlatformSoftwareSecureAdmissionRequest(
    PlatformProviderChildDomainLease Child, Guid Operation, ulong Generation, ulong Left, ulong Right,
    bool ExecuteRegisterLocal = false)
{
    public PlatformExecutableArtifactReceipt? Artifact { get; init; }
}

/// <summary>Cleanup and historical machine facts; no execution or publication authority.</summary>
public sealed record PlatformSoftwareSecureAdmissionObservation(
    PlatformSoftwareSecureAdmissionRequest Request, bool ResourcesClosed,
    ulong MachineAttempt = 0, ulong NoEffectCommit = 0, ulong? RegisterLocalValue = null, ulong RegisterLocalCommit = 0)
{
    public bool IsExecutionAllowed => false;
}

public interface IPlatformSoftwareSecureAdmissionProvider
{
    PlatformSoftwareSecureAdmissionObservation ObserveSoftwareSecureAdmission(PlatformSoftwareSecureAdmissionRequest request);
    PlatformSoftwareSecureAdmissionObservation ResolveSoftwareSecureAdmission(PlatformSoftwareSecureAdmissionRequest original);
}
