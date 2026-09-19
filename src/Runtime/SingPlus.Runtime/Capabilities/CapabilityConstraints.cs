using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum CapabilityConstraintSchema : ushort { V1 = 1 }

internal enum CapabilityOperation : ushort
{
    Read = 1, Write = 2, Map = 3, Signal = 4, Configure = 5,
    Transfer = 6, Delegate = 7, Execute = 8,
}

internal readonly record struct RightsConstraint(CapabilityRights Rights)
{
    internal RightsConstraint Canonicalize()
    {
        const CapabilityRights known = CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Map |
            CapabilityRights.Signal | CapabilityRights.Configure | CapabilityRights.Transfer |
            CapabilityRights.Delegate | CapabilityRights.Execute;
        if (Rights == CapabilityRights.None || (Rights & ~known) != 0)
            throw new ArgumentOutOfRangeException(nameof(Rights));
        return this;
    }

    internal static bool IsSubset(RightsConstraint child, RightsConstraint parent) =>
        (parent.Rights & child.Rights) == child.Rights;
}

internal readonly record struct ExactResourceConstraint(
    ResourceKind Kind, string ResourceId, ulong Generation, string? SubresourceType, string? SubresourceId)
{
    internal ExactResourceConstraint Canonicalize()
    {
        if (!Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(ResourceId) || Generation == 0)
            throw new ArgumentException("Resource identity is incomplete or unknown.");
        var type = string.IsNullOrWhiteSpace(SubresourceType) ? null : SubresourceType.Trim();
        var id = string.IsNullOrWhiteSpace(SubresourceId) ? null : SubresourceId.Trim();
        if ((type is null) != (id is null)) throw new ArgumentException("Typed subresource identity must be complete.");
        return new(Kind, ResourceId.Trim(), Generation, type, id);
    }

    internal static bool IsSubset(ExactResourceConstraint child, ExactResourceConstraint parent) => child == parent;
}

internal readonly record struct OperationSetConstraint
{
    internal OperationSetConstraint(IEnumerable<CapabilityOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        Operations = operations.Distinct().Order().ToImmutableArray();
        if (Operations.IsDefaultOrEmpty || Operations.Any(static operation => !Enum.IsDefined(operation)))
            throw new ArgumentException("Operation set contains no operations or an unknown operation.", nameof(operations));
    }

    internal ImmutableArray<CapabilityOperation> Operations { get; }
    internal OperationSetConstraint Canonicalize() => new(Operations);
    internal bool IsAuthorizedBy(CapabilityRights rights) => Operations.All(operation => operation switch
    {
        CapabilityOperation.Read => (rights & CapabilityRights.Read) != 0,
        CapabilityOperation.Write => (rights & CapabilityRights.Write) != 0,
        CapabilityOperation.Map => (rights & CapabilityRights.Map) != 0,
        CapabilityOperation.Signal => (rights & CapabilityRights.Signal) != 0,
        CapabilityOperation.Configure => (rights & CapabilityRights.Configure) != 0,
        CapabilityOperation.Transfer => (rights & CapabilityRights.Transfer) != 0,
        CapabilityOperation.Delegate => (rights & CapabilityRights.Delegate) != 0,
        CapabilityOperation.Execute => (rights & CapabilityRights.Execute) != 0,
        _ => false,
    });
    internal static bool IsSubset(OperationSetConstraint child, OperationSetConstraint parent) =>
        child.Operations.All(parent.Operations.Contains);
}

internal readonly record struct RangeConstraint(ulong OffsetBytes, ulong LengthBytes, uint ElementSize, uint Alignment)
{
    internal RangeConstraint Canonicalize()
    {
        if (LengthBytes == 0 || ElementSize == 0 || Alignment == 0 || (Alignment & (Alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(LengthBytes), "Range length, element size and power-of-two alignment are required.");
        _ = checked(OffsetBytes + LengthBytes);
        if (OffsetBytes % Alignment != 0 || LengthBytes % ElementSize != 0 || OffsetBytes % ElementSize != 0)
            throw new ArgumentException("Range is not aligned to its declared alignment and element size.");
        return this;
    }

    internal static bool IsSubset(RangeConstraint child, RangeConstraint parent)
    {
        try
        {
            child = child.Canonicalize();
            parent = parent.Canonicalize();
            var childEnd = checked(child.OffsetBytes + child.LengthBytes);
            var parentEnd = checked(parent.OffsetBytes + parent.LengthBytes);
            return child.ElementSize == parent.ElementSize && child.Alignment >= parent.Alignment &&
                   child.Alignment % parent.Alignment == 0 && child.OffsetBytes >= parent.OffsetBytes && childEnd <= parentEnd;
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentException) { return false; }
    }
}

internal readonly record struct LifetimeConstraint(long NotBeforeUtcTicks, long ExpiresUtcTicks)
{
    internal LifetimeConstraint Canonicalize()
    {
        if (NotBeforeUtcTicks < 0 || ExpiresUtcTicks <= NotBeforeUtcTicks)
            throw new ArgumentOutOfRangeException(nameof(ExpiresUtcTicks));
        return this;
    }

    internal static bool IsSubset(LifetimeConstraint child, LifetimeConstraint parent) =>
        child.NotBeforeUtcTicks >= parent.NotBeforeUtcTicks && child.ExpiresUtcTicks <= parent.ExpiresUtcTicks;
}

internal readonly record struct TargetSubjectConstraint(DomainId? DomainId, ulong Generation, bool AllowsRetarget = false)
{
    internal TargetSubjectConstraint Canonicalize()
    {
        if (DomainId is null && Generation == 0) return this;
        if (DomainId is not { Value: > 0 } || Generation == 0) throw new ArgumentOutOfRangeException(nameof(Generation));
        return this;
    }
    internal static bool IsSubset(TargetSubjectConstraint child, TargetSubjectConstraint parent) =>
        (!child.AllowsRetarget || parent.AllowsRetarget) &&
        (parent.DomainId is null ||
         child.DomainId is not null &&
         (child.DomainId == parent.DomainId && child.Generation == parent.Generation || parent.AllowsRetarget));
}

internal readonly record struct SessionConstraint(EndpointSessionHandle? Session)
{
    internal SessionConstraint Canonicalize()
    {
        if (Session is { } session && (session.SessionId.Value == 0 || session.Generation.Value == 0))
            throw new ArgumentException("Session identity is incomplete.");
        return this;
    }

    internal static bool IsSubset(SessionConstraint child, SessionConstraint parent) =>
        parent.Session is null || child.Session == parent.Session;
}

internal readonly record struct DelegationDepthConstraint(ushort RemainingDepth)
{
    internal DelegationDepthConstraint Canonicalize() => this;
    internal static bool IsSubset(DelegationDepthConstraint child, DelegationDepthConstraint parent) =>
        parent.RemainingDepth > 0 && child.RemainingDepth < parent.RemainingDepth;
}

internal readonly record struct QuotaAccountReference(ulong Value);
internal readonly record struct QuotaConstraint(QuotaAccountReference Account, ulong PerHandleCeiling)
{
    internal QuotaConstraint Canonicalize() => Account.Value == 0 || PerHandleCeiling == 0
        ? throw new ArgumentOutOfRangeException(nameof(PerHandleCeiling)) : this;
    internal static bool IsSubset(QuotaConstraint child, QuotaConstraint parent) =>
        child.Account == parent.Account && child.PerHandleCeiling <= parent.PerHandleCeiling;
}

internal sealed record EffectiveCapabilityConstraints(
    CapabilityConstraintSchema Schema,
    RightsConstraint Rights,
    ExactResourceConstraint Resource,
    OperationSetConstraint Operations,
    RangeConstraint? Range,
    LifetimeConstraint Lifetime,
    TargetSubjectConstraint TargetSubject,
    SessionConstraint Session,
    DelegationDepthConstraint DelegationDepth,
    QuotaConstraint Quota)
{
    internal EffectiveCapabilityConstraints Canonicalize()
    {
        if (Schema != CapabilityConstraintSchema.V1) throw new NotSupportedException("Unknown capability constraint schema.");
        var rights = Rights.Canonicalize();
        var operations = Operations.Canonicalize();
        if (!operations.IsAuthorizedBy(rights.Rights))
            throw new ArgumentException("Operation set is not authorized by the capability rights.", nameof(Operations));
        return new(Schema, rights, Resource.Canonicalize(), operations,
            Range?.Canonicalize(), Lifetime.Canonicalize(), TargetSubject.Canonicalize(), Session.Canonicalize(),
            DelegationDepth.Canonicalize(), Quota.Canonicalize());
    }

    internal static bool IsSubset(EffectiveCapabilityConstraints child, EffectiveCapabilityConstraints parent)
    {
        try
        {
            child = child.Canonicalize();
            parent = parent.Canonicalize();
            return child.Schema == parent.Schema && RightsConstraint.IsSubset(child.Rights, parent.Rights) &&
                   ExactResourceConstraint.IsSubset(child.Resource, parent.Resource) &&
                   OperationSetConstraint.IsSubset(child.Operations, parent.Operations) &&
                   ((parent.Range is null && child.Range is null) || (parent.Range is { } pr && child.Range is { } cr && RangeConstraint.IsSubset(cr, pr))) &&
                   LifetimeConstraint.IsSubset(child.Lifetime, parent.Lifetime) &&
                   TargetSubjectConstraint.IsSubset(child.TargetSubject, parent.TargetSubject) &&
                   SessionConstraint.IsSubset(child.Session, parent.Session) &&
                   DelegationDepthConstraint.IsSubset(child.DelegationDepth, parent.DelegationDepth) &&
                   QuotaConstraint.IsSubset(child.Quota, parent.Quota);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException) { return false; }
    }

    internal byte[] SerializeCanonical()
    {
        var value = Canonicalize();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)value.Schema); writer.Write((int)value.Rights.Rights);
        writer.Write((int)value.Resource.Kind); WriteString(writer, value.Resource.ResourceId); writer.Write(value.Resource.Generation);
        WriteString(writer, value.Resource.SubresourceType ?? ""); WriteString(writer, value.Resource.SubresourceId ?? "");
        writer.Write(value.Operations.Operations.Length); foreach (var operation in value.Operations.Operations) writer.Write((ushort)operation);
        writer.Write(value.Range is not null); if (value.Range is { } range) { writer.Write(range.OffsetBytes); writer.Write(range.LengthBytes); writer.Write(range.ElementSize); writer.Write(range.Alignment); }
        writer.Write(value.Lifetime.NotBeforeUtcTicks); writer.Write(value.Lifetime.ExpiresUtcTicks);
        writer.Write(value.TargetSubject.DomainId is not null); if (value.TargetSubject.DomainId is { } target) writer.Write(target.Value); writer.Write(value.TargetSubject.Generation); writer.Write(value.TargetSubject.AllowsRetarget);
        writer.Write(value.Session.Session is not null); if (value.Session.Session is { } session) { writer.Write(session.SessionId.Value); writer.Write(session.Generation.Value); }
        writer.Write(value.DelegationDepth.RemainingDepth); writer.Write(value.Quota.Account.Value); writer.Write(value.Quota.PerHandleCeiling);
        return stream.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length); writer.Write(bytes);
    }
}
