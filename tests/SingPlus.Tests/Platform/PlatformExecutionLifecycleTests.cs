using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformExecutionLifecycleTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExecutionReturnBalancesOnlyOriginalSourceInterlock(bool epoch)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle subject) = CreateAdmittedProcess(kernel, 819, 919, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary roots = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = roots[binding.BindingId]!;
        object foreign = null!;
        provider.TransitionCallback = () =>
        {
            foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            if (epoch) foreign.GetType().GetProperty("BackendEpoch")!.SetValue(foreign, new PlatformBackendEpoch(2));
            else foreign.GetType().GetProperty("ProviderIncarnation")!.SetValue(foreign, new PlatformProviderIncarnation(2));
            roots[binding.BindingId] = foreign;
        };
        Assert.False(kernel.StartProcess(subject).IsSuccess);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(true, foreign.GetType().GetProperty("ExecutionTransitionInFlight")!.GetValue(foreign));
        Assert.Equal(false, original.GetType().GetProperty("ExecutionTransitionInFlight")!.GetValue(original));
        Assert.False(kernel.StartProcess(subject).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(0, provider.RevokeCalls);
        roots[binding.BindingId] = original; // controlled exact-owner repair
        provider.TransitionCallback = null;
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ControllerLossInsideExecutionCallbackRetainsOriginalBindingUntilActualClosure(int fault)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle principal) = CreateAdmittedProcess(kernel, 1116, 2116, 1);
        CapabilityId control = kernel.MintCapability(new DomainId(2116), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        byte[] image = new byte[] { 11, 16 };
        ServiceManifestV1 manifest = new ServiceManifestV1(new("controller-execution"), new("1"),
            Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(1117, 2117));
        PlatformDomainBinding binding = default;
        kernel.BeforeComponentExecutionStartQualificationHook = () =>
            binding = kernel.BindPlatformDomain(new ProcessHandle(manifest.Process.ProcessId, 1)).Value!;
        provider.TransitionCallback = () =>
        {
            if (fault == 0) Assert.True(kernel.CapabilityAuthority.Revoke(control).IsSuccess);
            if (fault == 1) kernel.CapabilityAuthority.RevokeAllForDomain(new DomainId(2116));
            if (fault == 2) Assert.True(kernel.TerminateProcess(principal).IsSuccess);
        };
        Assert.Equal(KernelError.SupervisorDenied, kernel.AdmitComponent(new(manifest, image), principal, control).Error);
        Assert.Single(provider.Transitions);
        Assert.Equal(1, provider.RevokeCalls);
        ComponentLifecycleSnapshot component = kernel.QueryComponent(manifest.Identity).Value!;
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.State);
        Assert.True(kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(manifest.Identity, component.Process).IsSuccess);
        Assert.False(kernel.PlatformAuthority.ValidateDomain(binding, binding.Subject).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task BlockedExecutionCallbackAllowsCapabilitySourceLossAndCannotPublishOrRepeat(bool resume, bool domainLoss)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        CapabilityRequirementV1 requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "blocked-execution", CapabilityRights.Read);
        (SingProcess _, ProcessHandle process) = TestFixtures.Create(kernel, 1114, 2114, capabilities: [requirement]);
        CapabilityId capability = kernel.MintCapability(new DomainId(2114), process, requirement.ResourceKind,
            requirement.ResourceId, requirement.Rights).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcess(process).IsSuccess);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(process).Value!;
        if (resume)
        {
            Assert.True(kernel.StartProcess(process).IsSuccess);
            Assert.True(kernel.ParkProcess(process).IsSuccess);
        }
        int before = provider.Transitions.Count;
        using ManualResetEventSlim release = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.TransitionCallback = () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Task<KernelResult> work = Task.Run(() => resume ? kernel.ResumeProcess(process) : kernel.StartProcess(process));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(() =>
            {
                Assert.Equal(resume ? ProcessState.Parked : ProcessState.Admitted, kernel.Processes.Resolve(process).Value!.State);
                if (domainLoss) kernel.CapabilityAuthority.RevokeAllForDomain(new DomainId(2114));
                else Assert.True(kernel.RevokeCapability(capability).IsSuccess);
                Assert.True(ReadPending(kernel.PlatformAuthority, binding.BindingId));
                Assert.Equal(KernelError.PlatformBindingActive,
                    (resume ? kernel.ResumeProcess(process) : kernel.StartProcess(process)).Error);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.MissingCapability, (await work.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        Assert.False(ReadPending(kernel.PlatformAuthority, binding.BindingId));
        Assert.Equal(before + 1, provider.Transitions.Count);
        if (!domainLoss)
            Assert.True(kernel.MintCapability(new DomainId(2114), process, requirement.ResourceKind,
                requirement.ResourceId, requirement.Rights).IsSuccess);
        Assert.False((resume ? kernel.ResumeProcess(process) : kernel.StartProcess(process)).IsSuccess);
        Assert.Equal(before + 1, provider.Transitions.Count);
        provider.TransitionCallback = null;
        Assert.True(kernel.TerminateProcess(process).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(process).Value);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void MandatoryCapabilityLossDeniesStartOrResumeBeforeProvider(bool bound, bool resume)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        CapabilityRequirementV1 requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "execution-data", CapabilityRights.Read);
        (SingProcess _, ProcessHandle process) = TestFixtures.Create(kernel, 1112, 2112, capabilities: [requirement]);
        CapabilityId capability = kernel.MintCapability(new DomainId(2112), process, requirement.ResourceKind,
            requirement.ResourceId, requirement.Rights).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcess(process).IsSuccess);
        if (bound) Assert.True(kernel.BindPlatformDomain(process).IsSuccess);
        if (resume)
        {
            Assert.True(kernel.StartProcess(process).IsSuccess);
            Assert.True(kernel.ParkProcess(process).IsSuccess);
        }
        int calls = provider.Transitions.Count;
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(KernelError.MissingCapability, (resume ? kernel.ResumeProcess(process) : kernel.StartProcess(process)).Error);
        Assert.Equal(calls, provider.Transitions.Count);
        Assert.Equal(resume ? ProcessState.Parked : ProcessState.Admitted, kernel.Processes.Resolve(process).Value!.State);
        Assert.True(kernel.MintCapability(new DomainId(2112), process, requirement.ResourceKind,
            requirement.ResourceId, requirement.Rights).IsSuccess);
        Assert.True((resume ? kernel.ResumeProcess(process) : kernel.StartProcess(process)).IsSuccess);
        Assert.True(kernel.TerminateProcess(process).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExecutionCallbackCapabilityLossDeniesPublicationAndRequiresActualClosure(bool resume)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        CapabilityRequirementV1 requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "execution-final", CapabilityRights.Read);
        (SingProcess _, ProcessHandle process) = TestFixtures.Create(kernel, 1113, 2113, capabilities: [requirement]);
        CapabilityId capability = kernel.MintCapability(new DomainId(2113), process, requirement.ResourceKind,
            requirement.ResourceId, requirement.Rights).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcess(process).IsSuccess);
        Assert.True(kernel.BindPlatformDomain(process).IsSuccess);
        if (resume)
        {
            Assert.True(kernel.StartProcess(process).IsSuccess);
            Assert.True(kernel.ParkProcess(process).IsSuccess);
        }
        int calls = provider.Transitions.Count;
        provider.TransitionCallback = () => Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(KernelError.MissingCapability, (resume ? kernel.ResumeProcess(process) : kernel.StartProcess(process)).Error);
        Assert.Equal(calls + 1, provider.Transitions.Count);
        Assert.Equal(resume ? ProcessState.Parked : ProcessState.Admitted, kernel.Processes.Resolve(process).Value!.State);
        provider.TransitionCallback = null;
        Assert.True(kernel.TerminateProcess(process).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void BoundProcessPublishesLocalStateOnlyAfterProviderTransitionSucceeds()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 801, 901, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        Assert.True(kernel.StartProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);
        Assert.True(kernel.ParkProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Parked, process.State);
        Assert.True(kernel.ResumeProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);

        Assert.Equal(
            new[]
            {
                PlatformDomainExecutionTransition.Start,
                PlatformDomainExecutionTransition.Park,
                PlatformDomainExecutionTransition.Resume,
            },
            provider.Transitions);
    }

    [Fact]
    public void ProviderDenialLeavesAdmittedProcessUnpublished()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Deny);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 802, 902, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        KernelResult start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(new[] { PlatformDomainExecutionTransition.Start }, provider.Transitions);
    }

    [Fact]
    public void ProviderDenialRetainsStateAndRequiresExplicitClosureBeforeTransitionRetry()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 823, 923, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        Assert.True(kernel.StartProcess(handle).IsSuccess);

        provider.Behavior = LifecycleBehavior.Deny;
        KernelResult deniedPark = kernel.ParkProcess(handle);

        Assert.False(deniedPark.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, deniedPark.Error);
        Assert.Equal(ProcessState.Running, process.State);

        provider.Behavior = LifecycleBehavior.Success;
        Assert.Equal(KernelError.PlatformFaulted, kernel.ParkProcess(handle).Error);
        Assert.True(kernel.TerminateProcess(handle).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);

        (SingProcess? parked, ProcessHandle parkedHandle) = CreateAdmittedProcess(kernel, 825, 925, 1);
        Assert.True(kernel.BindPlatformDomain(parkedHandle).IsSuccess);
        Assert.True(kernel.StartProcess(parkedHandle).IsSuccess);
        Assert.True(kernel.ParkProcess(parkedHandle).IsSuccess);
        provider.Behavior = LifecycleBehavior.Deny;
        KernelResult deniedResume = kernel.ResumeProcess(parkedHandle);
        Assert.False(deniedResume.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, deniedResume.Error);
        Assert.Equal(ProcessState.Parked, parked.State);
        Assert.Equal(KernelError.PlatformFaulted, kernel.ResumeProcess(parkedHandle).Error);
        Assert.True(kernel.TerminateProcess(parkedHandle).IsSuccess);
    }

    [Fact]
    public void ProviderRevocationQuarantinesBindingUntilExplicitCloseSucceeds()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 803, 903, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        Assert.True(kernel.StartProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);

        provider.Behavior = LifecycleBehavior.Revoked;
        KernelResult park = kernel.ParkProcess(handle);

        Assert.False(park.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingRevoked, park.Error);
        Assert.Equal(ProcessState.Running, process.State);

        PlatformDomainExecutionTransition[] transitionsAfterRevocation = provider.Transitions.ToArray();
        KernelResult secondPark = kernel.ParkProcess(handle);
        Assert.False(secondPark.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, secondPark.Error);
        Assert.Equal(ProcessState.Running, process.State);
        Assert.Equal(transitionsAfterRevocation, provider.Transitions);

        KernelResult terminate = kernel.TerminateProcess(handle);
        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Fact]
    public void ProviderFaultQuarantinesBindingAndBlocksTransitionRetry()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Faulted);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 820, 920, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        KernelResult start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Single(provider.Transitions);

        KernelResult retry = kernel.StartProcess(handle);

        Assert.False(retry.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Single(provider.Transitions);
        Assert.True(kernel.TerminateProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Exited, process.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedOrStaleProviderSuccessIsQuarantinedWithoutLocalPublication(
        bool staleGeneration)
    {
        LifecycleProvider provider = new LifecycleProvider(
            staleGeneration
                ? LifecycleBehavior.StaleLeaseResult
                : LifecycleBehavior.WrongLeaseResult);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 804, 904, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        KernelResult start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);

        int transitionCount = provider.Transitions.Count;
        KernelResult retry = kernel.StartProcess(handle);
        Assert.False(retry.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Equal(transitionCount, provider.Transitions.Count);

        KernelResult terminate = kernel.TerminateProcess(handle);
        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void BoundProviderWithoutExecutionContractCannotFallBackToLocalOnlyState()
    {
        RuntimeKernel kernel = new RuntimeKernel(new BindingOnlyProvider());
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 805, 905, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        KernelResult start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
    }

    [Fact]
    public void RuntimeAdmissionEvidenceCannotPublishExecutableProcessState()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            FeatureAvailability = PlatformFeatureAvailability.RuntimeAdmission,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 822, 922, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        KernelResult start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Empty(provider.Transitions);
    }

    [Fact]
    public void StaleProcessGenerationIsRejectedBeforeProviderTransition()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 806, 906, 4);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        ProcessHandle stale = handle with { Generation = handle.Generation + 1 };

        KernelResult start = kernel.StartProcess(stale);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.StaleHandle, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Empty(provider.Transitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunningOrParkedLocalProcessCannotLateBindExternalReadyDomain(bool park)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 807, 907, 1);
        Assert.True(kernel.StartProcess(handle).IsSuccess);
        if (park)
            Assert.True(kernel.ParkProcess(handle).IsSuccess);
        ProcessState stateBeforeBind = process.State;

        KernelResult<PlatformDomainBinding> bind = kernel.BindPlatformDomain(handle);

        Assert.False(bind.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, bind.Error);
        Assert.Equal(stateBeforeBind, process.State);
        Assert.Equal(0, provider.BindCalls);
        Assert.Empty(provider.Transitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunningOrParkedBoundProcessCanDetachOnlyThroughTeardown(bool park)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 808, 908, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(handle).Value!;
        Assert.True(kernel.StartProcess(handle).IsSuccess);
        if (park)
            Assert.True(kernel.ParkProcess(handle).IsSuccess);
        ProcessState stateBeforeRevoke = process.State;

        KernelResult revoke = kernel.RevokePlatformDomain(handle, binding);

        Assert.False(revoke.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, revoke.Error);
        Assert.Equal(stateBeforeRevoke, process.State);
        Assert.Equal(0, provider.RevokeCalls);

        KernelResult nextTransition = park
            ? kernel.ResumeProcess(handle)
            : kernel.ParkProcess(handle);
        Assert.True(nextTransition.IsSuccess, nextTransition.Message);

        KernelResult terminate = kernel.TerminateProcess(handle);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Faulted, KernelError.PlatformFaulted)]
    [InlineData(PlatformAuthorityStatus.Revoked, KernelError.PlatformBindingRevoked)]
    [InlineData(PlatformAuthorityStatus.Stale, KernelError.StaleGeneration)]
    [InlineData(PlatformAuthorityStatus.WrongDomain, KernelError.WrongPlatformDomain)]
    public void PostStartProviderCloseFailurePinsTeardownBeforeLocalReclaim(
        PlatformAuthorityStatus providerStatus,
        KernelError expectedError)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            RevokeStatus = providerStatus,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 811, 911, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        Assert.True(kernel.StartProcess(handle).IsSuccess);

        KernelResult terminate = kernel.TerminateProcess(handle);

        Assert.False(terminate.IsSuccess);
        Assert.Equal(expectedError, terminate.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.True(kernel.Processes.Resolve(handle).IsSuccess);

        KernelResult<ProcessTeardownSnapshot> teardown = kernel.QueryProcessTeardown(handle);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, teardown.Value!.Phase);
        Assert.False(teardown.Value.PlatformDomainClosed);
        Assert.False(teardown.Value.LocalReclaimCompleted);
    }

    [Fact]
    public void FailedStartMayDetachWhileStateRemainsAdmitted()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Deny);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 809, 909, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(handle).Value!;
        Assert.Equal(KernelError.PlatformDenied, kernel.StartProcess(handle).Error);
        Assert.Equal(ProcessState.Admitted, process.State);

        KernelResult revoke = kernel.RevokePlatformDomain(handle, binding);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.True(kernel.StartProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);
    }

    [Fact]
    public void FaultedPreStartCloseQuarantinesBindingAndBlocksExecution()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            RevokeStatus = PlatformAuthorityStatus.Faulted,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 821, 921, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(handle).Value!;

        KernelResult close = kernel.RevokePlatformDomain(handle, binding);

        Assert.False(close.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, close.Error);
        Assert.Equal(ProcessState.Admitted, process.State);

        KernelResult start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, start.Error);
        Assert.Empty(provider.Transitions);

        provider.RevokeStatus = null;
        Assert.False(kernel.TerminateProcess(handle).IsSuccess);
        Assert.True(kernel.Processes.Resolve(handle).IsSuccess);
        Assert.NotEqual(ProcessState.Exited, process.State);
        Assert.False(kernel.QueryProcessTeardown(handle).Value.LocalReclaimCompleted);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void StaleOrForgedPreExecutionIdentityCannotDetachBinding()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 810, 910, 4);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(handle).Value!;

        ProcessHandle staleSubject = handle with { Generation = handle.Generation + 1 };
        KernelResult stale = kernel.RevokePlatformDomain(staleSubject, binding);
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleHandle, stale.Error);

        PlatformDomainBinding staleBinding = binding with
        {
            Generation = new PlatformDomainBindingGeneration(binding.Generation.Value + 1),
        };
        KernelResult staleBindingResult = kernel.RevokePlatformDomain(handle, staleBinding);
        Assert.False(staleBindingResult.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, staleBindingResult.Error);

        PlatformDomainBinding unknownBinding = binding with
        {
            BindingId = new PlatformDomainBindingId(binding.BindingId.Value + 1000),
        };
        KernelResult unknownBindingResult = kernel.RevokePlatformDomain(handle, unknownBinding);
        Assert.False(unknownBindingResult.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingNotFound, unknownBindingResult.Error);

        PlatformDomainBinding forged = binding with
        {
            Subject = new PlatformDomainIdentity(
                new DomainId(binding.Subject.DomainId.Value + 1),
                binding.Subject.Process),
        };
        KernelResult forgedResult = kernel.RevokePlatformDomain(handle, forged);
        Assert.False(forgedResult.IsSuccess);
        Assert.Equal(KernelError.WrongPlatformDomain, forgedResult.Error);

        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(0, provider.RevokeCalls);
        Assert.True(kernel.RevokePlatformDomain(handle, binding).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void SharedDomainSiblingCannotDetachAnotherProcessBinding()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? ownerProcess, ProcessHandle owner) = CreateAdmittedProcess(kernel, 812, 912, 1);
        (SingProcess? siblingProcess, ProcessHandle sibling) = CreateAdmittedProcess(kernel, 813, 912, 1);
        PlatformDomainBinding ownerBinding = kernel.BindPlatformDomain(owner).Value!;
        Assert.True(kernel.StartProcess(owner).IsSuccess);

        KernelResult forgedDetach = kernel.RevokePlatformDomain(sibling, ownerBinding);

        Assert.False(forgedDetach.IsSuccess);
        Assert.Equal(KernelError.WrongPlatformDomain, forgedDetach.Error);
        Assert.Equal(ProcessState.Running, ownerProcess.State);
        Assert.Equal(ProcessState.Admitted, siblingProcess.State);
        Assert.Equal(0, provider.RevokeCalls);

        Assert.True(kernel.ParkProcess(owner).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.True(kernel.TerminateProcess(sibling).IsSuccess);
    }

    [Fact]
    public void SharedDomainPeersOwnIndependentPlatformBindings()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle first) = CreateAdmittedProcess(kernel, 814, 914, 1);
        (SingProcess _, ProcessHandle second) = CreateAdmittedProcess(kernel, 815, 914, 1);

        KernelResult<PlatformDomainBinding> firstBinding = kernel.BindPlatformDomain(first);
        KernelResult<PlatformDomainBinding> secondBinding = kernel.BindPlatformDomain(second);

        Assert.True(firstBinding.IsSuccess, firstBinding.Message);
        Assert.True(secondBinding.IsSuccess, secondBinding.Message);
        Assert.NotEqual(firstBinding.Value!.BindingId, secondBinding.Value!.BindingId);
        Assert.NotEqual(firstBinding.Value.Subject, secondBinding.Value.Subject);
        Assert.Equal(first.ProcessId, firstBinding.Value.Subject.ProcessId);
        Assert.Equal(second.ProcessId, secondBinding.Value.Subject.ProcessId);

        Assert.True(kernel.StartProcess(first).IsSuccess);
        Assert.True(kernel.StartProcess(second).IsSuccess);
        Assert.True(kernel.TerminateProcess(first).IsSuccess);
        Assert.True(kernel.TerminateProcess(second).IsSuccess);
        Assert.Equal(2, provider.RevokeCalls);
    }

    [Fact]
    public void ClosedOldBindingCannotUnreserveLiveReplacement()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle handle) = CreateAdmittedProcess(kernel, 818, 918, 1);
        PlatformDomainBinding oldBinding = kernel.BindPlatformDomain(handle).Value!;
        Assert.True(kernel.RevokePlatformDomain(handle, oldBinding).IsSuccess);
        PlatformDomainBinding replacement = kernel.BindPlatformDomain(handle).Value!;

        KernelResult repeatOldClose = kernel.RevokePlatformDomain(handle, oldBinding);
        KernelResult<PlatformDomainBinding> duplicate = kernel.BindPlatformDomain(handle);

        Assert.True(repeatOldClose.IsSuccess, repeatOldClose.Message);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, duplicate.Error);
        Assert.Equal(2, provider.BindCalls);
        Assert.Equal(1, provider.RevokeCalls);

        Assert.True(kernel.StartProcess(handle).IsSuccess);
        Assert.True(kernel.TerminateProcess(handle).IsSuccess);
        Assert.Equal(2, provider.RevokeCalls);
        Assert.NotEqual(oldBinding.BindingId, replacement.BindingId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UnmaterializedProviderLeaseCannotBecomeLocalBinding(
        bool zeroLeaseId,
        bool zeroLeaseGeneration)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            ReturnZeroLeaseId = zeroLeaseId,
            ReturnZeroLeaseGeneration = zeroLeaseGeneration,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 816, 916, 1);

        KernelResult<PlatformDomainBinding> bind = kernel.BindPlatformDomain(handle);

        Assert.False(bind.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bind.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1, provider.BindCalls);
        Assert.Equal(1, provider.RevokeCalls);

        KernelResult start = kernel.StartProcess(handle);
        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Empty(provider.Transitions);
        Assert.False(kernel.TerminateProcess(handle).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.True(kernel.Processes.Resolve(handle).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(handle).Value!.LocalReclaimCompleted);
    }

    [Fact]
    public void FailedMalformedLeaseCleanupRemainsTrackedAndPinsLocalReclaim()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            ReturnZeroLeaseId = true,
            RevokeStatus = PlatformAuthorityStatus.Faulted,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 819, 919, 1);

        KernelResult<PlatformDomainBinding> bind = kernel.BindPlatformDomain(handle);
        KernelResult start = kernel.StartProcess(handle);
        KernelResult terminate = kernel.TerminateProcess(handle);

        Assert.False(bind.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bind.Error);
        Assert.Contains("quarantined", bind.Message!, StringComparison.Ordinal);
        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, start.Error);
        Assert.Empty(provider.Transitions);
        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.Equal(1, provider.RevokeCalls);

        KernelResult<ProcessTeardownSnapshot> teardown = kernel.QueryProcessTeardown(handle);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, teardown.Value!.Phase);
        Assert.False(teardown.Value.PlatformDomainClosed);
        Assert.False(teardown.Value.LocalReclaimCompleted);
        Assert.True(kernel.Processes.Resolve(handle).IsSuccess);
    }

    [Fact]
    public void RecoveredMalformedLeaseCleanupCannotProveOriginalEffectClosure()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            ReturnZeroLeaseId = true,
            RevokeStatus = PlatformAuthorityStatus.Faulted,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 824, 924, 1);

        KernelResult<PlatformDomainBinding> bind = kernel.BindPlatformDomain(handle);

        Assert.False(bind.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bind.Error);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted, kernel.StartProcess(handle).Error);

        provider.RevokeStatus = null;
        KernelResult terminate = kernel.TerminateProcess(handle);

        Assert.False(terminate.IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.True(kernel.Processes.Resolve(handle).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(handle).Value!.LocalReclaimCompleted);
    }

    [Fact]
    public void DomainContractRejectsUnmaterializedLocalSubject()
    {
        PlatformDomainIdentity missingDomainId = new PlatformDomainIdentity(
            default,
            new ProcessHandle(new ProcessId(817), 1));
        PlatformDomainIdentity missingProcessId = new PlatformDomainIdentity(
            new DomainId(917),
            new ProcessHandle(default, 1));
        PlatformDomainIdentity missingGeneration = new PlatformDomainIdentity(
            new DomainId(917),
            new ProcessHandle(new ProcessId(817), 0));

        Assert.Equal(
            PlatformAuthorityStatus.Denied,
            PlatformDomainContract.ValidateSubject(missingDomainId).Status);
        Assert.Equal(
            PlatformAuthorityStatus.Denied,
            PlatformDomainContract.ValidateSubject(missingProcessId).Status);
        Assert.Equal(
            PlatformAuthorityStatus.Denied,
            PlatformDomainContract.ValidateSubject(missingGeneration).Status);
    }

    [Fact]
    public void ExecutionResultValidationRequiresExactLeaseTransitionAndState()
    {
        PlatformProviderDomainLease lease = new PlatformProviderDomainLease(
            new PlatformProviderDomainLeaseId(41),
            new PlatformProviderLeaseGeneration(7),
            new PlatformDomainIdentity(
                new DomainId(51),
                new ProcessHandle(new ProcessId(61), 9)));

        PlatformDomainExecutionTransitionResult correct = new PlatformDomainExecutionTransitionResult(
            lease,
            PlatformDomainExecutionTransition.Start,
            PlatformDomainExecutionState.Running);
        Assert.True(PlatformDomainExecutionContract.ValidateResult(
            lease,
            PlatformDomainExecutionTransition.Start,
            correct).IsSuccess);

        PlatformDomainExecutionTransitionResult stale = correct with
        {
            DomainLease = lease with
            {
                Generation = new PlatformProviderLeaseGeneration(lease.Generation.Value + 1),
            },
        };
        Assert.Equal(
            PlatformAuthorityStatus.Stale,
            PlatformDomainExecutionContract.ValidateResult(
                lease,
                PlatformDomainExecutionTransition.Start,
                stale).Status);

        PlatformDomainExecutionTransitionResult wrongSubject = correct with
        {
            DomainLease = lease with
            {
                Subject = new PlatformDomainIdentity(
                    new DomainId(52),
                    lease.Subject.Process),
            },
        };
        Assert.Equal(
            PlatformAuthorityStatus.WrongDomain,
            PlatformDomainExecutionContract.ValidateResult(
                lease,
                PlatformDomainExecutionTransition.Start,
                wrongSubject).Status);

        PlatformDomainExecutionTransitionResult wrongProcess = correct with
        {
            DomainLease = lease with
            {
                Subject = lease.Subject with
                {
                    Process = new ProcessHandle(new ProcessId(62), 9),
                },
            },
        };
        Assert.Equal(
            PlatformAuthorityStatus.WrongDomain,
            PlatformDomainExecutionContract.ValidateResult(
                lease,
                PlatformDomainExecutionTransition.Start,
                wrongProcess).Status);

        Assert.Equal(
            PlatformAuthorityStatus.Faulted,
            PlatformDomainExecutionContract.ValidateResult(
                lease,
                PlatformDomainExecutionTransition.Start,
                correct with { Transition = PlatformDomainExecutionTransition.Park }).Status);

        Assert.Equal(
            PlatformAuthorityStatus.Faulted,
            PlatformDomainExecutionContract.ValidateResult(
                lease,
                PlatformDomainExecutionTransition.Start,
                correct with { State = PlatformDomainExecutionState.Parked }).Status);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task PendingExecutionPreventsClosureAndLateLocalPublication(int step)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 830, 930, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(handle).Value!;
        if (step > 0) Assert.True(kernel.StartProcess(handle).IsSuccess);
        if (step > 1) Assert.True(kernel.ParkProcess(handle).IsSuccess);
        int before = provider.Transitions.Count;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        provider.TransitionCallback = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        Task<KernelResult> work = Task.Run(() => step == 0 ? kernel.StartProcess(handle) : step == 1 ? kernel.ParkProcess(handle) : kernel.ResumeProcess(handle));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            await Task.Run(() =>
            {
                Assert.Equal(KernelError.PlatformBindingActive,
                    kernel.PlatformAuthority.TransitionDomainExecution(binding, binding.Subject, PlatformDomainExecutionTransition.Start).Error);
                Assert.Equal(KernelError.PlatformBindingActive, kernel.TerminateProcess(handle).Error);
                Assert.Equal(ProcessState.Exiting, process.State);
                ProcessTeardownSnapshot teardown = kernel.ObserveProcessTeardown(handle).Value!;
                Assert.False(teardown.LocalReclaimCompleted);
                Assert.Equal(0, provider.RevokeCalls);
                Assert.True(ReadPending(kernel.PlatformAuthority, binding.BindingId));
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.PlatformFaulted, (await work).Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.False(ReadPending(kernel.PlatformAuthority, binding.BindingId));
        provider.TransitionCallback = null;
        Assert.True(kernel.TerminateProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(before + 1, provider.Transitions.Count);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ExecutionUnknownOutcomeRetainsRootUntilActualClosureOrPinsMissingConsumer(int fault)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 831, 931, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(handle).Value!;
        object original = DomainRecords(kernel.PlatformAuthority)[binding.BindingId]!;
        provider.TransitionCallback = () =>
        {
            Assert.True(ReadPending(kernel.PlatformAuthority, binding.BindingId));
            if (fault == 0) throw new InvalidOperationException("controlled lost execution receipt");
            if (fault == 1) provider.Behavior = LifecycleBehavior.Deny;
            if (fault == 2) provider.Behavior = LifecycleBehavior.WrongLeaseResult;
            if (fault == 3)
            {
                Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
                Assert.True(ReadPending(kernel.PlatformAuthority, binding.BindingId));
                PlatformDomainBinding current = ReadBinding(kernel.PlatformAuthority, binding.BindingId);
                Assert.Equal(KernelError.PlatformBindingActive, kernel.PlatformAuthority.RevokeDomain(current, current.Subject).Error);
            }
            if (fault == 4) DomainRecords(kernel.PlatformAuthority).Remove(binding.BindingId);
        };
        KernelResult start = kernel.StartProcess(handle);
        Assert.Equal(fault == 1 ? KernelError.PlatformDenied : KernelError.PlatformFaulted, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(false, original.GetType().GetProperty("ExecutionTransitionInFlight")!.GetValue(original));
        if (fault == 3) Assert.True(ReadPending(kernel.PlatformAuthority, binding.BindingId));
        provider.TransitionCallback = null;
        Assert.False(kernel.StartProcess(handle).IsSuccess);
        Assert.Single(provider.Transitions);
        KernelResult close = kernel.TerminateProcess(handle);
        if (fault < 3)
        {
            Assert.True(close.IsSuccess, close.Message);
            Assert.Equal(1, provider.RevokeCalls);
            Assert.Equal(ProcessState.Exited, process.State);
        }
        else
        {
            Assert.Equal(fault == 3 ? KernelError.StaleGeneration : KernelError.PlatformBindingNotFound, close.Error);
            Assert.Equal(ProcessState.Exiting, process.State);
            Assert.False(kernel.ObserveProcessTeardown(handle).Value!.LocalReclaimCompleted);
            Assert.Equal(0, provider.RevokeCalls);
            Assert.False(kernel.TerminateProcess(handle).IsSuccess);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task KernelExecutionTransactionRejectsOverlapAndCommitAfterTeardown(int step)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 840, 940, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(handle).Value!;
        if (step > 0) Assert.True(kernel.StartProcess(handle).IsSuccess);
        if (step > 1) Assert.True(kernel.ParkProcess(handle).IsSuccess);
        int before = provider.Transitions.Count;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        provider.TransitionCallback = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        KernelResult Transition() => step == 0 ? kernel.StartProcess(handle) :
            step == 1 ? kernel.ParkProcess(handle) : kernel.ResumeProcess(handle);
        Task<KernelResult> work = Task.Run(Transition);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            object gate = PrivateGate(kernel, "_platformMemoryUseGate");
            lock (gate)
            {
                // Exercise actual advancing teardown while callback remains pending.
                Assert.Equal(KernelError.PlatformBindingActive, Transition().Error);
                Assert.Equal(KernelError.PlatformBindingActive, kernel.TerminateProcess(handle).Error);
                Assert.Equal(ProcessState.Exiting, process.State);
                Assert.False(kernel.ObserveProcessTeardown(handle).Value!.LocalReclaimCompleted);
                Assert.True(ReadPending(kernel.PlatformAuthority, binding.BindingId));
                Assert.Equal(0, provider.RevokeCalls);
                release.Set();
            }
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.False(ReadPending(kernel.PlatformAuthority, binding.BindingId));
        provider.TransitionCallback = null;
        Assert.True(kernel.TerminateProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(before + 1, provider.Transitions.Count);
        (SingProcess _, ProcessHandle replacement) = CreateAdmittedProcess(kernel, 840, 940, 2);
        Assert.Equal(KernelError.StaleHandle, Transition().Error);
        Assert.True(kernel.StartProcess(replacement).IsSuccess);
    }

    [Fact]
    public async Task TeardownBeforeBridgeAdmissionPreventsEffectAndOldGenerationPublication()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = CreateAdmittedProcess(kernel, 841, 941, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        object bridgeGate = PrivateGate(kernel.PlatformAuthority, "_secureDomainLifecycleGate");
        Task<KernelResult> work;
        lock (bridgeGate)
        {
            work = Task.Run(() => kernel.StartProcess(handle));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (PrivateGate(kernel, "_platformMemoryUseGate"))
                {
                    HashSet<ProcessHandle> pending = (HashSet<ProcessHandle>)typeof(RuntimeKernel).GetField("_executionTransactions",
                        global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel)!;
                    return pending.Contains(handle);
                }
            }, TimeSpan.FromSeconds(15)));
            Assert.True(kernel.TerminateProcess(handle).IsSuccess);
            Assert.Equal(ProcessState.Exited, process.State);
            CreateAdmittedProcess(kernel, 841, 941, 2);
        }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.Empty(provider.Transitions);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(ProcessState.Exited, process.State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void FinalExecutionConsumerRequiresFreshRootReceiptAndBalancesAdmission(int fault)
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle handle) = CreateAdmittedProcess(kernel, 842, 942, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(handle).Value!;
        object original = DomainRecords(kernel.PlatformAuthority)[binding.BindingId]!;
        bool committed = false;
        KernelResult result = kernel.PlatformAuthority.TransitionDomainExecution(binding, binding.Subject,
            PlatformDomainExecutionTransition.Start,
            completionConsumer: commit =>
            {
                Assert.True(ReadPending(kernel.PlatformAuthority, binding.BindingId));
                if (fault == 0)
                    Assert.Equal(KernelError.PlatformBindingActive, kernel.TerminateProcess(handle).Error);
                if (fault == 1) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
                if (fault == 2) DomainRecords(kernel.PlatformAuthority).Remove(binding.BindingId);
                return commit(() => { committed = true; return KernelResult.Ok(); });
            });
        Assert.False(result.IsSuccess);
        Assert.False(committed);
        Assert.Single(provider.Transitions);
        Assert.Equal(false, original.GetType().GetProperty("ExecutionTransitionInFlight")!.GetValue(original));
        if (fault == 1) Assert.True(ReadPending(kernel.PlatformAuthority, binding.BindingId));
        if (fault == 0) Assert.True(kernel.TerminateProcess(handle).IsSuccess);
        else
        {
            Assert.False(kernel.TerminateProcess(handle).IsSuccess);
            Assert.False(kernel.ObserveProcessTeardown(handle).Value!.LocalReclaimCompleted);
            Assert.Equal(0, provider.RevokeCalls);
        }
    }

    [Fact]
    public void AdmissionConsumerRefusalDoesNotCallProviderOrSetPending()
    {
        LifecycleProvider provider = new LifecycleProvider(LifecycleBehavior.Success);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle handle) = CreateAdmittedProcess(kernel, 843, 943, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(handle).Value!;
        KernelResult denied = kernel.PlatformAuthority.TransitionDomainExecution(binding, binding.Subject,
            PlatformDomainExecutionTransition.Start,
            admissionConsumer: _ => KernelResult.Fail(KernelError.InvalidTransition, "fresh local owner refuses"));
        Assert.Equal(KernelError.InvalidTransition, denied.Error);
        Assert.Empty(provider.Transitions);
        Assert.False(ReadPending(kernel.PlatformAuthority, binding.BindingId));
        Assert.True(kernel.StartProcess(handle).IsSuccess);
        Assert.True(kernel.TerminateProcess(handle).IsSuccess);
    }

    private static object PrivateGate(object owner, string field) =>
        owner.GetType().GetField(field, global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;

    private static global::System.Collections.IDictionary DomainRecords(PlatformAuthorityBridge bridge) =>
        (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
    private static bool ReadPending(PlatformAuthorityBridge bridge, PlatformDomainBindingId id)
    {
        object record = DomainRecords(bridge)[id]!;
        return (bool)record.GetType().GetProperty("ExecutionTransitionInFlight")!.GetValue(record)!;
    }
    private static PlatformDomainBinding ReadBinding(PlatformAuthorityBridge bridge, PlatformDomainBindingId id)
    {
        object record = DomainRecords(bridge)[id]!;
        return (PlatformDomainBinding)record.GetType().GetProperty("Binding")!.GetValue(record)!;
    }
    private static (SingPlus.Sip.SingProcess Process, ProcessHandle Handle) CreateAdmittedProcess(
        RuntimeKernel kernel,
        ulong processId,
        ulong domainId,
        ulong generation)
    {
        SingProcessManifestV1 manifest = new SingProcessManifestV1(
            new ProcessId(processId),
            new DomainId(domainId),
            generation,
            $"execution-lifecycle-{processId}-{generation}",
            ExecutionRole.Sip,
            MemoryProfile.SipRegion);
        KernelResult<SingProcess> created = kernel.CreateProcess(manifest);
        Assert.True(created.IsSuccess, created.Message);
        ProcessHandle handle = new ProcessHandle(manifest.ProcessId, manifest.Generation);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        return (created.Value!, handle);
    }

    private enum LifecycleBehavior
    {
        Success,
        Deny,
        Revoked,
        Faulted,
        WrongLeaseResult,
        StaleLeaseResult,
    }

    private sealed class LifecycleProvider(LifecycleBehavior behavior) :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDomainExecutionProvider
    {
        private readonly Dictionary<
            PlatformProviderDomainLeaseId,
            PlatformProviderDomainLease> _leases = [];
        private ulong _nextLeaseId = 1;

        public Action? TransitionCallback { get; set; }
        public LifecycleBehavior Behavior { get; set; } = behavior;
        public PlatformAuthorityStatus? RevokeStatus { get; set; }
        public PlatformFeatureAvailability FeatureAvailability { get; init; } =
            PlatformFeatureAvailability.Executable;
        public bool ReturnZeroLeaseId { get; init; }
        public bool ReturnZeroLeaseGeneration { get; init; }
        public int BindCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public List<PlatformDomainExecutionTransition> Transitions { get; } = [];

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("execution-test"),
            PlatformDomainContract.ContractVersion,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformFeatureManifest QueryFeatures() =>
            DomainFeatureManifest(FeatureAvailability);

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            BindCalls++;
            PlatformProviderDomainLease lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(
                    ReturnZeroLeaseId ? 0 : _nextLeaseId++),
                new PlatformProviderLeaseGeneration(
                    ReturnZeroLeaseGeneration ? 0UL : 1UL),
                subject);
            _leases.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult<PlatformDomainExecutionTransitionResult> TransitionDomainExecution(
            PlatformProviderDomainLease domainLease,
            PlatformDomainExecutionTransition transition)
        {
            Transitions.Add(transition);
            TransitionCallback?.Invoke();
            if (!_leases.TryGetValue(domainLease.LeaseId, out PlatformProviderDomainLease lease) ||
                domainLease != lease)
            {
                return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Unexpected provider lease.");
            }

            if (Behavior == LifecycleBehavior.Deny)
            {
                return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Execution transition denied by test provider.");
            }

            if (Behavior == LifecycleBehavior.Revoked)
            {
                return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(
                    PlatformAuthorityStatus.Revoked,
                    "Execution domain was revoked by test provider.");
            }

            if (Behavior == LifecycleBehavior.Faulted)
            {
                return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(
                    PlatformAuthorityStatus.Faulted,
                    "Execution transition faulted after an unknown external outcome.");
            }

            PlatformProviderDomainLease resultLease = Behavior switch
            {
                LifecycleBehavior.WrongLeaseResult =>
                    lease with
                    {
                        LeaseId = new PlatformProviderDomainLeaseId(lease.LeaseId.Value + 1),
                    },
                LifecycleBehavior.StaleLeaseResult =>
                    lease with
                    {
                        Generation = new PlatformProviderLeaseGeneration(
                            lease.Generation.Value + 1),
                    },
                _ => lease,
            };
            return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Ok(
                new PlatformDomainExecutionTransitionResult(
                    resultLease,
                    transition,
                    PlatformDomainExecutionContract.ExpectedState(transition)));
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            RevokeCalls++;
            if (RevokeStatus is { } status)
            {
                if (status == PlatformAuthorityStatus.Revoked)
                    _leases.Remove(lease.LeaseId);
                return PlatformAuthorityResult.Fail(
                    status,
                    "Domain revoke failed by test provider configuration.");
            }

            if (!_leases.TryGetValue(lease.LeaseId, out PlatformProviderDomainLease active) || active != lease)
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Unexpected provider lease.");
            }

            _leases.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not part of execution lifecycle tests.");

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not part of execution lifecycle tests.");
    }

    private sealed class BindingOnlyProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider
    {
        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("binding-only-test"),
            PlatformDomainContract.ContractVersion,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformFeatureManifest QueryFeatures() => DomainFeatureManifest();

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject) =>
            PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(
                new PlatformProviderDomainLease(
                    new PlatformProviderDomainLeaseId(1),
                    new PlatformProviderLeaseGeneration(1),
                    subject));

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) =>
            PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not part of execution lifecycle tests.");

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not part of execution lifecycle tests.");
    }

    private static PlatformFeatureManifest DomainFeatureManifest(
        PlatformFeatureAvailability availability = PlatformFeatureAvailability.Executable) =>
        new(new[]
    {
        new PlatformFeatureDescriptor(
            PlatformFeatureFamily.NeutralDomains,
            PlatformDomainContract.ContractVersion,
            availability),
    });
}



