using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuPlatformAuthorityProviderTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AliasedForeignRootOwnerCannotAuthorizeOriginalKernelReclaim(bool completed)
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = CreateProcess(kernel, 60, 600, 1);
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        Assert.True(kernel.BindPlatformDomain(owner).IsSuccess);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary domains = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        IDictionary subjects = (global::System.Collections.IDictionary)provider.GetType().GetField("_activeSubjects", flags)!.GetValue(provider)!;
        object original = Assert.Single(domains.Values.Cast<object>());
        PlatformProviderDomainLease lease = (PlatformProviderDomainLease)original.GetType().GetProperty("Lease")!.GetValue(original)!;
        object? foreignClosure = null;
        if (completed)
        {
            PlatformProviderDomainLease other = provider.BindDomain(new PlatformDomainIdentity(new DomainId(601), new ProcessHandle(new ProcessId(61), 1))).Value!;
            Assert.True(provider.RevokeDomain(other).IsSuccess);
            foreignClosure = domains[other.LeaseId]!.GetType().GetProperty("RootCloseObservation")!.GetValue(domains[other.LeaseId]);
        }
        int calls = proxy.CloseCalls;
        object foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
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
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(602), new ProcessHandle(new ProcessId(62), 1));
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary bindings = (global::System.Collections.IDictionary)provider.GetType().GetField("_rootBinds", flags)!.GetValue(provider)!;
        object? original = null;
        proxy.BeforeRootBind = () => { proxy.BeforeRootBind = null; original = bindings[subject]; bindings.Remove(subject); };
        if (lostReply) proxy.RootBindOutcome = "throw";
        if (lostReply) Assert.ThrowsAny<Exception>(() => provider.BindDomain(subject));
        else Assert.False(provider.BindDomain(subject).IsSuccess);
        Assert.NotNull(original);
        PlatformProviderDomainLease lease = (PlatformProviderDomainLease)original.GetType().GetProperty("Lease")!.GetValue(original)!;
        IDictionary originals = (global::System.Collections.IDictionary)provider.GetType().GetField("_originalRootBinds", flags)!.GetValue(provider)!;
        Assert.Same(original, originals[lease.LeaseId]);
        Assert.Equal(false, original.GetType().GetProperty("InFlight")!.GetValue(original));
        object? retainedDomain = original.GetType().GetProperty("PublishedDomain")!.GetValue(original);
        if (lostReply) Assert.Null(retainedDomain);
        else
        {
            Assert.NotNull(retainedDomain);
            Assert.Equal(true,retainedDomain.GetType().GetProperty("CleanupOnly")!.GetValue(retainedDomain));
            Assert.Same(original,retainedDomain.GetType().GetProperty("BindSource")!.GetValue(retainedDomain));
        }
        object? raw = original.GetType().GetProperty("Observation")!.GetValue(original);
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
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(603), new ProcessHandle(new ProcessId(63), 1));
        PlatformProviderDomainLease lease = provider.BindDomain(subject).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary domains = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        IDictionary subjects = (global::System.Collections.IDictionary)provider.GetType().GetField("_activeSubjects", flags)!.GetValue(provider)!;
        object original = domains[lease.LeaseId]!;
        subjects.Remove(subject);
        Assert.False(provider.BindDomain(subject).IsSuccess);
        Assert.Equal(1, proxy.RootBindCalls);
        Assert.Equal(1, proxy.Runtime.ActiveBindingCount);
        subjects[subject] = original;
        Assert.True(provider.RevokeDomain(lease).IsSuccess);
        PlatformAuthorityResult<PlatformProviderDomainLease> fresh = provider.BindDomain(subject);
        Assert.True(fresh.IsSuccess);
        Assert.NotEqual(lease.LeaseId, fresh.Value!.LeaseId);
        Assert.True(provider.RevokeDomain(fresh.Value).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveBindingCount);
    }

    [Fact]
    public void ProviderBindsTransitionsAndRevokesRealNeutralRuntimeLease()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(
            new DomainId(10),
            new ProcessHandle(new ProcessId(12), 7));

        PlatformAuthorityResult<PlatformProviderDomainLease> bind = provider.BindDomain(subject);

        Assert.True(bind.IsSuccess, bind.Message);
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.Equal(subject, bind.Value!.Subject);
        Assert.Equal(new PlatformProviderLeaseGeneration(1), bind.Value.Generation);
        Assert.NotEqual(typeof(DomainId), typeof(NeutralDomainBindingHandle));
        Assert.NotEqual(typeof(PlatformProviderDomainLeaseId), typeof(NeutralDomainBindingHandle));
        Assert.NotEqual(typeof(PlatformProviderLeaseGeneration), typeof(NeutralDomainBindingEpoch));

        PlatformAuthorityResult<PlatformDomainExecutionTransitionResult> start = provider.TransitionDomainExecution(bind.Value, PlatformDomainExecutionTransition.Start);
        Assert.True(start.IsSuccess, start.Message);
        Assert.Equal(bind.Value, start.Value!.DomainLease);
        Assert.Equal(PlatformDomainExecutionState.Running, start.Value.State);

        PlatformAuthorityResult<PlatformDomainExecutionTransitionResult> park = provider.TransitionDomainExecution(bind.Value, PlatformDomainExecutionTransition.Park);
        Assert.True(park.IsSuccess, park.Message);
        Assert.Equal(PlatformDomainExecutionState.Parked, park.Value!.State);

        PlatformAuthorityResult<PlatformDomainExecutionTransitionResult> resume = provider.TransitionDomainExecution(bind.Value, PlatformDomainExecutionTransition.Resume);
        Assert.True(resume.IsSuccess, resume.Message);
        Assert.Equal(PlatformDomainExecutionState.Running, resume.Value!.State);

        PlatformAuthorityResult revoke = provider.RevokeDomain(bind.Value);
        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    [Fact]
    public void InvalidRealHybridCpuExecutionOrderIsDeniedWithoutProviderLeaseLoss()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease binding = provider.BindDomain(
            new PlatformDomainIdentity(
                new DomainId(11),
                new ProcessHandle(new ProcessId(13), 1))).Value!;

        PlatformAuthorityResult<PlatformDomainExecutionTransitionResult> park = provider.TransitionDomainExecution(binding, PlatformDomainExecutionTransition.Park);
        Assert.Equal(PlatformAuthorityStatus.Denied, park.Status);
        Assert.Equal(1, runtime.ActiveBindingCount);

        PlatformAuthorityResult<PlatformDomainExecutionTransitionResult> start = provider.TransitionDomainExecution(binding, PlatformDomainExecutionTransition.Start);
        Assert.True(start.IsSuccess, start.Message);
        Assert.Equal(PlatformDomainExecutionState.Running, start.Value!.State);
    }

    [Fact]
    public void ExternallyClosedNeutralLeaseRequiresExactProviderCloseBeforeSubjectReuse()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(
            new DomainId(12),
            new ProcessHandle(new ProcessId(14), 2));
        PlatformProviderDomainLease binding = provider.BindDomain(subject).Value!;
        NeutralDomainBindingLease neutralLease = ReadNeutralLeaseForTesting(provider);

        Assert.True(runtime.Close(neutralLease).IsClosed);
        PlatformAuthorityResult<PlatformDomainExecutionTransitionResult> transition = provider.TransitionDomainExecution(
            binding,
            PlatformDomainExecutionTransition.Start);

        Assert.Equal(PlatformAuthorityStatus.Revoked, transition.Status);
        Assert.Equal(PlatformAuthorityStatus.Revoked,
            provider.TransitionDomainExecution(
                binding,
                PlatformDomainExecutionTransition.Start).Status);
        Assert.Equal(PlatformAuthorityStatus.Denied, provider.BindDomain(subject).Status);

        PlatformAuthorityResult close = provider.RevokeDomain(binding);

        Assert.True(close.IsSuccess, close.Message);
        Assert.True(provider.BindDomain(subject).IsSuccess);
        Assert.Equal(1, runtime.ActiveBindingCount);
    }

    [Fact]
    public void StaleOrWrongSubjectProviderLeaseIsRejectedBeforeExternalTransitionOrClosure()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease binding = provider.BindDomain(
            new PlatformDomainIdentity(
                new DomainId(20),
                new ProcessHandle(new ProcessId(22), 3))).Value!;

        PlatformProviderDomainLease stale = binding with
        {
            Generation = new PlatformProviderLeaseGeneration(binding.Generation.Value + 1),
        };
        PlatformProviderDomainLease wrongSubject = binding with
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
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        (SingProcess? process, ProcessHandle handle) = CreateProcess(kernel, 30, 300, 1);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        KernelResult start = kernel.StartProcess(handle);
        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1, runtime.ActiveBindingCount);
    }

    [Fact]
    public void DuplicateSingSubjectDoesNotMaterializeSecondHybridCpuBinding()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateProcess(kernel, 31, 310, 1);

        KernelResult<PlatformDomainBinding> first = kernel.BindPlatformDomain(handle);
        KernelResult<PlatformDomainBinding> second = kernel.BindPlatformDomain(handle);

        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(KernelError.PlatformDenied, second.Error);
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.NotEqual(ProcessState.Exiting, process.State);
    }

    [Fact]
    public void SharedSingDomainProcessesReceiveIndependentHybridCpuBindings()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        (SingProcess _, ProcessHandle first) = CreateProcess(kernel, 34, 340, 1);
        (SingProcess _, ProcessHandle second) = CreateProcess(kernel, 35, 340, 1);
        Assert.True(kernel.AdmitProcess(first).IsSuccess);
        Assert.True(kernel.AdmitProcess(second).IsSuccess);

        KernelResult<PlatformDomainBinding> firstBinding = kernel.BindPlatformDomain(first);
        KernelResult<PlatformDomainBinding> secondBinding = kernel.BindPlatformDomain(second);

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
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        (SingProcess? process, ProcessHandle handle) = CreateProcess(kernel, 32, 320, 4);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        ProcessHandle stale = handle with { Generation = handle.Generation + 1 };

        KernelResult start = kernel.StartProcess(stale);

        Assert.Equal(KernelError.StaleHandle, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1, runtime.ActiveBindingCount);
    }

    [Fact]
    public void ProcessTeardownClosesModelOnlyHybridCpuDomainBeforePublishingExit()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        (SingProcess? process, ProcessHandle handle) = CreateProcess(kernel, 33, 330, 1);

        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        Assert.Equal(1, runtime.ActiveBindingCount);

        KernelResult terminate = kernel.TerminateProcess(handle);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveBindingCount);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Fact]
    public void ProviderAdvertisesExactImplementedFeaturesWithoutSchedulerPolicyClaim()
    {
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        PlatformFeatureManifest features = provider.QueryFeatures();

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
        Assert.Equal(PlatformDmaCopySubmissionContract.ContractVersion,
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
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        (SingProcess? process, ProcessHandle handle) = CreateProcess(kernel, 36, 360, 1);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(handle).Value!;
        PlatformExecutionPolicy policy = new PlatformExecutionPolicy(
            new ExecutionBudget(
                TimeSpan.FromMilliseconds(4),
                TimeSpan.FromMilliseconds(20)),
            PriorityClass.Interactive,
            LatencyHint.PreferLowLatency,
            ThroughputHint.Balanced);

        KernelResult<PlatformExecutionPolicyRegistration> configuration = kernel.ConfigurePlatformExecutionPolicy(
            handle,
            binding,
            policy);

        Assert.False(configuration.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, configuration.Error);
        Assert.Equal(default(PlatformExecutionPolicyRegistration), configuration.Value);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1, runtime.ActiveBindingCount);

        KernelResult start = kernel.StartProcess(handle);
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
        SingProcessManifestV1 manifest = new SingProcessManifestV1(
            new ProcessId(processId),
            new DomainId(domainId),
            generation,
            $"hybridcpu-test-{processId}-{generation}",
            ExecutionRole.Sip,
            MemoryProfile.SipRegion);
        KernelResult<SingProcess> created = kernel.CreateProcess(manifest);
        Assert.True(created.IsSuccess, created.Message);
        return (created.Value!, new ProcessHandle(manifest.ProcessId, manifest.Generation));
    }

    private static NeutralDomainBindingLease ReadNeutralLeaseForTesting(
        HybridCpuPlatformAuthorityProvider provider)
    {
        object domains = typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_domains", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .GetValue(provider)!;
        object values = domains.GetType().GetProperty("Values")!.GetValue(domains)!;
        object record = ((System.Collections.IEnumerable)values).Cast<object>().Single();
        return (NeutralDomainBindingLease)record.GetType()
            .GetProperty("HybridCpuLease")!
            .GetValue(record)!;
    }
}
