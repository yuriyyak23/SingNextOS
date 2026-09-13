using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformExecutionPolicyTests
{
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
        var provider = new PolicyProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = CreateAdmittedProcess(kernel, 1801, 1810, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;

        var first = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);
        var repeat = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);
        var replacement = kernel.ConfigurePlatformExecutionPolicy(
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

        var request = Assert.Single(provider.PolicyRequests);
        Assert.Equal(InteractivePolicy, request.Policy);
        Assert.Equal(binding.Subject, request.DomainLease.Subject);
        Assert.NotEqual(typeof(PlatformDomainBindingId), typeof(PlatformProviderDomainLeaseId));
        Assert.NotEqual(typeof(PlatformDomainBindingGeneration), typeof(PlatformProviderLeaseGeneration));
    }

    [Fact]
    public void HostPolicyRegistrationIsModelOnlyAndCannotClaimExecutableLifecycle()
    {
        var kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        var (process, subject) = CreateAdmittedProcess(kernel, 1813, 1930, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;

        var registration = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            BackgroundPolicy);

        Assert.True(registration.IsSuccess, registration.Message);
        Assert.Equal(
            PlatformFeatureAvailability.ModelOnly,
            registration.Value!.Feature.Availability);
        Assert.Equal(BackgroundPolicy, registration.Value.Policy);

        var start = kernel.StartProcess(subject);

        Assert.False(start.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, start.Error);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
    }

    [Fact]
    public void InvalidPolicyIsRejectedBeforeProviderCall()
    {
        var provider = new PolicyProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = CreateAdmittedProcess(kernel, 1802, 1820, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var invalidPolicies = new[]
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

        foreach (var policy in invalidPolicies)
        {
            var result = kernel.ConfigurePlatformExecutionPolicy(subject, binding, policy);

            Assert.False(result.IsSuccess);
            Assert.Equal(KernelError.PlatformDenied, result.Error);
            Assert.Equal(default(PlatformExecutionPolicyRegistration), result.Value);
        }

        Assert.Empty(provider.PolicyRequests);
    }

    [Fact]
    public void ContractAcceptsAggregateBudgetAboveOnePeriodAndExactResult()
    {
        var lease = new PlatformProviderDomainLease(
            new PlatformProviderDomainLeaseId(41),
            new PlatformProviderLeaseGeneration(7),
            new PlatformDomainIdentity(
                new DomainId(51),
                new ProcessHandle(new ProcessId(61), 9)));
        var policy = new PlatformExecutionPolicy(
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
        var lease = new PlatformProviderDomainLease(
            new PlatformProviderDomainLeaseId(42),
            new PlatformProviderLeaseGeneration(8),
            new PlatformDomainIdentity(
                new DomainId(52),
                new ProcessHandle(new ProcessId(62), 10)));
        var correct = new PlatformExecutionPolicyResult(lease, InteractivePolicy);

        var wrongLease = correct with
        {
            DomainLease = lease with
            {
                LeaseId = new PlatformProviderDomainLeaseId(lease.LeaseId.Value + 1),
            },
        };
        var stale = correct with
        {
            DomainLease = lease with
            {
                Generation = new PlatformProviderLeaseGeneration(lease.Generation.Value + 1),
            },
        };
        var wrongSubject = correct with
        {
            DomainLease = lease with
            {
                Subject = new PlatformDomainIdentity(
                    new DomainId(lease.Subject.DomainId.Value + 1),
                    lease.Subject.Process),
            },
        };
        var wrongPolicy = correct with { Policy = BackgroundPolicy };

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
        var provider = new PolicyProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = CreateAdmittedProcess(kernel, 1803, 1830, 4);
        var (_, sibling) = CreateAdmittedProcess(kernel, 1804, 1830, 4);
        var binding = kernel.BindPlatformDomain(owner).Value!;

        var staleProcess = kernel.ConfigurePlatformExecutionPolicy(
            owner with { Generation = owner.Generation + 1 },
            binding,
            InteractivePolicy);
        Assert.Equal(KernelError.StaleHandle, staleProcess.Error);

        var staleBinding = kernel.ConfigurePlatformExecutionPolicy(
            owner,
            binding with
            {
                Generation = new PlatformDomainBindingGeneration(binding.Generation.Value + 1),
            },
            InteractivePolicy);
        Assert.Equal(KernelError.StaleGeneration, staleBinding.Error);

        var missingBinding = kernel.ConfigurePlatformExecutionPolicy(
            owner,
            binding with
            {
                BindingId = new PlatformDomainBindingId(binding.BindingId.Value + 1000),
            },
            InteractivePolicy);
        Assert.Equal(KernelError.PlatformBindingNotFound, missingBinding.Error);

        var forgedSubject = kernel.ConfigurePlatformExecutionPolicy(
            owner,
            binding with
            {
                Subject = new PlatformDomainIdentity(
                    new DomainId(binding.Subject.DomainId.Value + 1),
                    binding.Subject.Process),
            },
            InteractivePolicy);
        Assert.Equal(KernelError.WrongPlatformDomain, forgedSubject.Error);

        var siblingUse = kernel.ConfigurePlatformExecutionPolicy(
            sibling,
            binding,
            InteractivePolicy);
        Assert.Equal(KernelError.WrongPlatformDomain, siblingUse.Error);

        Assert.Empty(provider.PolicyRequests);
    }

    [Fact]
    public void MissingFeatureInterfaceOrNonOperationalClassIsRejectedBeforeProviderCall()
    {
        foreach (var availability in new[]
                 {
                     PlatformFeatureAvailability.ProjectionOnly,
                     PlatformFeatureAvailability.ProductionSecure,
                 })
        {
            var nonOperational = new PolicyProvider
            {
                PolicyAvailability = availability,
            };
            var nonOperationalKernel = new RuntimeKernel(nonOperational);
            var (_, first) = CreateAdmittedProcess(nonOperationalKernel, 1805, 1850, 1);
            var firstBinding = nonOperationalKernel.BindPlatformDomain(first).Value!;

            var wrongClass = nonOperationalKernel.ConfigurePlatformExecutionPolicy(
                first,
                firstBinding,
                InteractivePolicy);

            Assert.Equal(KernelError.PlatformUnsupported, wrongClass.Error);
            Assert.Empty(nonOperational.PolicyRequests);
        }

        var omitted = new PolicyProvider { AdvertisePolicy = false };
        var omittedKernel = new RuntimeKernel(omitted);
        var (_, omittedSubject) = CreateAdmittedProcess(omittedKernel, 1814, 1940, 1);
        var omittedBinding = omittedKernel.BindPlatformDomain(omittedSubject).Value!;

        var missingFeature = omittedKernel.ConfigurePlatformExecutionPolicy(
            omittedSubject,
            omittedBinding,
            InteractivePolicy);

        Assert.Equal(KernelError.PlatformUnsupported, missingFeature.Error);
        Assert.Empty(omitted.PolicyRequests);

        var bindingOnly = new BindingOnlyProvider();
        var bindingOnlyKernel = new RuntimeKernel(bindingOnly);
        var (_, second) = CreateAdmittedProcess(bindingOnlyKernel, 1806, 1860, 1);
        var secondBinding = bindingOnlyKernel.BindPlatformDomain(second).Value!;

        var missingInterface = bindingOnlyKernel.ConfigurePlatformExecutionPolicy(
            second,
            secondBinding,
            InteractivePolicy);

        Assert.Equal(KernelError.PlatformUnsupported, missingInterface.Error);
    }

    [Fact]
    public void ProviderDenialDoesNotPublishOrQuarantinePolicy()
    {
        var provider = new PolicyProvider
        {
            ConfigureStatus = PlatformAuthorityStatus.Denied,
        };
        var kernel = new RuntimeKernel(provider);
        var (process, subject) = CreateAdmittedProcess(kernel, 1807, 1870, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;

        var denied = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);

        Assert.False(denied.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        Assert.Equal(default(PlatformExecutionPolicyRegistration), denied.Value);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Single(provider.PolicyRequests);

        provider.ConfigureStatus = null;
        var retry = kernel.ConfigurePlatformExecutionPolicy(
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
        var provider = new PolicyProvider { ConfigureStatus = status };
        var kernel = new RuntimeKernel(provider);
        var (process, subject) = CreateAdmittedProcess(kernel, 1808, 1880, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;

        var first = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);
        var retry = kernel.ConfigurePlatformExecutionPolicy(
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

        var terminate = kernel.TerminateProcess(subject);

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
        var provider = new PolicyProvider { ResultMutation = mutation };
        var kernel = new RuntimeKernel(provider);
        var (process, subject) = CreateAdmittedProcess(kernel, 1809, 1890, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;

        var first = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            binding,
            InteractivePolicy);
        var retry = kernel.ConfigurePlatformExecutionPolicy(
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
        var provider = new PolicyProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = CreateAdmittedProcess(kernel, 1810, 1900, 1);
        var oldBinding = kernel.BindPlatformDomain(subject).Value!;
        Assert.True(kernel.ConfigurePlatformExecutionPolicy(
            subject,
            oldBinding,
            InteractivePolicy).IsSuccess);

        Assert.True(kernel.RevokePlatformDomain(subject, oldBinding).IsSuccess);
        var oldUse = kernel.ConfigurePlatformExecutionPolicy(
            subject,
            oldBinding,
            InteractivePolicy);
        Assert.Equal(KernelError.PlatformBindingRevoked, oldUse.Error);

        var replacement = kernel.BindPlatformDomain(subject).Value!;
        var replacementPolicy = kernel.ConfigurePlatformExecutionPolicy(
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
        var provider = new PolicyProvider
        {
            ResultMutation = PolicyResultMutation.WrongPolicy,
            RevokeStatus = PlatformAuthorityStatus.Faulted,
        };
        var kernel = new RuntimeKernel(provider);
        var (process, subject) = CreateAdmittedProcess(kernel, 1811, 1910, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        Assert.Equal(
            KernelError.PlatformFaulted,
            kernel.ConfigurePlatformExecutionPolicy(
                subject,
                binding,
                InteractivePolicy).Error);

        var terminate = kernel.TerminateProcess(subject);

        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);

        var teardown = kernel.QueryProcessTeardown(subject);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, teardown.Value!.Phase);
        Assert.False(teardown.Value.PlatformDomainClosed);
        Assert.False(teardown.Value.LocalReclaimCompleted);
    }

    [Fact]
    public void RunningParkedAndExitingProcessesCannotConfigurePolicy()
    {
        var provider = new PolicyProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = CreateAdmittedProcess(kernel, 1812, 1920, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;

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
        var manifest = new SingProcessManifestV1(
            new ProcessId(processId),
            new DomainId(domainId),
            generation,
            $"execution-policy-{processId}-{generation}",
            ExecutionRole.Sip,
            MemoryProfile.SipRegion);
        var created = kernel.CreateProcess(manifest);
        Assert.True(created.IsSuccess, created.Message);
        var handle = new ProcessHandle(manifest.ProcessId, manifest.Generation);
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
        IPlatformExecutionPolicyProvider
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

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("execution-policy-test"),
            PlatformDomainContract.ContractVersion,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformFeatureManifest QueryFeatures()
        {
            var features = new List<PlatformFeatureDescriptor>
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
            var lease = new PlatformProviderDomainLease(
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
            if (!_leases.TryGetValue(domainLease.LeaseId, out var active) ||
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

            var resultLease = ResultMutation switch
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
            var resultPolicy = ResultMutation == PolicyResultMutation.WrongPolicy
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
            if (!_leases.TryGetValue(domainLease.LeaseId, out var active) ||
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

            if (!_leases.TryGetValue(lease.LeaseId, out var active) || active != lease)
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
