namespace YAKSys_Hybrid_CPU.Core;

/// <summary>Explicit bounded entry intent on an already admitted artifact; not permission or emission authority.</summary>
public sealed class NeutralSoftwareSecureArtifactEntryRequest
{
    public NeutralSoftwareSecureArtifactEntryRequest(NeutralChildDomainLease child, NeutralExecutableArtifactReceipt artifact,
        Guid operation, ulong generation, ulong left, ulong right, uint schemaVersion = 1)
    {
        if (operation == Guid.Empty || generation == 0) throw new ArgumentException("Original operation required.");
        Child = child; Artifact = artifact; Operation = operation; Generation = generation;
        Left = left; Right = right; SchemaVersion = schemaVersion;
    }
    public NeutralChildDomainLease Child { get; }
    public NeutralExecutableArtifactReceipt Artifact { get; }
    public Guid Operation { get; }
    public ulong Generation { get; }
    public ulong Left { get; }
    public ulong Right { get; }
    public uint SchemaVersion { get; }
}

/// <summary>Historical native entry/cleanup facts. Neither a reusable permit nor OS publication.</summary>
public sealed class NeutralSoftwareSecureArtifactEntryObservation
{
    public NeutralSoftwareSecureArtifactEntryObservation(NeutralSoftwareSecureArtifactEntryRequest request,
        NeutralSoftwareSecureAdmissionStatus status, bool resourcesClosed = false, Guid runId = default,
        ulong executionGeneration = 0, ulong machineAttempt = 0, ulong commit = 0, ulong? value = null)
    { Request = request; Status = status; ResourcesClosed = resourcesClosed; RunId = runId;
      ExecutionGeneration = executionGeneration; MachineAttempt = machineAttempt; Commit = commit; Value = value; }
    public NeutralSoftwareSecureArtifactEntryRequest Request { get; }
    public NeutralSoftwareSecureAdmissionStatus Status { get; }
    public bool ResourcesClosed { get; }
    public Guid RunId { get; }
    public ulong ExecutionGeneration { get; }
    public ulong MachineAttempt { get; }
    public ulong Commit { get; }
    public ulong? Value { get; }
    public bool IsExecutionAllowed => false;
}

public interface INeutralSoftwareSecureArtifactEntryProvider
{
    NeutralSoftwareSecureArtifactEntryObservation ExecuteSoftwareSecureArtifactEntry(NeutralSoftwareSecureArtifactEntryRequest request);
    NeutralSoftwareSecureArtifactEntryObservation ResolveSoftwareSecureArtifactEntry(NeutralSoftwareSecureArtifactEntryRequest original);
    bool CancelSoftwareSecureArtifactEntry(NeutralSoftwareSecureArtifactEntryRequest original);
}
