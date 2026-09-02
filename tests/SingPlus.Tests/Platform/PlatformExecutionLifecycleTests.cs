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
    public void ProviderRevocationLeavesRunningProcessRunningAndRevokesBridgeBinding()
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
        Assert.Equal(KernelError.PlatformBindingRevoked, secondPark.Error);
        Assert.Equal(ProcessState.Running, process.State);
        Assert.Equal(transitionsAfterRevocation, provider.Transitions);
    }

    [Fact]
    public void MalformedProviderSuccessIsFaultedAndDoesNotPublishLocalState()
    {
        var provider = new LifecycleProvider(LifecycleBehavior.WrongLeaseResult);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = CreateAdmittedProcess(kernel, 804, 904, 1);
        Assert.True(kernel.BindPlatformDomain(handle).IsSuccess);

        var start = kernel.StartProcess(handle);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
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

    [Fact]
    public void ExecutionResultValidationRequiresExactLeaseTransitionAndState()
    {
        var lease = new PlatformProviderDomainLease(
            new PlatformProviderDomainLeaseId(41),
            new PlatformProviderLeaseGeneration(7),
            new PlatformDomainIdentity(new DomainId(51), 9));

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
                Subject = new PlatformDomainIdentity(new DomainId(52), 9),
            },
        };
        Assert.Equal(
            PlatformAuthorityStatus.WrongDomain,
            PlatformDomainExecutionContract.ValidateResult(
                lease,
                PlatformDomainExecutionTransition.Start,
                wrongSubject).Status);

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
        WrongLeaseResult,
    }

    private sealed class LifecycleProvider(LifecycleBehavior behavior) :
        IPlatformAuthorityProvider,
        IPlatformDomainExecutionProvider
    {
        private PlatformProviderDomainLease? _lease;

        public LifecycleBehavior Behavior { get; set; } = behavior;
        public List<PlatformDomainExecutionTransition> Transitions { get; } = [];

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("execution-test"),
            1,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(1),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _lease = lease;
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult<PlatformDomainExecutionTransitionResult> TransitionDomainExecution(
            PlatformProviderDomainLease domainLease,
            PlatformDomainExecutionTransition transition)
        {
            Transitions.Add(transition);
            if (_lease is not { } lease || domainLease != lease)
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

            var resultLease = Behavior == LifecycleBehavior.WrongLeaseResult
                ? lease with { LeaseId = new PlatformProviderDomainLeaseId(lease.LeaseId.Value + 1) }
                : lease;
            return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Ok(
                new PlatformDomainExecutionTransitionResult(
                    resultLease,
                    transition,
                    PlatformDomainExecutionContract.ExpectedState(transition)));
        }

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

    private sealed class BindingOnlyProvider : IPlatformAuthorityProvider
    {
        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("binding-only-test"),
            1,
            PlatformAuthorityFeatures.NeutralDomainBinding);

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
}
