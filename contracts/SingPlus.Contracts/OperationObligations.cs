using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace SingPlus.Contracts;

public readonly record struct OperationObligationsDigestV1(string Value);

public readonly record struct OperationRegionObligationV1(
    RegionHandle Region,
    RegionUseMode Mode,
    RegionUseRange Range,
    MutationEpoch MutationEpoch);

public readonly record struct OperationTemporalObligationV1(
    long NotBeforeUtcTicks,
    long ExpiresUtcTicks);

public sealed record OperationSemanticRequirementsV1(
    SemanticRequirementV1<IsolationClassV1> Isolation,
    SemanticRequirementV1<PublicationEnforcementClassV1> Publication,
    SemanticRequirementV1<ReplayClassV1> Replay,
    SemanticRequirementV1<DeterminismClassV1> Determinism,
    SemanticRequirementV1<CancellationClassV1> Cancellation,
    SemanticRequirementV1<ContainmentClassV1> Containment,
    SemanticRequirementV1<LocalityClassV1> Locality,
    SemanticRequirementV1<ResourceAssuranceV1> ResourceAssurance);

/// <summary>
/// Immutable requirements captured from exact SingNext owner snapshots. This value is
/// neither authority nor a lease, and possession never permits execution or effects.
/// </summary>
public sealed record OperationObligationsV1(
    ushort Version,
    ProcessHandle Principal,
    ExternalOperationHandle Operation,
    EndpointSessionHandle? Session,
    IReadOnlyList<OperationRegionObligationV1> RegionUses,
    IReadOnlyList<ResourceEnvelopeV1> ResourceRequirements,
    OperationTemporalObligationV1 Temporal,
    ExternalVisibilityRequirement VisibilityRequirement,
    ExternalPublicationPolicy PublicationPolicy,
    ExternalEffectPolicy EffectPolicy,
    OperationSemanticRequirementsV1 SemanticRequirements,
    OperationObligationsDigestV1 Digest = default)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
    public bool ReservesResources => false;
    public bool AuthorizesPublication => false;

    public OperationObligationsV1 Canonicalize()
    {
        if (Version != CurrentVersion) throw new NotSupportedException("Unknown operation-obligations version.");
        if (Principal.ProcessId.Value == 0 || Principal.Generation == 0 ||
            Operation.OperationId.Value == 0 || Operation.Generation.Value == 0)
            throw new ArgumentException("Principal and operation identities must be exact and non-zero.");
        if (Session is { } session && (session.SessionId.Value == 0 || session.Generation.Value == 0))
            throw new ArgumentException("Session identity must be exact and non-zero.", nameof(Session));
        ArgumentNullException.ThrowIfNull(RegionUses);
        ArgumentNullException.ThrowIfNull(ResourceRequirements);
        ArgumentNullException.ThrowIfNull(SemanticRequirements);
        if (RegionUses.Count == 0)
            throw new ArgumentException("At least one exact Region use is required.", nameof(RegionUses));
        if (Temporal.NotBeforeUtcTicks < 0 || Temporal.ExpiresUtcTicks <= Temporal.NotBeforeUtcTicks)
            throw new ArgumentOutOfRangeException(nameof(Temporal), "Temporal interval is not canonical.");
        if (!Enum.IsDefined(VisibilityRequirement) || !Enum.IsDefined(PublicationPolicy) ||
            !Enum.IsDefined(EffectPolicy.EffectClass) || !Enum.IsDefined(EffectPolicy.ReplayProtection))
            throw new ArgumentOutOfRangeException(nameof(PublicationPolicy), "Unknown effect, visibility, or publication class.");
        if (PublicationPolicy == ExternalPublicationPolicy.Staged &&
            EffectPolicy.EffectClass != ExternalEffectClass.StagedReversibleUntilPublish)
            throw new ArgumentException("Staged publication requires a reversible-until-publish effect.");

        var regions = RegionUses.Select(CanonicalRegion).ToArray();
        var resources = ResourceRequirements.Select(resource => resource.Canonicalize()).ToArray();
        if (resources.Distinct().Count() != resources.Length)
            throw new ArgumentException("Duplicate resource requirements are non-canonical.", nameof(ResourceRequirements));
        ValidateSemanticRequirements(SemanticRequirements);

        var canonical = this with
        {
            RegionUses = new ReadOnlyCollection<OperationRegionObligationV1>(regions),
            ResourceRequirements = new ReadOnlyCollection<ResourceEnvelopeV1>(resources),
            Digest = default,
        };
        var expectedDigest = new OperationObligationsDigestV1(ComputeDigest(canonical));
        if (!string.IsNullOrEmpty(Digest.Value) &&
            !string.Equals(Digest.Value, expectedDigest.Value, StringComparison.Ordinal))
            throw new ArgumentException("Operation-obligations digest does not match canonical content.", nameof(Digest));
        return canonical with { Digest = expectedDigest };
    }

    private static OperationRegionObligationV1 CanonicalRegion(OperationRegionObligationV1 region)
    {
        if (region.Region.RegionId.Value == 0 || region.Region.Generation.Value == 0 ||
            region.MutationEpoch.Value == 0 || !Enum.IsDefined(region.Mode) ||
            region.Range.Offset < 0 || region.Range.Length <= 0)
            throw new ArgumentException("Region obligation identity, mode, epoch, or range is non-canonical.");
        _ = checked(region.Range.Offset + region.Range.Length);
        return region;
    }

    private static void ValidateSemanticRequirements(OperationSemanticRequirementsV1 value)
    {
        Validate(value.Isolation);
        Validate(value.Publication);
        Validate(value.Replay);
        Validate(value.Determinism);
        Validate(value.Cancellation);
        Validate(value.Containment);
        Validate(value.Locality);
        Validate(value.ResourceAssurance);
    }

    private static void Validate<TClass>(SemanticRequirementV1<TClass> value)
        where TClass : struct, Enum
    {
        if (value.Version != SemanticRequirementV1<TClass>.CurrentVersion ||
            !Enum.IsDefined(value.Strength) || !Enum.IsDefined(value.RequiredClass))
            throw new NotSupportedException($"Unknown semantic requirement for {typeof(TClass).Name}.");
    }

    private static string ComputeDigest(OperationObligationsV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(value.Version);
            writer.Write(value.Principal.ProcessId.Value);
            writer.Write(value.Principal.Generation);
            writer.Write(value.Operation.OperationId.Value);
            writer.Write(value.Operation.Generation.Value);
            writer.Write(value.Session.HasValue);
            if (value.Session is { } session)
            {
                writer.Write(session.SessionId.Value);
                writer.Write(session.Generation.Value);
            }
            writer.Write(value.RegionUses.Count);
            foreach (var region in value.RegionUses)
            {
                writer.Write(region.Region.RegionId.Value);
                writer.Write(region.Region.Generation.Value);
                writer.Write((int)region.Mode);
                writer.Write(region.Range.Offset);
                writer.Write(region.Range.Length);
                writer.Write(region.MutationEpoch.Value);
            }
            writer.Write(value.ResourceRequirements.Count);
            foreach (var resource in value.ResourceRequirements)
            {
                writer.Write(resource.Version);
                writer.Write((byte)resource.Family);
                writer.Write((ushort)resource.ResourceClass);
                writer.Write((byte)resource.Unit);
                writer.Write(resource.Amount);
                writer.Write(resource.WindowNanoseconds);
                writer.Write(resource.SemanticScope);
            }
            writer.Write(value.Temporal.NotBeforeUtcTicks);
            writer.Write(value.Temporal.ExpiresUtcTicks);
            writer.Write((int)value.VisibilityRequirement);
            writer.Write((int)value.PublicationPolicy);
            writer.Write((int)value.EffectPolicy.EffectClass);
            writer.Write((int)value.EffectPolicy.ReplayProtection);
            writer.Write(value.EffectPolicy.ReplayConsumerAcknowledged);
            Write(writer, value.SemanticRequirements.Isolation);
            Write(writer, value.SemanticRequirements.Publication);
            Write(writer, value.SemanticRequirements.Replay);
            Write(writer, value.SemanticRequirements.Determinism);
            Write(writer, value.SemanticRequirements.Cancellation);
            Write(writer, value.SemanticRequirements.Containment);
            Write(writer, value.SemanticRequirements.Locality);
            Write(writer, value.SemanticRequirements.ResourceAssurance);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void Write<TClass>(BinaryWriter writer, SemanticRequirementV1<TClass> value)
        where TClass : struct, Enum
    {
        writer.Write(value.Version);
        writer.Write((byte)value.Strength);
        writer.Write(Convert.ToUInt64(value.RequiredClass));
    }
}
