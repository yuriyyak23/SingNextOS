using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformExecutionLifecycleTests
{
    [Fact]
    public void BoundProcessPublishesLocalStateOnlyAfterProviderTransitionSucceeds()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.Success);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 801, 901, 1);
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
        var provider = new LifecycleProvider(LifecycleBehavior.Deny);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 802, 902, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        var start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(new[] { PlatformDomainExecutionTransition.Start }, provider.Transitions);
    }

    [Fact]
    public void ProviderDenialLeavesRunningAndParkedStatesUnchanged()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.Success);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 823, 923, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        Assert.True(kernel.StartProcess(handle).IsSuccess);

        provider.Behavior = LifecycleBehavior.Deny;
        var deniedPark = kernel.ParkProcess(handle);

        Assert.False(deniedPark.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, deniedPark.Error);
        Assert.Equal(ProcessState.Running, process.State);

        provider.Behavior = LifecycleBehavior.Success;
        Assert.True(kernel.ParkProcess(handle).IsSuccess);
        provider.Behavior = LifecycleBehavior.Deny;

        var deniedResume = kernel.ResumeProcess(handle);

        Assert.False(deniedResume.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, deniedResume.Error);
        Assert.Equal(ProcessState.Parked, process.State);
    }

    [Fact]
    public void ProviderRevocationQuarantinesBindingUntilExplicitCloseSucceeds()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.Success);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 803, 903, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        Assert.True(kernel.StartProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);

        provider.Behavior = LifecycleBehavior.Revoked;
        var park = kernel.ParkProcess(handle);

        Assert.False(park.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingRevoked, park.Error);
        Assert.Equal(ProcessState.Running, process.State);

        var transitionsAfterRevocation = provider.Transitions.ToArray();
        var secondPark = kernel.ParkProcess(handle);
        Assert.False(secondPark.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, secondPark.Error);
        Assert.Equal(ProcessState.Running, process.State);
        Assert.Equal(transitionsAfterRevocation, provider.Transitions);

        var terminate = kernel.TerminateProcess(handle);
        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Fact]
    public void ProviderFaultQuarantinesBindingAndBlocksTransitionRetry()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.Faulted);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 820, 920, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        var start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Single(provider.Transitions);

        var retry = kernel.StartProcess(handle);

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
        var provider = new LifecycleProvider(
            staleGeneration
                ? LifecycleBehavior.StaleLeaseResult
                : LifecycleBehavior.WrongLeaseResult);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 804, 904, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        var start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);

        var transitionCount = provider.Transitions.Count;
        var retry = kernel.StartProcess(handle);
        Assert.False(retry.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Equal(transitionCount, provider.Transitions.Count);

        var terminate = kernel.TerminateProcess(handle);
        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void BoundProviderWithoutExecutionContractCannotFallBackToLocalOnlyState()
    {
        var kernel = new RuntimeKernel(new BindingOnlyProvider());
        var (process, handle) = CreateAdmittedProcess(kernel, 805, 905, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        var start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
    }

    [Fact]
    public void RuntimeAdmissionEvidenceCannotPublishExecutableProcessState()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            FeatureAvailability = PlatformFeatureAvailability.RuntimeAdmission,
        };
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 822, 922, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        var start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Empty(provider.Transitions);
    }

    [Fact]
    public void StaleProcessGenerationIsRejectedBeforeProviderTransition()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.Success);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 806, 906, 4);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        var stale = handle with { Generation = handle.Generation + 1 };

        var start = kernel.StartProcess(stale);

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
        var provider = new LifecycleProvider(LifecycleBehavior.Success);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 807, 907, 1);
        Assert.True(kernel.StartProcess(handle).IsSuccess);
        if (park)
            Assert.True(kernel.ParkProcess(handle).IsSuccess);
        var stateBeforeBind = process.State;

        var bind = kernel.BindPlatformDomain(handle);

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
        var provider = new LifecycleProvider(LifecycleBehavior.Success);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 808, 908, 1);
        var binding = kernel.BindPlatformDomain(handle).Value!;
        Assert.True(kernel.StartProcess(handle).IsSuccess);
        if (park)
            Assert.True(kernel.ParkProcess(handle).IsSuccess);
        var stateBeforeRevoke = process.State;

        var revoke = kernel.RevokePlatformDomain(handle, binding);

        Assert.False(revoke.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, revoke.Error);
        Assert.Equal(stateBeforeRevoke, process.State);
        Assert.Equal(0, provider.RevokeCalls);

        var nextTransition = park
            ? kernel.ResumeProcess(handle)
            : kernel.ParkProcess(handle);
        Assert.True(nextTransition.IsSuccess, nextTransition.Message);

        var terminate = kernel.TerminateProcess(handle);

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
        var provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            RevokeStatus = providerStatus,
        };
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 811, 911, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);
        Assert.True(kernel.StartProcess(handle).IsSuccess);

        var terminate = kernel.TerminateProcess(handle);

        Assert.False(terminate.IsSuccess);
        Assert.Equal(expectedError, terminate.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.True(kernel.Processes.Resolve(handle).IsSuccess);

        var teardown = kernel.QueryProcessTeardown(handle);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, teardown.Value!.Phase);
        Assert.False(teardown.Value.PlatformDomainClosed);
        Assert.False(teardown.Value.LocalReclaimCompleted);
    }

    [Fact]
    public void FailedStartMayDetachWhileStateRemainsAdmitted()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.Deny);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 809, 909, 1);
        var binding = kernel.BindPlatformDomain(handle).Value!;
        Assert.Equal(KernelError.PlatformDenied, kernel.StartProcess(handle).Error);
        Assert.Equal(ProcessState.Admitted, process.State);

        var revoke = kernel.RevokePlatformDomain(handle, binding);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.True(kernel.StartProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);
    }

    [Fact]
    public void FaultedPreStartCloseQuarantinesBindingAndBlocksExecution()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            RevokeStatus = PlatformAuthorityStatus.Faulted,
        };
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 821, 921, 1);
        var binding = kernel.BindPlatformDomain(handle).Value!;

        var close = kernel.RevokePlatformDomain(handle, binding);

        Assert.False(close.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, close.Error);
        Assert.Equal(ProcessState.Admitted, process.State);

        var start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, start.Error);
        Assert.Empty(provider.Transitions);

        provider.RevokeStatus = null;
        Assert.True(kernel.TerminateProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(2, provider.RevokeCalls);
    }

    [Fact]
    public void StaleOrForgedPreExecutionIdentityCannotDetachBinding()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.Success);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 810, 910, 4);
        var binding = kernel.BindPlatformDomain(handle).Value!;

        var staleSubject = handle with { Generation = handle.Generation + 1 };
        var stale = kernel.RevokePlatformDomain(staleSubject, binding);
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleHandle, stale.Error);

        var staleBinding = binding with
        {
            Generation = new PlatformDomainBindingGeneration(binding.Generation.Value + 1),
        };
        var staleBindingResult = kernel.RevokePlatformDomain(handle, staleBinding);
        Assert.False(staleBindingResult.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, staleBindingResult.Error);

        var unknownBinding = binding with
        {
            BindingId = new PlatformDomainBindingId(binding.BindingId.Value + 1000),
        };
        var unknownBindingResult = kernel.RevokePlatformDomain(handle, unknownBinding);
        Assert.False(unknownBindingResult.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingNotFound, unknownBindingResult.Error);

        var forged = binding with
        {
            Subject = new PlatformDomainIdentity(
                new DomainId(binding.Subject.DomainId.Value + 1),
                binding.Subject.Process),
        };
        var forgedResult = kernel.RevokePlatformDomain(handle, forged);
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
        var provider = new LifecycleProvider(LifecycleBehavior.Success);
        var kernel = new RuntimeKernel(provider);
        var (ownerProcess, owner) = CreateAdmittedProcess(kernel, 812, 912, 1);
        var (siblingProcess, sibling) = CreateAdmittedProcess(kernel, 813, 912, 1);
        var ownerBinding = kernel.BindPlatformDomain(owner).Value!;
        Assert.True(kernel.StartProcess(owner).IsSuccess);

        var forgedDetach = kernel.RevokePlatformDomain(sibling, ownerBinding);

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
        var provider = new LifecycleProvider(LifecycleBehavior.Success);
        var kernel = new RuntimeKernel(provider);
        var (_, first) = CreateAdmittedProcess(kernel, 814, 914, 1);
        var (_, second) = CreateAdmittedProcess(kernel, 815, 914, 1);

        var firstBinding = kernel.BindPlatformDomain(first);
        var secondBinding = kernel.BindPlatformDomain(second);

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
        var provider = new LifecycleProvider(LifecycleBehavior.Success);
        var kernel = new RuntimeKernel(provider);
        var (_, handle) = CreateAdmittedProcess(kernel, 818, 918, 1);
        var oldBinding = kernel.BindPlatformDomain(handle).Value!;
        Assert.True(kernel.RevokePlatformDomain(handle, oldBinding).IsSuccess);
        var replacement = kernel.BindPlatformDomain(handle).Value!;

        var repeatOldClose = kernel.RevokePlatformDomain(handle, oldBinding);
        var duplicate = kernel.BindPlatformDomain(handle);

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
        var provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            ReturnZeroLeaseId = zeroLeaseId,
            ReturnZeroLeaseGeneration = zeroLeaseGeneration,
        };
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 816, 916, 1);

        var bind = kernel.BindPlatformDomain(handle);

        Assert.False(bind.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bind.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1, provider.BindCalls);
        Assert.Equal(1, provider.RevokeCalls);

        Assert.True(kernel.StartProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);
        Assert.Empty(provider.Transitions);
    }

    [Fact]
    public void FailedMalformedLeaseCleanupRemainsTrackedAndPinsLocalReclaim()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            ReturnZeroLeaseId = true,
            RevokeStatus = PlatformAuthorityStatus.Faulted,
        };
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 819, 919, 1);

        var bind = kernel.BindPlatformDomain(handle);
        var start = kernel.StartProcess(handle);
        var terminate = kernel.TerminateProcess(handle);

        Assert.False(bind.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bind.Error);
        Assert.Contains("quarantined", bind.Message!, StringComparison.Ordinal);
        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, start.Error);
        Assert.Empty(provider.Transitions);
        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.Equal(2, provider.RevokeCalls);

        var teardown = kernel.QueryProcessTeardown(handle);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, teardown.Value!.Phase);
        Assert.False(teardown.Value.PlatformDomainClosed);
        Assert.False(teardown.Value.LocalReclaimCompleted);
        Assert.True(kernel.Processes.Resolve(handle).IsSuccess);
    }

    [Fact]
    public void RecoveredMalformedLeaseCleanupClosesBeforeLocalReclaim()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.Success)
        {
            ReturnZeroLeaseId = true,
            RevokeStatus = PlatformAuthorityStatus.Faulted,
        };
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 824, 924, 1);

        var bind = kernel.BindPlatformDomain(handle);

        Assert.False(bind.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bind.Error);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted, kernel.StartProcess(handle).Error);

        provider.RevokeStatus = null;
        var terminate = kernel.TerminateProcess(handle);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(2, provider.RevokeCalls);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Fact]
    public void DomainContractRejectsUnmaterializedLocalSubject()
    {
        var missingDomainId = new PlatformDomainIdentity(
            default,
            new ProcessHandle(new ProcessId(817), 1));
        var missingProcessId = new PlatformDomainIdentity(
            new DomainId(917),
            new ProcessHandle(default, 1));
        var missingGeneration = new PlatformDomainIdentity(
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
        var lease = new PlatformProviderDomainLease(
            new PlatformProviderDomainLeaseId(41),
            new PlatformProviderLeaseGeneration(7),
            new PlatformDomainIdentity(
                new DomainId(51),
                new ProcessHandle(new ProcessId(61), 9)));

        var correct = new PlatformDomainExecutionTransitionResult(
            lease,
            PlatformDomainExecutionTransition.Start,
            PlatformDomainExecutionState.Running);
        Assert.True(PlatformDomainExecutionContract.ValidateResult(
            lease,
            PlatformDomainExecutionTransition.Start,
            correct).IsSuccess);

        var stale = correct with
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

        var wrongSubject = correct with
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

        var wrongProcess = correct with
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

    private static (SingPlus.Sip.SingProcess Process, ProcessHandle Handle) CreateAdmittedProcess(
        RuntimeKernel kernel,
        ulong processId,
        ulong domainId,
        ulong generation)
    {
        var manifest = new SingProcessManifestV1(
            new ProcessId(processId),
            new DomainId(domainId),
            generation,
            $"execution-lifecycle-{processId}-{generation}",
            ExecutionRole.Sip,
            MemoryProfile.SipRegion);
        var created = kernel.CreateProcess(manifest);
        Assert.True(created.IsSuccess, created.Message);
        var handle = new ProcessHandle(manifest.ProcessId, manifest.Generation);
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
            var lease = new PlatformProviderDomainLease(
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
            if (!_leases.TryGetValue(domainLease.LeaseId, out var lease) ||
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

            var resultLease = Behavior switch
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

            if (!_leases.TryGetValue(lease.LeaseId, out var active) || active != lease)
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
