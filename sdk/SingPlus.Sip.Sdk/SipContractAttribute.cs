using SingPlus.Contracts;

namespace SingPlus.Sip.Sdk;

[AttributeUsage(AttributeTargets.Interface)]
public sealed class SipContractAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method)]
public sealed class MessageAttribute(int id) : Attribute
{
    public int Id { get; } = id;
}

[AttributeUsage(AttributeTargets.Interface)]
public sealed class InitialStateAttribute(string state) : Attribute
{
    public string State { get; } = state;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class TransitionAttribute(string from, string to) : Attribute
{
    public string From { get; } = from;
    public string To { get; } = to;
}

[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
public sealed class TerminalStateAttribute(string state) : Attribute
{
    public string State { get; } = state;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresCapabilityAttribute(ResourceKind resourceKind, string resourceId, CapabilityRights rights) : Attribute
{
    public ResourceKind ResourceKind { get; } = resourceKind;
    public string ResourceId { get; } = resourceId;
    public CapabilityRights Rights { get; } = rights;
}

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ConsumesAttribute : Attribute;

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class BorrowsAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ReturnsOwnershipAttribute : Attribute;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class BoundedPayloadAttribute(int maxBytes) : Attribute
{
    public int MaxBytes { get; } = maxBytes;
}
