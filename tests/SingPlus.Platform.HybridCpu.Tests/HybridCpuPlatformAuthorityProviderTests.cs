using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuPlatformAuthorityProviderTests
{
    [Fact]
    public void ProviderBindsTransitionsAndRevokesRealNeutralRuntimeLease()
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

        var start = provider.TransitionDomainExecution(
            bind.Value,
            PlatformDomainExecutionTransition.Start);
        Assert.True(start.IsSuccess, start.Message);
        Assert.Equal(bind.Value, start.Value!.DomainLease);
        Assert.Equal(PlatformDomainExecutionState.Running, start.Value.State);

        var park = provider.TransitionDomainExecution(
            bind.Value,
            PlatformDomainExecutionTransition.Park);
        Assert.True(park.IsSuccess, park.Message);
        Assert.Equal(PlatformDomainExecutionState.Parked, park.Value!.State);

        var resume = provider.TransitionDomainExecution(
            bind.Value,
            PlatformDomainExecutionTransition.Resume);
        Assert.True(resume.IsSuccess, resume.Message);
        Assert.Equal(PlatformDomainExecutionState.Running, resume.Value!.State);

        var revoke = provider.RevokeDomain(bind.Value);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    [Fact]
    public void InvalidRealHybridCpuExecutionOrderIsDeniedWithoutProviderLeaseLoss()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var binding = provider.BindDomain(
            new PlatformDomainIdentity(new DomainId(11), 1)).Value!;

        var park = provider.TransitionDomainExecution(
            binding,
            PlatformDomainExecutionTransition.Park);

        Assert.Equal(PlatformAuthorityStatus.Denied, park.Status);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var start = provider.TransitionDomainExecution(
            binding,
            PlatformDomainExecutionTransition.Start);
        Assert.True(start.IsSuccess, start.Message);
        Assert.Equal(PlatformDomainExecutionState.Running, start.Value!.State);
    }

    [Fact]
    public void StaleOrWrongSubjectProviderLeaseIsRejectedBeforeExternalTransitionOrClosure()
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

        var staleTransition = provider.TransitionDomainExecution(
            stale,
            PlatformDomainExecutionTransition.Start);
        var wrongTransition = provider.TransitionDomainExecution(
            wrongSubject,
            PlatformDomainExecutionTransition.Start);
        var staleRevoke = provider.RevokeDomain(stale);
        var wrongRevoke = provider.RevokeDomain(wrongSubject);

        Assert.Equal(PlatformAuthorityStatus.Stale, staleTransition.Status);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain, wrongTransition.Status);
        Assert.Equal(PlatformAuthorityStatus.Stale, staleRevoke.Status);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain, wrongRevoke.Status);
        Assert.Equal(1, runtime.ActiveBindingCount);

        Assert.True(provider.RevokeDomain(binding).IsSuccess);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    [Fact]
    public void RuntimeKernelPublishesRunningAndParkedOnlyAfterRealHybridCpuTransitions()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, handle) = CreateProcess(kernel, 30, 300, 1);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        Assert.True(kernel.StartProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);
        Assert.True(kernel.ParkProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Parked, process.State);
        Assert.True(kernel.ResumeProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);
        Assert.Equal(1, runtime.ActiveBindingCount);
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
    public void WrongProcessGenerationIsRejectedBeforeHybridCpuAdmissionOrTransition()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, handle) = CreateProcess(kernel, 32, 320, 4);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        var stale = handle with { Generation = handle.Generation + 1 };

        var start = kernel.StartProcess(stale);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.StaleHandle, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1, runtime.ActiveBindingCount);
    }

    [Fact]
    public void PhaseTwoProcessTeardownClosesRunningHybridCpuDomainBeforePublishingExit()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, handle) = CreateProcess(kernel, 33, 330, 1);

        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        var binding = kernel.BindPlatformDomain(handle);
        Assert.True(binding.IsSuccess, binding.Message);
        Assert.True(kernel.StartProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var terminate = kernel.TerminateProcess(handle);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveBindingCount);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Fact]
    public void ProviderAdvertisesNeutralDomainsAsExecutableWithoutMappingClaims()
    {
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        var features = provider.QueryFeatures();

        Assert.Equal(
            PlatformFeatureAvailability.Executable,
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
