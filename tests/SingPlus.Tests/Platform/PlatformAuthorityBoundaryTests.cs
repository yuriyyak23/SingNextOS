using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Tests.Platform;

public sealed class PlatformAuthorityBoundaryTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public void ProviderIdentityUsesDedicatedIdentifierSpace()
    {
        var providerIdProperty = typeof(PlatformProviderDescriptor)
            .GetProperty(nameof(PlatformProviderDescriptor.ProviderId));

        Assert.NotNull(providerIdProperty);
        Assert.Equal(typeof(PlatformProviderId), providerIdProperty.PropertyType);
        Assert.NotEqual(typeof(CapabilityId), typeof(PlatformProviderId));
        Assert.NotEqual(typeof(DomainId), typeof(PlatformProviderDomainLeaseId));
        Assert.NotEqual(typeof(RegionHandle), typeof(PlatformProviderRegionMappingId));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void PlatformSubjectUsesExactLocalProcessHandleWithoutCollapsingProviderIdentity()
    {
        var processProperty = typeof(PlatformDomainIdentity)
            .GetProperty(nameof(PlatformDomainIdentity.Process));

        Assert.NotNull(processProperty);
        Assert.Equal(typeof(ProcessHandle), processProperty.PropertyType);
        Assert.NotEqual(typeof(ProcessHandle), typeof(PlatformProviderDomainLeaseId));
        Assert.NotEqual(typeof(ProcessHandle), typeof(CapabilityId));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void SipContractsDoNotReferencePlatformProviderTypes()
    {
        var references = typeof(CapabilityId).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(
            references,
            static reference => reference.Name?.StartsWith(
                "SingPlus.Platform",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void CorePlatformPublicApiDoesNotExposeRawHardwareOrCompatibilityState()
    {
        var forbiddenTerms = new[]
        {
            "VMCS",
            "VMX",
            "PhysicalAddress",
            "Opcode",
            "LaneId"
        };

        var surface = typeof(IPlatformAuthorityProvider).Assembly
            .GetExportedTypes()
            .SelectMany(DescribePublicSurface)
            .ToArray();

        foreach (var forbidden in forbiddenTerms)
        {
            Assert.DoesNotContain(
                surface,
                entry => entry.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static IEnumerable<string> DescribePublicSurface(Type type)
    {
        yield return type.FullName ?? type.Name;

        foreach (var constructor in type.GetConstructors())
        {
            yield return constructor.Name;
            foreach (var parameter in constructor.GetParameters())
                yield return parameter.ParameterType.FullName ?? parameter.ParameterType.Name;
        }

        foreach (var method in type.GetMethods(
                     BindingFlags.Public |
                     BindingFlags.Instance |
                     BindingFlags.Static |
                     BindingFlags.DeclaredOnly))
        {
            yield return method.Name;
            yield return method.ReturnType.FullName ?? method.ReturnType.Name;
            foreach (var parameter in method.GetParameters())
                yield return parameter.ParameterType.FullName ?? parameter.ParameterType.Name;
        }

        foreach (var property in type.GetProperties(
                     BindingFlags.Public |
                     BindingFlags.Instance |
                     BindingFlags.Static |
                     BindingFlags.DeclaredOnly))
        {
            yield return property.Name;
            yield return property.PropertyType.FullName ?? property.PropertyType.Name;
        }

        foreach (var field in type.GetFields(
                     BindingFlags.Public |
                     BindingFlags.Instance |
                     BindingFlags.Static |
                     BindingFlags.DeclaredOnly))
        {
            yield return field.Name;
            yield return field.FieldType.FullName ?? field.FieldType.Name;
        }
    }
}
