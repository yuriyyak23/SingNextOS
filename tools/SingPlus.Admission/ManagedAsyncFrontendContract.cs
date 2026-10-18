namespace SingPlus.Admission;

// Versioned compiler-facing contract for a future CIL importer. This does not lower or schedule continuations.
public enum ManagedAsyncAbi : byte
{
    LegacyCilStateMachine = 1,
    RuntimeAsyncV2 = 2
}

public enum ManagedAsyncSemanticOperation : byte
{
    Suspend = 1,
    ContinuationReference = 2,
    Resume = 3
}

public readonly record struct ManagedAsyncFrontendContractV1
{
    private readonly IReadOnlyList<ManagedAsyncSemanticOperation>? _operations;

    public ManagedAsyncFrontendContractV1(int Version, ManagedAsyncAbi Abi,
        IReadOnlyList<ManagedAsyncSemanticOperation> Operations)
    {
        this.Version = Version;
        this.Abi = Abi;
        this.Operations = Operations;
    }

    public int Version { get; init; }
    public ManagedAsyncAbi Abi { get; init; }
    public IReadOnlyList<ManagedAsyncSemanticOperation> Operations
    {
        get => _operations!;
        init => _operations = value is null ? null : Array.AsReadOnly(value.ToArray());
    }
}

public enum ManagedAsyncAdmissionDisposition : byte
{
    LegacyCompatible,
    RecognizedButLoweringUnavailable,
    Rejected
}

public static class ManagedAsyncFrontendQualification
{
    public static ManagedAsyncAdmissionDisposition Validate(ManagedAsyncFrontendContractV1 contract)
    {
        if (contract.Version != 1 || contract.Operations is null) return ManagedAsyncAdmissionDisposition.Rejected;
        if (contract.Operations.Any(static op => op is not (ManagedAsyncSemanticOperation.Suspend or
            ManagedAsyncSemanticOperation.ContinuationReference or ManagedAsyncSemanticOperation.Resume)))
            return ManagedAsyncAdmissionDisposition.Rejected;
        return contract.Abi switch
        {
            ManagedAsyncAbi.LegacyCilStateMachine when contract.Operations.Count == 0 => ManagedAsyncAdmissionDisposition.LegacyCompatible,
            ManagedAsyncAbi.RuntimeAsyncV2 when contract.Operations.SequenceEqual(new[]
            {
                ManagedAsyncSemanticOperation.Suspend,
                ManagedAsyncSemanticOperation.ContinuationReference,
                ManagedAsyncSemanticOperation.Resume
            }) => ManagedAsyncAdmissionDisposition.RecognizedButLoweringUnavailable,
            _ => ManagedAsyncAdmissionDisposition.Rejected
        };
    }
}
