using System.Security.Cryptography;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip.Sdk;

namespace SingPlus.Tests.Runtime;

[SipContract]
internal interface IVNextP05ResourceService
{
    [Message(1)]
    [RequiresResource(1, ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, 10, "host:compute-v1")]
    int Run();
}

public sealed class VNextPhase05SipResourceSentryTests
{
    [Fact]
    public void GeneratedSentryFailsClosedWithoutLiveResolver()
    {
        var target = new Target();
        var context = new TrustedSipInvocationContext(default, default, default, default);

        var result = IVNextP05ResourceServiceGeneratedOperationSentries.InvokeRuntime_Run(target, in context);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public void MissingLiveResourceGrantCannotBeReplacedByGeneratedMetadata()
    {
        var setup = Create();
        var target = new Target();
        var invalid = setup.Binding with { ResourceGrant = new CapabilityId(ulong.MaxValue) };
        var context = Context(setup, invalid);

        var result = IVNextP05ResourceServiceGeneratedOperationSentries.InvokeRuntime_Run(target, in context);

        Assert.False(result.IsSuccess);
        Assert.Equal((int)KernelError.CapabilityNotFound, result.ErrorCode);
        Assert.Equal(0, target.Calls);
        Assert.Equal(0UL, Used(setup));
    }

    [Fact]
    public void MissingEffectCapabilityFailsBeforeGeneratedTargetOrProvider()
    {
        var setup = Create();
        var target = new Target { Submit = true };
        var invalid = setup.Binding with { EffectCapability = new CapabilityId(ulong.MaxValue) };
        var context = Context(setup, invalid);

        var result = IVNextP05ResourceServiceGeneratedOperationSentries.InvokeRuntime_Run(target, in context);

        Assert.False(result.IsSuccess);
        Assert.Equal((int)KernelError.CapabilityNotFound, result.ErrorCode);
        Assert.Equal(0, target.Calls);
        Assert.Equal(0, target.ProviderCalls);
        Assert.Equal(0UL, Used(setup));
    }

    [Fact]
    public void ServiceExceptionAfterAdmissionDisposesPreSubmitLease()
    {
        var setup = Create();
        var target = new Target { Throw = true };
        var context = Context(setup, setup.Binding);

        Assert.Throws<InvalidOperationException>(() =>
            IVNextP05ResourceServiceGeneratedOperationSentries.InvokeRuntime_Run(target, in context));

        Assert.Equal(1, target.Calls);
        Assert.Equal(0UL, Used(setup));
        Assert.NotEqual(ExternalOperationState.Submitted,
            setup.Kernel.ExternalOperations.Query(setup.Operation).Value!.State);
    }

    [Fact]
    public void GeneratedTargetCanCrossSubmitBoundaryExactlyOnce()
    {
        var setup = Create();
        var target = new Target { Submit = true };
        var context = Context(setup, setup.Binding);

        var result = IVNextP05ResourceServiceGeneratedOperationSentries.InvokeRuntime_Run(target, in context);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, target.ProviderCalls);
        Assert.Equal(10UL, Used(setup));
        Assert.Equal(ExternalOperationState.Submitted,
            setup.Kernel.ExternalOperations.Query(setup.Operation).Value!.State);
    }

    [Fact]
    public void ManifestResourceIntentIsCanonicalRequestedMetadataOnly()
    {
        var image = new byte[] { 1, 2, 3 };
        var requirement = IVNextP05ResourceServiceCapabilities.Run_Resource;
        var manifest = new ServiceManifestV1(new("p05"), new("1"),
            Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(950, 1950), resourceUseRequirements: [requirement]);
        var kernel = new RuntimeKernel();

        var admission = kernel.EvaluateComponentAdmission(new ComponentAdmissionPlan(manifest, image));

        Assert.Equal(ManifestAdmissionDisposition.Granted, admission.Disposition);
        var decision = Assert.Single(admission.Decisions,
            item => item.Kind == ManifestRequirementKind.ResourceUse);
        Assert.Equal(ManifestRequirementDisposition.Requested, decision.Disposition);
        var canonical = global::System.Text.Encoding.UTF8.GetString(manifest.SerializeCanonical());
        Assert.Contains("ResourceUseRequirements", canonical, StringComparison.Ordinal);
        Assert.Contains("host:compute-v1", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(CapabilityId), typeof(SipResourceRequirementV1).GetProperties().Select(p => p.PropertyType));
        Assert.DoesNotContain(typeof(BudgetReservationHandle), typeof(SipResourceRequirementV1).GetProperties().Select(p => p.PropertyType));
    }

    [Fact]
    public void ResourceContractRejectsUnknownVersionEnumAndSentinels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SipResourceRequirementV1(2,
            ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, 1, "host:compute-v1",
            ResourceAssuranceV1.RuntimeEnforced, SipResourceDonationPolicyV1.None));
        Assert.Throws<ArgumentException>(() => new SipResourceRequirementV1(1,
            (ResourceClassV1)999, ResourceUnitV1.Nanoseconds, 1, "host:compute-v1",
            ResourceAssuranceV1.RuntimeEnforced, SipResourceDonationPolicyV1.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SipResourceRequirementV1(1,
            ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, ulong.MaxValue, "host:compute-v1",
            ResourceAssuranceV1.RuntimeEnforced, SipResourceDonationPolicyV1.None));
        Assert.Throws<ArgumentException>(() => new SipResourceRequirementV1(1,
            ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, 1, " host:compute-v1 ",
            ResourceAssuranceV1.RuntimeEnforced, SipResourceDonationPolicyV1.None));
    }

    [Fact]
    public void ManifestCannotClaimFutureGuaranteedReservation()
    {
        var future = new SipResourceRequirementV1(1, ResourceClassV1.ComputeTime,
            ResourceUnitV1.Nanoseconds, 1, "host:compute-v1",
            ResourceAssuranceV1.GuaranteedReservation, SipResourceDonationPolicyV1.None);

        Assert.Throws<ArgumentException>(() => new ServiceManifestV1(new("future"), new("1"),
            new string('a', 64), TestFixtures.Manifest(951, 1951), resourceUseRequirements: [future]));
    }

    [Fact]
    public void ReflectionAndDefaultValuesCannotForgeLiveAdmission()
    {
        Assert.Empty(typeof(TrustedSipInvocationContext).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(GeneratedSipResourceAdmission).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(typeof(GeneratedSipResourceAdmission).GetFields(BindingFlags.Public | BindingFlags.Instance),
            field => typeof(IDisposable).IsAssignableFrom(field.FieldType) || typeof(Delegate).IsAssignableFrom(field.FieldType));

        var result = IVNextP05ResourceServiceGeneratedOperationSentries.InvokeRuntime_Run(new Target(), default);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ProviderFailureThroughGeneratedSentryQuarantinesWithoutRefund()
    {
        var setup = Create();
        var target = new Target { Submit = true, ProviderFailure = true };
        var context = Context(setup, setup.Binding);

        var result = IVNextP05ResourceServiceGeneratedOperationSentries.InvokeRuntime_Run(target, in context);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, target.ProviderCalls);
        Assert.Equal(10UL, Used(setup));
        var operation = setup.Kernel.ExternalOperations.Query(setup.Operation).Value!;
        Assert.Equal(ExternalOperationState.Submitted, operation.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, operation.Disposition);
    }

    private static TrustedSipInvocationContext Context(Setup setup, SipResourceAdmissionBinding binding) =>
        new(setup.Process, setup.Process, default, default,
            requirement => setup.Kernel.EnterSipResourceAdmission(setup.Process, binding, requirement));

    private static Setup Create()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 940, 1940).Handle;
        var process = TestFixtures.Create(kernel, 941, 1941).Handle;
        var administration = kernel.MintCapability(new(1940), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, process, "p05",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100), new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!;
        var effect = kernel.MintCapability(new(1941), process, ResourceKind.Compute,
            "compute:effect", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
        var grant = kernel.CapabilityAuthority.Mint(new(1941), new(1941), ResourceKind.Compute,
            "resource-use:compute-time", CapabilityRights.Delegate, process.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 4,
            resourceUse: new(1, new(1, ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
                ResourceUnitV1.Nanoseconds, 100, 0, "host:compute-v1"), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 3)).Value!.CapabilityId;
        var region = kernel.AllocateBuffer<byte>(process, 16).Value!;
        var operation = kernel.PrepareExternalOperation(process,
            [new(region.Handle, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1);
        var binding = new SipResourceAdmissionBinding(effect, ResourceKind.Compute, "compute:effect", 1,
            grant, 1, "host:compute-v1", operation, dependencies);
        return new(kernel, process, budget.ProcessBudget, operation, binding);
    }

    private static ulong Used(Setup setup) => Assert.Single(
        setup.Kernel.QueryBudget(setup.Budget).Value!.Usage,
        usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;

    private sealed record Setup(RuntimeKernel Kernel, ProcessHandle Process, BudgetAccountHandle Budget,
        ExternalOperationHandle Operation, SipResourceAdmissionBinding Binding);

    private sealed class Target : IIVNextP05ResourceServiceGeneratedSentryTarget_Run
    {
        internal int Calls { get; private set; }
        internal bool Throw { get; init; }
        internal bool Submit { get; init; }
        internal int ProviderCalls { get; private set; }
        internal bool ProviderFailure { get; init; }

        public GeneratedSipSentryResult<int> Sentry_Run(in TrustedSipInvocationContext context,
            GeneratedSipResourceAdmission resourceAdmission)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("service fault");
            if (Submit)
            {
                var submitted = resourceAdmission.Submit(() =>
                {
                    ProviderCalls++;
                    return ProviderFailure
                        ? GeneratedSipSubmitResult.Failure((int)KernelError.PlatformFaulted, "provider failed")
                        : GeneratedSipSubmitResult.Ok();
                });
                if (!submitted.IsSuccess)
                    return GeneratedSipSentryResult<int>.Failure(submitted.ErrorCode, submitted.Message!);
            }
            return GeneratedSipSentryResult<int>.Success(7);
        }
    }
}
