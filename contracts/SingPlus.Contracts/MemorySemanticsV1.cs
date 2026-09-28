using System.Buffers.Binary;

namespace SingPlus.Contracts;

public enum MemoryOwnershipClassV1 : byte
{
    Exclusive = 1,
    SharedReadOnly = 2,
    SharedMutable = 3,
    SharedReadOnlyInputAndExclusiveOutput = 4,
}

public enum MemoryAccessClassV1 : byte
{
    ReadOnlyInput = 1,
    ExclusiveStagedOutput = 2,
    DirectMutableOutput = 3,
    ReadOnlyInputAndExclusiveStagedOutput = 4,
}

public enum MemoryOrderClassV1 : byte
{
    Unspecified = 1,
    CompletionBeforeVisibilityFence = 2,
    SequentiallyConsistent = 3,
}

public enum MemoryAtomicityClassV1 : byte
{
    None = 1,
    NaturallyAlignedScalar = 2,
    ProviderDefined = 3,
}

public enum MemoryCoherenceAssumptionV1 : byte
{
    None = 1,
    ExplicitFenceOnly = 2,
    HardwareCoherent = 3,
}

public enum MemoryVisibilityClassV1 : byte
{
    DevicePrivateUntilCompletion = 1,
    ConsumerVisibleAfterFence = 2,
}

public enum MemoryPublicationModeV1 : byte
{
    StagedWithheldUntilVisible = 1,
    DirectCoherent = 2,
}

/// <summary>Typed memory requirement/guarantee payload. It is not ownership or permission.</summary>
public readonly record struct MemorySemanticsV1(
    ushort Version,
    MemoryOwnershipClassV1 Ownership,
    MemoryAccessClassV1 Access,
    MemoryOrderClassV1 Order,
    MemoryAtomicityClassV1 Atomicity,
    MemoryCoherenceAssumptionV1 CoherenceAssumption,
    MemoryVisibilityClassV1 Visibility,
    MemoryPublicationModeV1 PublicationMode)
{
    public const ushort CurrentVersion = 1;
    public const int CanonicalSize = 9;
    public static SemanticExtensionClassId ExtensionClassId => new("memory.semantics");
    public bool AuthorizesOwnership => false;
    public bool AuthorizesExecution => false;
    public bool AuthorizesPublication => false;

    public MemorySemanticsV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Ownership) || !Enum.IsDefined(Access) ||
            !Enum.IsDefined(Order) || !Enum.IsDefined(Atomicity) || !Enum.IsDefined(CoherenceAssumption) ||
            !Enum.IsDefined(Visibility) || !Enum.IsDefined(PublicationMode))
            throw new NotSupportedException("Memory semantics version or dimension is unsupported.");
        if (Access is MemoryAccessClassV1.ExclusiveStagedOutput or
            MemoryAccessClassV1.ReadOnlyInputAndExclusiveStagedOutput &&
            (Ownership != (Access == MemoryAccessClassV1.ExclusiveStagedOutput
                ? MemoryOwnershipClassV1.Exclusive
                : MemoryOwnershipClassV1.SharedReadOnlyInputAndExclusiveOutput) ||
             PublicationMode != MemoryPublicationModeV1.StagedWithheldUntilVisible ||
             Visibility != MemoryVisibilityClassV1.ConsumerVisibleAfterFence ||
             Order != MemoryOrderClassV1.CompletionBeforeVisibilityFence))
            throw new ArgumentException("Exclusive staged output requires exclusive ownership evidence and completion/fence/visibility/publication ordering.");
        if (PublicationMode == MemoryPublicationModeV1.DirectCoherent &&
            (Access != MemoryAccessClassV1.DirectMutableOutput ||
             CoherenceAssumption != MemoryCoherenceAssumptionV1.HardwareCoherent))
            throw new ArgumentException("Direct publication requires the separately gated coherent mutable contour.");
        if (Ownership == MemoryOwnershipClassV1.SharedMutable && Access != MemoryAccessClassV1.DirectMutableOutput)
            throw new ArgumentException("Shared mutable ownership is not valid for the staged/exclusive contour.");
        return this;
    }

    public byte[] SerializeCanonical()
    {
        var exact = Validate();
        var bytes = new byte[CanonicalSize];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, exact.Version);
        bytes[2] = (byte)exact.Ownership;
        bytes[3] = (byte)exact.Access;
        bytes[4] = (byte)exact.Order;
        bytes[5] = (byte)exact.Atomicity;
        bytes[6] = (byte)exact.CoherenceAssumption;
        bytes[7] = (byte)exact.Visibility;
        bytes[8] = (byte)exact.PublicationMode;
        return bytes;
    }

    public static MemorySemanticsV1 ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != CanonicalSize)
            throw new FormatException("Memory semantics payload has a non-canonical size.");
        return new MemorySemanticsV1(BinaryPrimitives.ReadUInt16BigEndian(bytes), (MemoryOwnershipClassV1)bytes[2],
            (MemoryAccessClassV1)bytes[3], (MemoryOrderClassV1)bytes[4],
            (MemoryAtomicityClassV1)bytes[5], (MemoryCoherenceAssumptionV1)bytes[6],
            (MemoryVisibilityClassV1)bytes[7], (MemoryPublicationModeV1)bytes[8]).Validate();
    }

    public SemanticExtensionClauseV1 ToClause(SemanticExtensionRequirement requirement) =>
        SemanticExtensionClauseV1.Create(ExtensionClassId, "singnext.memory-semantics/1", CurrentVersion,
            requirement, SerializeCanonical());
}

public static class MemorySemanticPartialOrderV1
{
    public static bool Refines(MemorySemanticsV1 provided, MemorySemanticsV1 required)
    {
        try { provided.Validate(); required.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }

        return OwnershipRefines(provided.Ownership, required.Ownership) &&
               provided.Access == required.Access &&
               OrderRefines(provided.Order, required.Order) &&
               AtomicityRefines(provided.Atomicity, required.Atomicity) &&
               CoherenceRefines(provided.CoherenceAssumption, required.CoherenceAssumption) &&
               LinearRefines(provided.Visibility, required.Visibility) &&
               provided.PublicationMode == required.PublicationMode;
    }

    private static bool OwnershipRefines(MemoryOwnershipClassV1 provided, MemoryOwnershipClassV1 required) =>
        provided == required || provided == MemoryOwnershipClassV1.Exclusive && required == MemoryOwnershipClassV1.SharedReadOnly;

    private static bool AtomicityRefines(MemoryAtomicityClassV1 provided, MemoryAtomicityClassV1 required) =>
        required == MemoryAtomicityClassV1.None || provided == required;

    private static bool CoherenceRefines(MemoryCoherenceAssumptionV1 provided, MemoryCoherenceAssumptionV1 required) =>
        provided == required || required == MemoryCoherenceAssumptionV1.None;

    // SC ordering does not establish device completion or a visibility fence.
    // The two named guarantees are incomparable until a versioned clause can
    // represent their conjunction with executable provider evidence.
    private static bool OrderRefines(MemoryOrderClassV1 provided, MemoryOrderClassV1 required) =>
        provided == required || required == MemoryOrderClassV1.Unspecified;

    private static bool LinearRefines<T>(T provided, T required) where T : struct, Enum =>
        Convert.ToByte(provided) >= Convert.ToByte(required);
}
