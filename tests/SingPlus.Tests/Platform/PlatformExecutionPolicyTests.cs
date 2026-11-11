using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformExecutionPolicyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PolicyCompletionCannotMutateForeignRootSourceFactsOrInterlock(bool epoch)
    {
        PolicyProvider provider = new PolicyProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1824, 1944, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary roots = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = roots[binding.BindingId]!;
        object foreign = null!;
        provider.PolicyCallback = () =>
        {
            foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            Type type = foreign.GetType();
            if (epoch) type.GetProperty("BackendEpoch")!.SetValue(foreign, new PlatformBackendEpoch(2));
            else type.GetProperty("ProviderIncarnation")!.SetValue(foreign, new PlatformProviderIncarnation(2));
            roots[binding.BindingId] = foreign;
        };
        Assert.False(kernel.ConfigurePlatformExecutionPolicy(subject, binding, InteractivePolicy).IsSuccess);
        Assert.Equal(true, foreign.GetType().GetProperty("ExecutionTransitionInFlight")!.GetValue(foreign));
        Assert.Null(foreign.GetType().GetProperty("ExecutionPolicy")!.GetValue(foreign));
        Assert.Equal("Active", foreign.GetType().GetProperty("AuthorityState")!.GetValue(foreign)!.ToString());
        Assert.Equal(false, original.GetType().GetProperty("ExecutionTransitionInFlight")!.GetValue(original));
        Assert.NotNull(original.GetType().GetProperty("ExecutionPolicy")!.GetValue(original));
        Assert.False(kernel.StartProcess(subject).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Single(provider.PolicyRequests);
        Assert.Equal(0, provider.RevokeCalls);
        roots[binding.BindingId] = original; // model repair of exact owner, not durable recovery
        provider.PolicyCallback = null;
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Single(provider.PolicyRequests);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void PolicyCallbackFaultCannotPublishOrRepeatAcceptance(int fault)
    {
        PolicyProvider provider = new PolicyProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1820, 1940, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        provider.PolicyCallback = () =>
        {
            if (fault == 0) provider.Incarnation++;
            if (fault == 1) kernel.PlatformAuthority.ObserveBackendReset();
            if (fault == 2) Assert.False(kernel.TerminateProcess(subject).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Lost policy outcome.");
            if (fault == 4) provider.ThrowOnSource = true;
            if (fault == 5) Assert.Equal(KernelError.PlatformBindingActive, kernel.RevokePlatformDomain(subject, binding).Error);
        };
        KernelResult<PlatformExecutionPolicyRegistration> result = kernel.ConfigurePlatformExecutionPolicy(subject, binding, InteractivePolicy);
        Assert.False(result.IsSuccess);
        Assert.Equal(default(PlatformExecutionPolicyRegistration), result.Value);
        provider.ThrowOnSource = false;
        Assert.False(kernel.ConfigurePlatformExecutionPolicy(subject, binding, InteractivePolicy).IsSuccess);
        Assert.Single(provider.PolicyRequests);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        if (fault is 2 or 3 or 4 or 5)
        {
            Assert.True(kernel.TerminateProcess(subject).IsSuccess);
            Assert.True(kernel.Processes.ResolveTerminalState(subject).IsSuccess);
            Assert.Equal(1, provider.RevokeCalls);
        }
    }

    [Fact]
    public void PolicyPendingBlocksReentrantConfigureAndStartWithoutRepeatingEffect()
    {
        PolicyProvider provider = new PolicyProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1821, 1941, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        provider.PolicyCallback = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive,
                kernel.ConfigurePlatformExecutionPolicy(subject, binding, BackgroundPolicy).Error);
            Assert.False(kernel.StartProcess(subject).IsSuccess);
            Assert.Equal(0, provider.RevokeCalls);
        };
        KernelResult<PlatformExecutionPolicyRegistration> configured = kernel.ConfigurePlatformExecutionPolicy(subject, binding, InteractivePolicy);
        Assert.True(configured.IsSuccess, configured.Message);
        Assert.True(kernel.ConfigurePlatformExecutionPolicy(subject, binding, InteractivePolicy).IsSuccess);
        Assert.Single(provider.PolicyRequests);
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.ResolveTerminalState(subject).IsSuccess);
    }

    [Fact]
    public void PolicyInterlockPrecedesFirstSourceCallbackAndBalancesRepeat()
    {
        PolicyProvider provider = new PolicyProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1823, 1943, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        int sourceReads = 0;
        provider.SourceCallback = () =>
        {
            sourceReads++;
            Assert.Equal(KernelError.PlatformBindingActive,
                kernel.ConfigurePlatformExecutionPolicy(subject, binding, InteractivePolicy).Error);
        };
        Assert.True(kernel.ConfigurePlatformExecutionPolicy(subject, binding, InteractivePolicy).IsSuccess);
        Assert.True(kernel.ConfigurePlatformExecutionPolicy(subject, binding, InteractivePolicy).IsSuccess);
        Assert.Equal(4, sourceReads);
        Assert.Single(provider.PolicyRequests);
        provider.SourceCallback = null;
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
    }

    [Fact]
    public void SourceCallbackBeforePolicyEffectRechecksActualProcessOwner()
    {
        PolicyProvider provider = new PolicyProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1822, 1942, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        int reads = 0;
        provider.SourceCallback = () =>
        {
            if (++reads == 2) Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        };
        Assert.False(kernel.ConfigurePlatformExecutionPolicy(subject, binding, InteractivePolicy).IsSuccess);
        Assert.Empty(provider.PolicyRequests);
        provider.SourceCallback = null;
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.ResolveTerminalState(subject).IsSuccess);
    }

    private static readonly PlatformExecutionPolicy InteractivePolicy = new(
        new ExecutionBudget(
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(20)),
        PriorityClass.Interactive,
        LatencyHint.PreferLowLatency,
        ThroughputHint.Balanced);

    private static readonly PlatformExecutionPolicy BackgroundPolicy = new(
        new ExecutionBudget(
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(100)),
        PriorityClass.Background,
        LatencyHint.Balanced,
        ThroughputHint.PreferThroughput);

    [Fact]
    public void ExactProviderAcceptancePublishesLocalPolicyRegistrationOnce()
    {
        PolicyProvider provider = new PolicyProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1801, 1810, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;

        KernelResult<PlatformExecutionPolicyRegistration> first = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);
        KernelResult<PlatformExecutionPolicyRegistration> repeat = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);
        KernelResult<PlatformExecutionPolicyRegistration> replacement = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            BackgroundPolicy);

        Assert.True(first.IsSuccess, first.Message);
        Assert.True(repeat.IsSuccess, repeat.Message);
        Assert.Equal(first.Value, repeat.Value);
        Assert.Equal(binding, first.Value!.DomainBinding);
        Assert.Equal(InteractivePolicy, first.Value.Policy);
        Assert.Equal(
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.ExecutionPolicy,
                PlatformExecutionPolicyContract.ContractVersion,
                PlatformFeatureAvailability.ModelOnly),
            first.Value.Feature);
        Assert.False(replacement.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, replacement.Error);
        Assert.Equal(default(PlatformExecutionPolicyRegistration), replacement.Value);

        (PlatformProviderDomainLease DomainLease, PlatformExecutionPolicy Policy) request = Assert.Single(provider.PolicyRequests);
        Assert.Equal(InteractivePolicy, request.Policy);
        Assert.Equal(binding.Subject, request.DomainLease.Subject);
        Assert.NotEqual(typeof(PlatformDomainBindingId), typeof(PlatformProviderDomainLeaseId));
        Assert.NotEqual(typeof(PlatformDomainBindingGeneration), typeof(PlatformProviderLeaseGeneration));
    }

    [Fact]
    public void HostPolicyRegistrationIsModelOnlyAndCannotClaimExecutableLifecycle()
    {
        RuntimeKernel kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        (SingProcess? process, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1813, 1930, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;

        KernelResult<PlatformExecutionPolicyRegistration> registration = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            BackgroundPolicy);

        Assert.True(registration.IsSuccess, registration.Message);
        Assert.Equal(
            PlatformFeatureAvailability.ModelOnly,
            registration.Value!.Feature.Availability);
        Assert.Equal(BackgroundPolicy, registration.Value.Policy);

        KernelResult start = kernel.StartProcess(subject);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
    }

    [Fact]
    public void InvalidPolicyIsRejectedBeforeProviderCall()
    {
        PolicyProvider provider = new PolicyProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1802, 1820, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        PlatformExecutionPolicy[] invalidPolicies = new[]
        {
            InteractivePolicy with
            {
                Budget = new ExecutionBudget(
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(20)),
            },
            InteractivePolicy with
            {
                Budget = new ExecutionBudget(
                    TimeSpan.FromTicks(-1),
                    TimeSpan.FromMilliseconds(20)),
            },
            InteractivePolicy with
            {
                Budget = new ExecutionBudget(
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.Zero),
            },
            InteractivePolicy with
            {
                Budget = new ExecutionBudget(
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromTicks(-1)),
            },
            InteractivePolicy with { Priority = (PriorityClass)byte.MaxValue },
            InteractivePolicy with { Latency = (LatencyHint)byte.MaxValue },
            InteractivePolicy with { Throughput = (ThroughputHint)byte.MaxValue },
        };

        foreach (PlatformExecutionPolicy policy in invalidPolicies)
        {
            KernelResult<PlatformExecutionPolicyRegistration> result = kernel.ConfigurePlatformExecutionPolicy(subject, binding, policy);

            Assert.False(result.IsSuccess);
            Assert.Equal(KernelError.PlatformDenied, result.Error);
            Assert.Equal(default(PlatformExecutionPolicyRegistration), result.Value);
        }

        Assert.Empty(provider.PolicyRequests);
    }

    [Fact]
    public void ContractAcceptsAggregateBudgetAboveOnePeriodAndExactResult()
    {
        PlatformProviderDomainLease lease = new PlatformProviderDomainLease(
            new PlatformProviderDomainLeaseId(41),
            new PlatformProviderLeaseGeneration(7),
            new PlatformDomainIdentity(
                new DomainId(51),
                new ProcessHandle(new ProcessId(61), 9)));
        PlatformExecutionPolicy policy = new PlatformExecutionPolicy(
            new ExecutionBudget(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(10)),
            PriorityClass.Normal,
            LatencyHint.PreferLowLatency,
            ThroughputHint.PreferThroughput);

        Assert.True(PlatformExecutionPolicyContract.ValidatePolicy(policy).IsSuccess);
        Assert.True(PlatformExecutionPolicyContract.ValidateResult(
            lease,
            policy,
            new PlatformExecutionPolicyResult(lease, policy)).IsSuccess);
    }

    [Fact]
    public void ResultValidationRequiresExactLeaseGenerationSubjectAndPolicy()
    {
        PlatformProviderDomainLease lease = new PlatformProviderDomainLease(
            new PlatformProviderDomainLeaseId(42),
            new PlatformProviderLeaseGeneration(8),
            new PlatformDomainIdentity(
                new DomainId(52),
                new ProcessHandle(new ProcessId(62), 10)));
        PlatformExecutionPolicyResult correct = new PlatformExecutionPolicyResult(lease, InteractivePolicy);

        PlatformExecutionPolicyResult wrongLease = correct with
        {
            DomainLease = lease with
            {
                LeaseId = new PlatformProviderDomainLeaseId(lease.LeaseId.Value + 1),
            },
        };
        PlatformExecutionPolicyResult stale = correct with
        {
            DomainLease = lease with
            {
                Generation = new PlatformProviderLeaseGeneration(lease.Generation.Value + 1),
            },
        };
        PlatformExecutionPolicyResult wrongSubject = correct with
        {
            DomainLease = lease with
            {
                Subject = new PlatformDomainIdentity(
                    new DomainId(lease.Subject.DomainId.Value + 1),
                    lease.Subject.Process),
            },
        };
        PlatformExecutionPolicyResult wrongPolicy = correct with { Policy = BackgroundPolicy };

        Assert.Equal(
            PlatformAuthorityStatus.WrongDomain,
            PlatformExecutionPolicyContract.ValidateResult(
                lease,
                InteractivePolicy,
                wrongLease).Status);
        Assert.Equal(
            PlatformAuthorityStatus.Stale,
            PlatformExecutionPolicyContract.ValidateResult(
                lease,
                InteractivePolicy,
                stale).Status);
        Assert.Equal(
            PlatformAuthorityStatus.WrongDomain,
            PlatformExecutionPolicyContract.ValidateResult(
                lease,
                InteractivePolicy,
                wrongSubject).Status);
        Assert.Equal(
            PlatformAuthorityStatus.Faulted,
            PlatformExecutionPolicyContract.ValidateResult(
                lease,
                InteractivePolicy,
                wrongPolicy).Status);
    }

    [Fact]
    public void StaleForgedOrCrossProcessLocalIdentityIsRejectedBeforeProviderCall()
    {
        PolicyProvider provider = new PolicyProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = CreateAdmittedProcess(kernel, 1803, 1830, 4);
        (SingProcess _, ProcessHandle sibling) = CreateAdmittedProcess(kernel, 1804, 1830, 4);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;

        KernelResult<PlatformExecutionPolicyRegistration> staleProcess = kernel.ConfigurePlatformExecutionPolicy(
            owner with { Generation = owner.Generation + 1 },
            binding,
            InteractivePolicy);
        Assert.Equal(KernelError.StaleHandle, staleProcess.Error);

        KernelResult<PlatformExecutionPolicyRegistration> staleBinding = kernel.ConfigurePlatformExecutionPolicy(
            owner,
            binding with
            {
                Generation = new PlatformDomainBindingGeneration(binding.Generation.Value + 1),
            },
            InteractivePolicy);
        Assert.Equal(KernelError.StaleGeneration, staleBinding.Error);

        KernelResult<PlatformExecutionPolicyRegistration> missingBinding = kernel.ConfigurePlatformExecutionPolicy(
            owner,
            binding with
            {
                BindingId = new PlatformDomainBindingId(binding.BindingId.Value + 1000),
            },
            InteractivePolicy);
        Assert.Equal(KernelError.PlatformBindingNotFound, missingBinding.Error);

        KernelResult<PlatformExecutionPolicyRegistration> forgedSubject = kernel.ConfigurePlatformExecutionPolicy(
            owner,
            binding with
            {
                Subject = new PlatformDomainIdentity(
                    new DomainId(binding.Subject.DomainId.Value + 1),
                    binding.Subject.Process),
            },
            InteractivePolicy);
        Assert.Equal(KernelError.WrongPlatformDomain, forgedSubject.Error);

        KernelResult<PlatformExecutionPolicyRegistration> siblingUse = kernel.ConfigurePlatformExecutionPolicy(
            sibling,
            binding,
            InteractivePolicy);
        Assert.Equal(KernelError.WrongPlatformDomain, siblingUse.Error);

        Assert.Empty(provider.PolicyRequests);
    }

    [Fact]
    public void MissingFeatureInterfaceOrNonOperationalClassIsRejectedBeforeProviderCall()
    {
        foreach (PlatformFeatureAvailability availability in new[]
                 {
                     PlatformFeatureAvailability.ProjectionOnly,
                     PlatformFeatureAvailability.ProductionSecure,
                 })
        {
            PolicyProvider nonOperational = new PolicyProvider
            {
                PolicyAvailability = availability,
            };
            RuntimeKernel nonOperationalKernel = new RuntimeKernel(nonOperational);
            (SingProcess _, ProcessHandle first) = CreateAdmittedProcess(nonOperationalKernel, 1805, 1850, 1);
            PlatformDomainBinding firstBinding = nonOperationalKernel.BindPlatformDomain(first).Value!;

            KernelResult<PlatformExecutionPolicyRegistration> wrongClass = nonOperationalKernel.ConfigurePlatformExecutionPolicy(
                first,
                firstBinding,
                InteractivePolicy);

            Assert.Equal(KernelError.PlatformUnsupported, wrongClass.Error);
            Assert.Empty(nonOperational.PolicyRequests);
        }

        PolicyProvider omitted = new PolicyProvider { AdvertisePolicy = false };
        RuntimeKernel omittedKernel = new RuntimeKernel(omitted);
        (SingProcess _, ProcessHandle omittedSubject) = CreateAdmittedProcess(omittedKernel, 1814, 1940, 1);
        PlatformDomainBinding omittedBinding = omittedKernel.BindPlatformDomain(omittedSubject).Value!;

        KernelResult<PlatformExecutionPolicyRegistration> missingFeature = omittedKernel.ConfigurePlatformExecutionPolicy(
            omittedSubject,
            omittedBinding,
            InteractivePolicy);

        Assert.Equal(KernelError.PlatformUnsupported, missingFeature.Error);
        Assert.Empty(omitted.PolicyRequests);

        BindingOnlyProvider bindingOnly = new BindingOnlyProvider();
        RuntimeKernel bindingOnlyKernel = new RuntimeKernel(bindingOnly);
        (SingProcess _, ProcessHandle second) = CreateAdmittedProcess(bindingOnlyKernel, 1806, 1860, 1);
        PlatformDomainBinding secondBinding = bindingOnlyKernel.BindPlatformDomain(second).Value!;

        KernelResult<PlatformExecutionPolicyRegistration> missingInterface = bindingOnlyKernel.ConfigurePlatformExecutionPolicy(
            second,
            secondBinding,
            InteractivePolicy);

        Assert.Equal(KernelError.PlatformUnsupported, missingInterface.Error);
    }

    [Fact]
    public void ProviderDenialDoesNotPublishOrQuarantinePolicy()
    {
        PolicyProvider provider = new PolicyProvider
        {
            ConfigureStatus = PlatformAuthorityStatus.Denied,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1807, 1870, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;

        KernelResult<PlatformExecutionPolicyRegistration> denied = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);

        Assert.False(denied.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        Assert.Equal(default(PlatformExecutionPolicyRegistration), denied.Value);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Single(provider.PolicyRequests);

        provider.ConfigureStatus = null;
        KernelResult<PlatformExecutionPolicyRegistration> retry = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);

        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal(2, provider.PolicyRequests.Count);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Revoked, KernelError.PlatformBindingRevoked)]
    [InlineData(PlatformAuthorityStatus.Stale, KernelError.StaleGeneration)]
    [InlineData(PlatformAuthorityStatus.WrongDomain, KernelError.WrongPlatformDomain)]
    [InlineData(PlatformAuthorityStatus.Faulted, KernelError.PlatformFaulted)]
    public void AmbiguousProviderFailureQuarantinesPolicyUntilExactDomainClose(
        PlatformAuthorityStatus status,
        KernelError expectedError)
    {
        PolicyProvider provider = new PolicyProvider { ConfigureStatus = status };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1808, 1880, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;

        KernelResult<PlatformExecutionPolicyRegistration> first = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);
        KernelResult<PlatformExecutionPolicyRegistration> retry = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);

        Assert.False(first.IsSuccess);
        Assert.Equal(expectedError, first.Error);
        Assert.Equal(default(PlatformExecutionPolicyRegistration), first.Value);
        Assert.False(retry.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Equal(default(PlatformExecutionPolicyRegistration), retry.Value);
        Assert.Single(provider.PolicyRequests);
        Assert.Equal(ProcessState.Admitted, process.State);

        KernelResult terminate = kernel.TerminateProcess(subject);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(subject).Error);
    }

    [Theory]
    [InlineData(PolicyResultMutation.WrongLease)]
    [InlineData(PolicyResultMutation.StaleGeneration)]
    [InlineData(PolicyResultMutation.WrongSubject)]
    [InlineData(PolicyResultMutation.WrongPolicy)]
    public void MalformedProviderSuccessIsNotPublishedAndQuarantinesBinding(
        PolicyResultMutation mutation)
    {
        PolicyProvider provider = new PolicyProvider { ResultMutation = mutation };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1809, 1890, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;

        KernelResult<PlatformExecutionPolicyRegistration> first = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);
        KernelResult<PlatformExecutionPolicyRegistration> retry = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);

        Assert.False(first.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, first.Error);
        Assert.Equal(default(PlatformExecutionPolicyRegistration), first.Value);
        Assert.False(retry.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Single(provider.PolicyRequests);
        Assert.Equal(ProcessState.Admitted, process.State);

        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(ProcessState.Exited, process.State);
    }

    [Fact]
    public void PolicyLifetimeEndsWithExactLocalBinding()
    {
        PolicyProvider provider = new PolicyProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1810, 1900, 1);
        PlatformDomainBinding oldBinding = kernel.BindPlatformDomain(subject).Value!;
        Assert.True(kernel.ConfigurePlatformExecutionPolicy(
            subject,
            oldBinding,
            InteractivePolicy).IsSuccess);

        Assert.True(kernel.RevokePlatformDomain(subject, oldBinding).IsSuccess);
        KernelResult<PlatformExecutionPolicyRegistration> oldUse = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            oldBinding,
            InteractivePolicy);
        Assert.Equal(KernelError.PlatformBindingRevoked, oldUse.Error);

        PlatformDomainBinding replacement = kernel.BindPlatformDomain(subject).Value!;
        KernelResult<PlatformExecutionPolicyRegistration> replacementPolicy = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            replacement,
            BackgroundPolicy);

        Assert.True(replacementPolicy.IsSuccess, replacementPolicy.Message);
        Assert.NotEqual(oldBinding.BindingId, replacement.BindingId);
        Assert.Equal(2, provider.PolicyRequests.Count);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void DomainCloseFailureAfterPolicyQuarantinePinsLocalReclaim()
    {
        PolicyProvider provider = new PolicyProvider
        {
            ResultMutation = PolicyResultMutation.WrongPolicy,
            RevokeStatus = PlatformAuthorityStatus.Faulted,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1811, 1910, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        Assert.Equal(
            KernelError.PlatformFaulted,
            kernel.ConfigurePlatformExecutionPolicy(
                subject,
                binding,
                InteractivePolicy).Error);

        KernelResult terminate = kernel.TerminateProcess(subject);

        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);

        KernelResult<ProcessTeardownSnapshot> teardown = kernel.QueryProcessTeardown(subject);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, teardown.Value!.Phase);
        Assert.False(teardown.Value.PlatformDomainClosed);
        Assert.False(teardown.Value.LocalReclaimCompleted);
    }

    [Fact]
    public void RunningParkedAndExitingProcessesCannotConfigurePolicy()
    {
        PolicyProvider provider = new PolicyProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = CreateAdmittedProcess(kernel, 1812, 1920, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;

        Assert.True(kernel.StartProcess(subject).IsSuccess);
        Assert.Equal(
            KernelError.InvalidTransition,
            kernel.ConfigurePlatformExecutionPolicy(
                subject,
                binding,
                InteractivePolicy).Error);
        Assert.True(kernel.ParkProcess(subject).IsSuccess);
        Assert.Equal(
            KernelError.InvalidTransition,
            kernel.ConfigurePlatformExecutionPolicy(
                subject,
                binding,
                InteractivePolicy).Error);
        Assert.Empty(provider.PolicyRequests);

        provider.RevokeStatus = PlatformAuthorityStatus.Faulted;
        Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(subject).Error);
        Assert.Equal(
            KernelError.InvalidTransition,
            kernel.ConfigurePlatformExecutionPolicy(
                subject,
                binding,
                InteractivePolicy).Error);
        Assert.Empty(provider.PolicyRequests);
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
            $"execution-policy-{processId}-{generation}",
            ExecutionRole.Sip,
            MemoryProfile.SipRegion);
        KernelResult<SingProcess> created = kernel.CreateProcess(manifest);
        Assert.True(created.IsSuccess, created.Message);
        ProcessHandle handle = new ProcessHandle(manifest.ProcessId, manifest.Generation);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        return (created.Value!, handle);
    }

    public enum PolicyResultMutation
    {
        None = 0,
        WrongLease,
        StaleGeneration,
        WrongSubject,
        WrongPolicy,
    }

    private sealed class PolicyProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDomainExecutionProvider,
        IPlatformExecutionPolicyProvider,
        IPlatformProviderIncarnationSource
    {
        private readonly Dictionary<
            PlatformProviderDomainLeaseId,
            PlatformProviderDomainLease> _leases = [];
        private ulong _nextLeaseId = 1;

        public PlatformAuthorityStatus? ConfigureStatus { get; set; }
        public PlatformAuthorityStatus? RevokeStatus { get; set; }
        public PolicyResultMutation ResultMutation { get; set; }
        public bool AdvertisePolicy { get; set; } = true;
        public PlatformFeatureAvailability PolicyAvailability { get; set; } =
            PlatformFeatureAvailability.ModelOnly;
        public List<(PlatformProviderDomainLease DomainLease, PlatformExecutionPolicy Policy)>
            PolicyRequests { get; } = [];
        public int RevokeCalls { get; private set; }
        public ulong Incarnation { get; set; } = 1;
        public bool ThrowOnSource { get; set; }
        public Action? SourceCallback { get; set; }
        public Action? PolicyCallback { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get
            {
                SourceCallback?.Invoke();
                if (ThrowOnSource) throw new InvalidOperationException("Unavailable policy source.");
                return new(Incarnation);
            }
        }

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("execution-policy-test"),
            PlatformDomainContract.ContractVersion,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformFeatureManifest QueryFeatures()
        {
            List<PlatformFeatureDescriptor> features = new List<PlatformFeatureDescriptor>
            {
                new(
                    PlatformFeatureFamily.NeutralDomains,
                    PlatformDomainContract.ContractVersion,
                    PlatformFeatureAvailability.Executable),
            };
            if (AdvertisePolicy)
            {
                features.Add(new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.ExecutionPolicy,
                    PlatformExecutionPolicyContract.ContractVersion,
                    PolicyAvailability));
            }

            return new PlatformFeatureManifest(features);
        }

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            PlatformProviderDomainLease lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(_nextLeaseId++),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _leases.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult<PlatformExecutionPolicyResult> ConfigureExecutionPolicy(
            PlatformProviderDomainLease domainLease,
            PlatformExecutionPolicy policy)
        {
            PolicyRequests.Add((domainLease, policy));
            PolicyCallback?.Invoke();
            if (!_leases.TryGetValue(domainLease.LeaseId, out PlatformProviderDomainLease active) ||
                active != domainLease)
            {
                return PlatformAuthorityResult<PlatformExecutionPolicyResult>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Unexpected provider domain lease.");
            }

            if (ConfigureStatus is { } status)
            {
                return PlatformAuthorityResult<PlatformExecutionPolicyResult>.Fail(
                    status,
                    "Execution policy failed by test provider configuration.");
            }

            PlatformProviderDomainLease resultLease = ResultMutation switch
            {
                PolicyResultMutation.WrongLease => domainLease with
                {
                    LeaseId = new PlatformProviderDomainLeaseId(domainLease.LeaseId.Value + 1000),
                },
                PolicyResultMutation.StaleGeneration => domainLease with
                {
                    Generation = new PlatformProviderLeaseGeneration(
                        domainLease.Generation.Value + 1),
                },
                PolicyResultMutation.WrongSubject => domainLease with
                {
                    Subject = new PlatformDomainIdentity(
                        new DomainId(domainLease.Subject.DomainId.Value + 1),
                        domainLease.Subject.Process),
                },
                _ => domainLease,
            };
            PlatformExecutionPolicy resultPolicy = ResultMutation == PolicyResultMutation.WrongPolicy
                ? BackgroundPolicy
                : policy;
            return PlatformAuthorityResult<PlatformExecutionPolicyResult>.Ok(
                new PlatformExecutionPolicyResult(resultLease, resultPolicy));
        }

        public PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>
            TransitionDomainExecution(
                PlatformProviderDomainLease domainLease,
                PlatformDomainExecutionTransition transition)
        {
            if (!_leases.TryGetValue(domainLease.LeaseId, out PlatformProviderDomainLease active) ||
                active != domainLease)
            {
                return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Unexpected provider domain lease.");
            }

            return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Ok(
                new PlatformDomainExecutionTransitionResult(
                    domainLease,
                    transition,
                    PlatformDomainExecutionContract.ExpectedState(transition)));
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            RevokeCalls++;
            if (RevokeStatus is { } status)
            {
                return PlatformAuthorityResult.Fail(
                    status,
                    "Domain close failed by test provider configuration.");
            }

            if (!_leases.TryGetValue(lease.LeaseId, out PlatformProviderDomainLease active) || active != lease)
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Unexpected provider domain lease.");
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
                "Not part of execution policy tests.");

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not part of execution policy tests.");
    }

    private sealed class BindingOnlyProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider
    {
        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("binding-only-policy-test"),
            PlatformDomainContract.ContractVersion,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.ExecutionPolicy,
                PlatformExecutionPolicyContract.ContractVersion,
                PlatformFeatureAvailability.ModelOnly),
        });

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
                "Not part of execution policy tests.");

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not part of execution policy tests.");
    }
}
