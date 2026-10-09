using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuPlatformAuthorityProviderTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AliasedForeignRootOwnerCannotAuthorizeOriginalKernelReclaim(bool completed)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = CreateProcess(kernel, 60, 600, 1);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        Assert.True(kernel.BindPlatformDomain(owner).IsSuccess);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        var subjects = (global::System.Collections.IDictionary)provider.GetType().GetField("_activeSubjects", flags)!.GetValue(provider)!;
        var original = Assert.Single(domains.Values.Cast<object>());
        var lease = (PlatformProviderDomainLease)original.GetType().GetProperty("Lease")!.GetValue(original)!;
        object? foreignClosure = null;
        if (completed)
        {
            var other = provider.BindDomain(new(new DomainId(601), new ProcessHandle(new ProcessId(61), 1))).Value!;
            Assert.True(provider.RevokeDomain(other).IsSuccess);
            foreignClosure = domains[other.LeaseId]!.GetType().GetProperty("RootCloseObservation")!.GetValue(domains[other.LeaseId]);
        }
        var calls = proxy.CloseCalls;
        var foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (completed) foreign.GetType().GetProperty("RootCloseObservation")!.SetValue(foreign, foreignClosure);
        domains[lease.LeaseId] = foreign; subjects[lease.Subject] = foreign;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(calls, proxy.CloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveBindingCount);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LostRootBindRouteCannotRepeatOriginalNeutralInvocation(bool lostReply)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(new DomainId(602), new ProcessHandle(new ProcessId(62), 1));
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var bindings = (global::System.Collections.IDictionary)provider.GetType().GetField("_rootBinds", flags)!.GetValue(provider)!;
        object? original = null;
        proxy.BeforeRootBind = () => { proxy.BeforeRootBind = null; original = bindings[subject]; bindings.Remove(subject); };
        if (lostReply) proxy.RootBindOutcome = "throw";
        if (lostReply) Assert.ThrowsAny<Exception>(() => provider.BindDomain(subject));
        else Assert.False(provider.BindDomain(subject).IsSuccess);
        Assert.NotNull(original);
        var lease = (PlatformProviderDomainLease)original.GetType().GetProperty("Lease")!.GetValue(original)!;
        var originals = (global::System.Collections.IDictionary)provider.GetType().GetField("_originalRootBinds", flags)!.GetValue(provider)!;
        Assert.Same(original, originals[lease.LeaseId]);
        Assert.Equal(false, original.GetType().GetProperty("InFlight")!.GetValue(original));
        Assert.Null(original.GetType().GetProperty("PublishedDomain")!.GetValue(original));
        var raw = original.GetType().GetProperty("Observation")!.GetValue(original);
        if (lostReply) Assert.Null(raw);
        else Assert.True(Assert.IsType<NeutralDomainBindResult>(raw).IsBound);
        proxy.RootBindOutcome = null;
        Assert.False(provider.BindDomain(subject).IsSuccess);
        Assert.Equal(1, proxy.RootBindCalls);
        Assert.Equal(1, proxy.Runtime.ActiveBindingCount);
    }

    [Fact]
    public void MissingSubjectRouteCannotCreateSecondOwnerAndClosedOriginalAllowsFreshBind()
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(new DomainId(603), new ProcessHandle(new ProcessId(63), 1));
        var lease = provider.BindDomain(subject).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        var subjects = (global::System.Collections.IDictionary)provider.GetType().GetField("_activeSubjects", flags)!.GetValue(provider)!;
        var original = domains[lease.LeaseId]!;
        subjects.Remove(subject);
        Assert.False(provider.BindDomain(subject).IsSuccess);
        Assert.Equal(1, proxy.RootBindCalls);
        Assert.Equal(1, proxy.Runtime.ActiveBindingCount);
        subjects[subject] = original;
        Assert.True(provider.RevokeDomain(lease).IsSuccess);
        var fresh = provider.BindDomain(subject);
        Assert.True(fresh.IsSuccess);
        Assert.NotEqual(lease.LeaseId, fresh.Value!.LeaseId);
        Assert.True(provider.RevokeDomain(fresh.Value).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveBindingCount);
    }

    [Fact]
    public void ProviderBindsTransitionsAndRevokesRealNeutralRuntimeLease()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(
            new DomainId(10),
            new ProcessHandle(new ProcessId(12), 7));

        var bind = provider.BindDomain(subject);

        Assert.True(bind.IsSuccess, bind.Message);
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.Equal(subject, bind.Value!.Subject);
        Assert.Equal(new PlatformProviderLeaseGeneration(1), bind.Value.Generation);
        Assert.NotEqual(typeof(DomainId), typeof(NeutralDomainBindingHandle));
        Assert.NotEqual(typeof(PlatformProviderDomainLeaseId), typeof(NeutralDomainBindingHandle));
        Assert.NotEqual(typeof(PlatformProviderLeaseGeneration), typeof(NeutralDomainBindingEpoch));

        var start = provider.TransitionDomainExecution(bind.Value, PlatformDomainExecutionTransition.Start);
        Assert.True(start.IsSuccess, start.Message);
        Assert.Equal(bind.Value, start.Value!.DomainLease);
        Assert.Equal(PlatformDomainExecutionState.Running, start.Value.State);

        var park = provider.TransitionDomainExecution(bind.Value, PlatformDomainExecutionTransition.Park);
        Assert.True(park.IsSuccess, park.Message);
        Assert.Equal(PlatformDomainExecutionState.Parked, park.Value!.State);

        var resume = provider.TransitionDomainExecution(bind.Value, PlatformDomainExecutionTransition.Resume);
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
            new PlatformDomainIdentity(
                new DomainId(11),
                new ProcessHandle(new ProcessId(13), 1))).Value!;

        var park = provider.TransitionDomainExecution(binding, PlatformDomainExecutionTransition.Park);
        Assert.Equal(PlatformAuthorityStatus.Denied, park.Status);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var start = provider.TransitionDomainExecution(binding, PlatformDomainExecutionTransition.Start);
        Assert.True(start.IsSuccess, start.Message);
        Assert.Equal(PlatformDomainExecutionState.Running, start.Value!.State);
    }

    [Fact]
    public void ExternallyClosedNeutralLeaseRequiresExactProviderCloseBeforeSubjectReuse()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(
            new DomainId(12),
            new ProcessHandle(new ProcessId(14), 2));
        var binding = provider.BindDomain(subject).Value!;
        var neutralLease = ReadNeutralLeaseForTesting(provider);

        Assert.True(runtime.Close(neutralLease).IsClosed);
        var transition = provider.TransitionDomainExecution(
            binding,
            PlatformDomainExecutionTransition.Start);

        Assert.Equal(PlatformAuthorityStatus.Revoked, transition.Status);
        Assert.Equal(PlatformAuthorityStatus.Revoked,
            provider.TransitionDomainExecution(
                binding,
                PlatformDomainExecutionTransition.Start).Status);
        Assert.Equal(PlatformAuthorityStatus.Denied, provider.BindDomain(subject).Status);

        var close = provider.RevokeDomain(binding);

        Assert.True(close.IsSuccess, close.Message);
        Assert.True(provider.BindDomain(subject).IsSuccess);
        Assert.Equal(1, runtime.ActiveBindingCount);
    }

    [Fact]
    public void StaleOrWrongSubjectProviderLeaseIsRejectedBeforeExternalTransitionOrClosure()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var binding = provider.BindDomain(
            new PlatformDomainIdentity(
                new DomainId(20),
                new ProcessHandle(new ProcessId(22), 3))).Value!;

        var stale = binding with
        {
            Generation = new PlatformProviderLeaseGeneration(binding.Generation.Value + 1),
        };
        var wrongSubject = binding with
        {
            Subject = new PlatformDomainIdentity(
                new DomainId(21),
                binding.Subject.Process),
        };

        Assert.Equal(PlatformAuthorityStatus.Stale,
            provider.TransitionDomainExecution(stale, PlatformDomainExecutionTransition.Start).Status);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain,
            provider.TransitionDomainExecution(wrongSubject, PlatformDomainExecutionTransition.Start).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale, provider.RevokeDomain(stale).Status);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain, provider.RevokeDomain(wrongSubject).Status);
        Assert.Equal(1, runtime.ActiveBindingCount);

        Assert.True(provider.RevokeDomain(binding).IsSuccess);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    [Fact]
    public void ModelOnlyRuntimeAdmissionCannotPublishExecutableProcessState()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, handle) = CreateProcess(kernel, 30, 300, 1);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        var start = kernel.StartProcess(handle);
        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
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
        Assert.Equal(KernelError.PlatformDenied, second.Error);
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.NotEqual(ProcessState.Exiting, process.State);
    }

    [Fact]
    public void SharedSingDomainProcessesReceiveIndependentHybridCpuBindings()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, first) = CreateProcess(kernel, 34, 340, 1);
        var (_, second) = CreateProcess(kernel, 35, 340, 1);
        Assert.True(kernel.AdmitProcess(first).IsSuccess);
        Assert.True(kernel.AdmitProcess(second).IsSuccess);

        var firstBinding = kernel.BindPlatformDomain(first);
        var secondBinding = kernel.BindPlatformDomain(second);

        Assert.True(firstBinding.IsSuccess, firstBinding.Message);
        Assert.True(secondBinding.IsSuccess, secondBinding.Message);
        Assert.NotEqual(firstBinding.Value!.Subject, secondBinding.Value!.Subject);
        Assert.Equal(2, runtime.ActiveBindingCount);

        Assert.True(kernel.TerminateProcess(first).IsSuccess);
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.True(kernel.TerminateProcess(second).IsSuccess);
        Assert.Equal(0, runtime.ActiveBindingCount);
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

        Assert.Equal(KernelError.StaleHandle, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1, runtime.ActiveBindingCount);
    }

    [Fact]
    public void ProcessTeardownClosesModelOnlyHybridCpuDomainBeforePublishingExit()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, handle) = CreateProcess(kernel, 33, 330, 1);

        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var terminate = kernel.TerminateProcess(handle);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveBindingCount);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Fact]
    public void ProviderAdvertisesExactImplementedFeaturesWithoutSchedulerPolicyClaim()
    {
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        var features = provider.QueryFeatures();

        Assert.Equal(NeutralRuntimeImplementationProfile.ModelOnly, provider.ImplementationProfile);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.NeutralDomains).Availability);
        Assert.Equal(PlatformDomainContract.ContractVersion,
            features.Resolve(PlatformFeatureFamily.NeutralDomains).ContractVersion);
        Assert.Equal(PlatformOwnedRegionMappingContract.ContractVersion,
            features.Resolve(PlatformFeatureFamily.OwnedRegionMapping).ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.OwnedRegionMapping).Availability);
        Assert.Equal(PlatformRegionVisibilityContract.ContractVersion,
            features.Resolve(PlatformFeatureFamily.ExplicitMemoryVisibility).ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.ExplicitMemoryVisibility).Availability);
        Assert.Equal(PlatformDmaGrantContract.ContractVersion,
            features.Resolve(PlatformFeatureFamily.DmaMapping).ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.DmaMapping).Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.DmaMapping).Availability);
        Assert.Equal(0u,
            features.Resolve(PlatformFeatureFamily.ExecutionPolicy).ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            features.Resolve(PlatformFeatureFamily.ExecutionPolicy).Availability);
        Assert.DoesNotContain(
            typeof(IPlatformExecutionPolicyProvider),
            provider.GetType().GetInterfaces());
        Assert.Equal(
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping,
            provider.Descriptor.Features);
    }

    [Fact]
    public void UnsupportedSchedulerPolicyDoesNotPromoteModelOnlyBindingToExecutable()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, handle) = CreateProcess(kernel, 36, 360, 1);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        var binding = kernel.BindPlatformDomain(handle).Value!;
        var policy = new PlatformExecutionPolicy(
            new ExecutionBudget(
                TimeSpan.FromMilliseconds(4),
                TimeSpan.FromMilliseconds(20)),
            PriorityClass.Interactive,
            LatencyHint.PreferLowLatency,
            ThroughputHint.Balanced);

        var configuration = kernel.ConfigurePlatformExecutionPolicy(
            handle,
            binding,
            policy);

        Assert.False(configuration.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, configuration.Error);
        Assert.Equal(default(PlatformExecutionPolicyRegistration), configuration.Value);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var start = kernel.StartProcess(handle);
        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1, runtime.ActiveBindingCount);
    }

    [Fact]
    public void CorePlatformAndRuntimeAssembliesDoNotReferenceHybridCpuImplementation()
    {
        static bool IsNeutralHybridCpuReference(System.Reflection.AssemblyName reference) =>
            string.Equals(reference.Name, "HybridCPU_NeutralRuntime", StringComparison.Ordinal);

        Assert.DoesNotContain(typeof(IPlatformAuthorityProvider).Assembly.GetReferencedAssemblies(),
            IsNeutralHybridCpuReference);
        Assert.DoesNotContain(typeof(RuntimeKernel).Assembly.GetReferencedAssemblies(),
            IsNeutralHybridCpuReference);
        Assert.Contains(typeof(HybridCpuPlatformAuthorityProvider).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "HybridCPU_NeutralRuntime.Contracts", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(HybridCpuPlatformAuthorityProvider).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "HybridCpu_ExecutableAdapter", StringComparison.Ordinal));
    }

    internal static (SingPlus.Sip.SingProcess Process, ProcessHandle Handle) CreateProcess(
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

    private static NeutralDomainBindingLease ReadNeutralLeaseForTesting(
        HybridCpuPlatformAuthorityProvider provider)
    {
        var domains = typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_domains", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .GetValue(provider)!;
        var values = domains.GetType().GetProperty("Values")!.GetValue(domains)!;
        var record = ((System.Collections.IEnumerable)values).Cast<object>().Single();
        return (NeutralDomainBindingLease)record.GetType()
            .GetProperty("HybridCpuLease")!
            .GetValue(record)!;
    }
}
