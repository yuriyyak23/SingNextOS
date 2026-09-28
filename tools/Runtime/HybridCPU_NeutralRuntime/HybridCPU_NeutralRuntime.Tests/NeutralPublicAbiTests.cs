using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class NeutralPublicAbiTests
{
    [Fact]
    public void RuntimeInterfaceExposesOnlyTypedAuthorityAndVisibilityOperations()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(INeutralDomainRuntime.ImplementationProfile),
            nameof(INeutralRuntimeFeatureProvider.QueryNeutralFeatures),
            nameof(INeutralDomainOperations.Bind),
            nameof(INeutralDomainOperations.Close),
            nameof(INeutralDomainOperations.TransitionExecution),
            nameof(INeutralMappingOperations.MapOwnedRegion),
            nameof(INeutralMappingOperations.CloseOwnedRegionMapping),
            nameof(INeutralMappingOperations.PrepareOwnedRegionVisibility),
            nameof(INeutralMappingOperations.AcquireOwnedRegionVisibility),
            nameof(INeutralDeviceOperations.BindDevice),
            nameof(INeutralDeviceOperations.CloseDevice),
            nameof(INeutralMmioOperations.MapMmio),
            nameof(INeutralMmioOperations.CloseMmio),
            nameof(INeutralInterruptOperations.BindInterrupt),
            nameof(INeutralInterruptOperations.SignalInterrupt),
            nameof(INeutralInterruptOperations.PollInterrupt),
            nameof(INeutralInterruptOperations.CompleteInterruptDelivery),
            nameof(INeutralInterruptOperations.CloseInterrupt),
            nameof(INeutralDmaOperations.BindDmaGrant),
            nameof(INeutralDmaOperations.CloseDmaGrant),
            nameof(INeutralDmaOperations.PrepareDmaVisibility),
            nameof(INeutralDmaOperations.AcquireDmaVisibility),
        };

        var runtime = typeof(INeutralDomainRuntime);
        var names = runtime.GetProperties().Select(property => property.Name)
            .Concat(runtime.GetInterfaces().SelectMany(type => type.GetMethods()).Select(method => method.Name))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.Order(), names.Order());
    }

    [Fact]
    public void PublicAbiHasNoDynamicVariadicOrUntypedOperationParameters()
    {
        var publicMethods = typeof(INeutralDomainRuntime).GetInterfaces()
            .Append(typeof(INeutralDomainRuntime))
            .SelectMany(type => type.GetMethods());

        foreach (var method in publicMethods)
        {
            Assert.DoesNotContain(method.GetParameters(), parameter =>
                parameter.ParameterType == typeof(object) ||
                parameter.GetCustomAttribute<ParamArrayAttribute>() is not null);
        }
    }

    [Fact]
    public void DmaContractRemainsAdmissionOnly()
    {
        var forbiddenFragments = new[] { "Submit", "CompleteDma", "Descriptor", "Scatter", "Physical", "BusAddress", "Iommu", "Apic", "Msi", "Gsi" };
        var publicNames = typeof(INeutralDomainRuntime).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(member => member.Name)
            .ToArray();

        foreach (var fragment in forbiddenFragments)
            Assert.DoesNotContain(publicNames, name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResourceFamiliesUseIndependentHandleAndEpochTypes()
    {
        var handles = new[]
        {
            typeof(NeutralDomainBindingHandle), typeof(NeutralOwnedRegionMappingHandle),
            typeof(NeutralDeviceLeaseHandle), typeof(NeutralMmioLeaseHandle),
            typeof(NeutralInterruptLeaseHandle), typeof(NeutralDmaGrantHandle),
            typeof(NeutralChildDomainHandle), typeof(NeutralGuestMappingHandle),
            typeof(NeutralVirtualIoHandle), typeof(NeutralExecutableArtifactHandle),
        };
        var epochs = new[]
        {
            typeof(NeutralDomainBindingEpoch), typeof(NeutralOwnedRegionMappingEpoch),
            typeof(NeutralDeviceLeaseEpoch), typeof(NeutralMmioLeaseEpoch),
            typeof(NeutralInterruptLeaseEpoch), typeof(NeutralDmaGrantEpoch),
            typeof(NeutralChildDomainEpoch), typeof(NeutralGuestMappingEpoch),
            typeof(NeutralVirtualIoEpoch), typeof(NeutralExecutableArtifactEpoch),
        };

        Assert.Equal(handles.Length, handles.Distinct().Count());
        Assert.Equal(epochs.Length, epochs.Distinct().Count());
    }

    [Fact]
    public void NeutralRuntimeDoesNotReferenceProviderOrBackendAssemblies()
    {
        var references = typeof(INeutralDomainRuntime).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference =>
            reference.Name?.Contains("HybridCpu", StringComparison.OrdinalIgnoreCase) == true ||
            reference.Name?.Contains("HybridCPU-v2", StringComparison.OrdinalIgnoreCase) == true ||
            reference.Name?.Contains("ISE", StringComparison.OrdinalIgnoreCase) == true ||
            reference.Name?.Contains("Emulator", StringComparison.OrdinalIgnoreCase) == true);
    }
}
