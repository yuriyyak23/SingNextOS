using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuPlatformAuthorityProviderTests
{
    [Fact]
    public void ProviderBindsAndRevokesRealNeutralRuntimeLease()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(new DomainId(10), 7);

        var bind = provider.BindDomain(subject);

        Assert.True(bind.IsSuccess, bind.Message);
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.Equal(subject, bind.Value!.Subject);
        Assert.Equal(new PlatformProviderLeaseGeneration(1), bind.Value.Generation);
        Assert.NotEqual(typeof(DomainId), typeof(NeutralDomainBindingHandle));
        Assert.NotEqual(typeof(PlatformProviderDomainLeaseId), typeof(NeutralDomainBindingHandle));
        Assert.NotEqual(typeof(PlatformProviderLeaseGeneration), typeof(NeutralDomainBindingEpoch));

        var revoke = provider.RevokeDomain(bind.Value);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    [Fact]
    public void StaleOrWrongSubjectProviderLeaseIsRejectedBeforeExternalClosure()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var binding = provider.BindDomain(
            new PlatformDomainIdentity(new DomainId(20), 3)).Value!;

        var stale = binding with
        {
            Generation = new PlatformProviderLeaseGeneration(binding.Generation.Value + 1),
        };
        var wrongSubject = binding with
        {
            Subject = new PlatformDomainIdentity(new DomainId(21), 3),
        };

        var staleResult = provider.RevokeDomain(stale);
        var wrongSubjectResult = provider.RevokeDomain(wrongSubject);

        Assert.Equal(PlatformAuthorityStatus.Stale, staleResult.Status);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain, wrongSubjectResult.Status);
        Assert.Equal(1, runtime.ActiveBindingCount);

        Assert.True(provider.RevokeDomain(binding).IsSuccess);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    [Fact]
    public void DuplicateSingSubjectDoesNotMaterializeSecondHybridCpuBinding()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateProcess(kernel, 31, 310, 1);

        var first = kernel.BindPlatformDomain(handle);
        var second = kernel.BindPlatformDomain(handle);

        Assert.True(first.IsSuccess, first.Message);
        Assert.False(second.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, second.Error);
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.NotEqual(ProcessState.Exiting, process.State);
    }

    [Fact]
    public void WrongProcessGenerationIsRejectedBeforeHybridCpuAdmission()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, handle) = CreateProcess(kernel, 32, 320, 4);
        var stale = handle with { Generation = handle.Generation + 1 };

        var bind = kernel.BindPlatformDomain(stale);

        Assert.False(bind.IsSuccess);
        Assert.Equal(KernelError.StaleHandle, bind.Error);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    [Fact]
    public void PhaseTwoProcessTeardownClosesHybridCpuDomainBeforePublishingExit()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, handle) = CreateProcess(kernel, 33, 330, 1);

        var binding = kernel.BindPlatformDomain(handle);
        Assert.True(binding.IsSuccess, binding.Message);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var terminate = kernel.TerminateProcess(handle);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveBindingCount);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Fact]
    public void ProviderAdvertisesOnlyNeutralRuntimeAdmissionInThisSlice()
    {
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        var features = provider.QueryFeatures();

        Assert.Equal(
            PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.NeutralDomains).Availability);
        Assert.Equal(
            PlatformFeatureAvailability.Unavailable,
            features.Resolve(PlatformFeatureFamily.OwnedRegionMapping).Availability);
        Assert.Equal(
            PlatformAuthorityFeatures.NeutralDomainBinding,
            provider.Descriptor.Features);
    }

    [Fact]
    public void CorePlatformAndRuntimeAssembliesDoNotReferenceHybridCpuImplementation()
    {
        static bool IsNeutralHybridCpuReference(System.Reflection.AssemblyName reference) =>
            string.Equals(reference.Name, "HybridCPU_NeutralRuntime", StringComparison.Ordinal);

        Assert.DoesNotContain(
            typeof(IPlatformAuthorityProvider).Assembly.GetReferencedAssemblies(),
            IsNeutralHybridCpuReference);
        Assert.DoesNotContain(
            typeof(RuntimeKernel).Assembly.GetReferencedAssemblies(),
            IsNeutralHybridCpuReference);
        Assert.Contains(
            typeof(HybridCpuPlatformAuthorityProvider).Assembly.GetReferencedAssemblies(),
            IsNeutralHybridCpuReference);
    }

    private static (SingPlus.Sip.SingProcess Process, ProcessHandle Handle) CreateProcess(
        RuntimeKernel kernel,
        ulong processId,
        ulong domainId,
        ulong generation)
    {
        var manifest = new SingProcessManifestV1(
            new ProcessId(processId),
            new DomainId(domainId),
            generation,
            $"hybridcpu-test-{processId}-{generation}",
            ExecutionRole.Sip,
            MemoryProfile.SipRegion);
        var created = kernel.CreateProcess(manifest);
        Assert.True(created.IsSuccess, created.Message);
        return (created.Value!, new ProcessHandle(manifest.ProcessId, manifest.Generation));
    }
}
