using SingPlus.Contracts;

namespace SingPlus.Kernel.Sdk;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class KernelProfileAttribute(MemoryProfile profile) : Attribute
{
    public MemoryProfile Profile { get; } = profile;
}
