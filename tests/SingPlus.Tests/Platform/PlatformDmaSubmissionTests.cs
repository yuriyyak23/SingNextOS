using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDmaSubmissionTests
{
    [Fact]
    public void V6AtomicCopySubmitCreatesBothPendingLifetimesAtOneProviderBoundary()
    {
        var scenario = CreateCopyScenario(1520, 1720);
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;

        var result = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
            scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination, destinationPrepare);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(DmaEffectStateV1.EffectPossible, result.Value!.Source.Binding.EffectState);
        Assert.Equal(DmaEffectStateV1.EffectPossible, result.Value.Destination.Binding.EffectState);
        Assert.Equal(1, scenario.Provider.CopySubmitCalls);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).Error);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).Error);

    }

    [Fact]
    public void AtomicCopyNotAcceptedLeavesBothPreparedCyclesRetryable()
    {
        var scenario = CreateCopyScenario(1521, 1730);
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        scenario.Provider.CopySubmitStatus = PlatformAuthorityStatus.NotAccepted;

        var denied = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
            scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination, destinationPrepare);
        scenario.Provider.CopySubmitStatus = null;
        var retried = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
            scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination, destinationPrepare);

        Assert.False(denied.IsSuccess);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(2, scenario.Provider.CopySubmitCalls);
    }

    [Fact]
    public void AtomicCopyCompletionClosesBothPendingLifetimesAfterDestinationAcquire()
    {
        var scenario = CreateCopyScenario(1523, 1750);
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        var execution = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
            scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination,
            destinationPrepare).Value!;

        var completion = scenario.Kernel.CompleteV6PlatformDmaCopy(
            scenario.Subject, scenario.Subject, execution);

        Assert.True(completion.IsSuccess, completion.Message);
        Assert.True(completion.Value!.SourceCompletion.IsSatisfied);
        Assert.True(completion.Value.DestinationCompletion.IsSatisfied);
        Assert.Equal(PlatformDmaPostCompletionVisibilityRequirement.None,
            completion.Value.SourceVisibility.Requirement);
        Assert.Equal(PlatformDmaPostCompletionVisibilityRequirement.AcquisitionFence,
            completion.Value.DestinationVisibility.Requirement);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).IsSuccess);
    }

    [Fact]
    public void IncompleteDestinationLegKeepsBothCopyLifetimesDraining()
    {
        var scenario = CreateCopyScenario(1524, 1760);
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        var execution = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
            scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination,
            destinationPrepare).Value!;
        scenario.Provider.KeepWriteCompletionPending = true;

        var completion = scenario.Kernel.CompleteV6PlatformDmaCopy(
            scenario.Subject, scenario.Subject, execution);

        Assert.Equal(KernelError.PlatformBindingDraining, completion.Error);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).Error);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).Error);

        scenario.Provider.KeepWriteCompletionPending = false;
        var retried = scenario.Kernel.CompleteV6PlatformDmaCopy(
            scenario.Subject, scenario.Subject, execution);

        Assert.True(retried.IsSuccess, retried.Message);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).IsSuccess);
    }

    [Fact]
    public void ProviderDmaDataMotionClosesAuthorityBeforeSourceReleaseAndCanonicalReceipt()
    {
        var scenario = CreateCopyScenario(1525, 1770);
        scenario.Input.Span.Fill(0x6B);
        scenario.Provider.CopyEffect = () => scenario.Input.Span.CopyTo(scenario.Output.Span);
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;

        var moved = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
            scenario.Subject, scenario.Subject, scenario.Input, scenario.Output, scenario.Plan,
            scenario.SourceMapping, scenario.Source, sourcePrepare,
            scenario.DestinationMapping, scenario.Destination, destinationPrepare,
            () => 3, () => 5);

        Assert.True(moved.IsSuccess, moved.Message);
        Assert.False(scenario.Input.IsValid);
        Assert.True(scenario.Output.IsValid);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal((byte)0x6B, value));
        Assert.Equal(DataMotionModeV1.ProviderDmaThenReleaseSource, moved.Value!.Receipt.Plan.Mode);
        Assert.False(moved.Value.Receipt.AuthorizesMovement);
        Assert.Equal(2, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(2, scenario.Provider.MappingRevokeCalls);
    }

    [Fact]
    public void PendingProviderDmaDataMotionDoesNotReleaseEitherRegionOrLowerAuthority()
    {
        var scenario = CreateCopyScenario(1526, 1780);
        scenario.Provider.CopyEffect = () => scenario.Input.Span.CopyTo(scenario.Output.Span);
        scenario.Provider.KeepWriteCompletionPending = true;
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;

        var moved = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
            scenario.Subject, scenario.Subject, scenario.Input, scenario.Output, scenario.Plan,
            scenario.SourceMapping, scenario.Source, sourcePrepare,
            scenario.DestinationMapping, scenario.Destination, destinationPrepare,
            () => 3, () => 5);

        Assert.Equal(KernelError.PlatformBindingDraining, moved.Error);
        Assert.True(scenario.Input.IsValid);
        Assert.True(scenario.Output.IsValid);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).Error);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).Error);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);

        scenario.Provider.KeepWriteCompletionPending = false;
        var resumed = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
            scenario.Subject, scenario.Subject, scenario.Input, scenario.Output, scenario.Plan,
            scenario.SourceMapping, scenario.Source, sourcePrepare,
            scenario.DestinationMapping, scenario.Destination, destinationPrepare,
            () => 3, () => 5);

        Assert.True(resumed.IsSuccess, resumed.Message);
        Assert.False(scenario.Input.IsValid);
        Assert.Equal(1, scenario.Provider.CopySubmitCalls);
        Assert.Equal(2, scenario.Provider.MappingRevokeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderDmaDataMotionResetBeforeClosureRetainsSourceAndNoReceipt(bool duringCopySubmit)
    {
        var scenario = CreateCopyScenario(duringCopySubmit ? 1550UL : 1551UL,
            duringCopySubmit ? 1990UL : 2000UL);
        scenario.Provider.CopyEffect = () => scenario.Input.Span.CopyTo(scenario.Output.Span);
        scenario.Provider.ResetDuringCopySubmit = duringCopySubmit;
        scenario.Provider.ResetDuringRevoke = !duringCopySubmit;
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;

        var moved = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
            scenario.Subject, scenario.Subject, scenario.Input, scenario.Output, scenario.Plan,
            scenario.SourceMapping, scenario.Source, sourcePrepare,
            scenario.DestinationMapping, scenario.Destination, destinationPrepare,
            () => 3, () => 5);

        Assert.False(moved.IsSuccess);
        Assert.True(scenario.Input.IsValid);
        Assert.True(scenario.Output.IsValid);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).Error);
    }

    [Fact]
    public void ProviderDmaPartialGrantClosureCannotResubmitOrReleaseSourceAfterDestinationReset()
    {
        var scenario = CreateCopyScenario(1552, 2010);
        scenario.Provider.CopyEffect = () => scenario.Input.Span.CopyTo(scenario.Output.Span);
        scenario.Provider.RevokeEffect = () =>
        {
            if (scenario.Provider.DmaRevokeCalls == 2)
                scenario.Provider.AdvanceIncarnation();
        };
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;

        var first = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
            scenario.Subject, scenario.Subject, scenario.Input, scenario.Output, scenario.Plan,
            scenario.SourceMapping, scenario.Source, sourcePrepare,
            scenario.DestinationMapping, scenario.Destination, destinationPrepare,
            () => 3, () => 5);
        var repeated = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
            scenario.Subject, scenario.Subject, scenario.Input, scenario.Output, scenario.Plan,
            scenario.SourceMapping, scenario.Source, sourcePrepare,
            scenario.DestinationMapping, scenario.Destination, destinationPrepare,
            () => 3, () => 5);

        Assert.Equal(KernelError.PlatformFaulted, first.Error);
        Assert.False(repeated.IsSuccess);
        Assert.Equal(1, scenario.Provider.CopySubmitCalls);
        Assert.True(scenario.Input.IsValid);
        Assert.True(scenario.Output.IsValid);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingRevoked,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).Error);
        var process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        var identity = new PlatformDomainIdentity(process.DomainId, scenario.Subject);
        var observation = scenario.Kernel.PlatformAuthority.ObserveDmaCopyClosure(
            scenario.Source, identity, scenario.Destination, identity);
        Assert.True(observation.IsSuccess, observation.Message);
        Assert.True(observation.Value!.SourceGrantClosed);
        Assert.False(observation.Value.DestinationGrantClosed);
        Assert.True(observation.Value.DestinationFaultPinned);
        Assert.False(observation.Value.AuthorizesSourceRegionRelease);
        Assert.False(observation.Value.ProvesProviderEffectClosure);
        Assert.False(scenario.Kernel.PlatformAuthority.ObserveDmaCopyClosure(
            scenario.Destination, identity, scenario.Source, identity).IsSuccess);
    }

    [Fact]
    public void CrossOwnerProviderDmaMovesBetweenExactDomainAuthorities()
    {
        var scenario = CreateCrossOwnerCopyScenario(1527, 1790, 1528, 1800);
        scenario.Input.Span.Fill(0x3D);
        scenario.Provider.CopyEffect = () => scenario.Input.Span.CopyTo(scenario.Output.Span);
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.SourceSubject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.DestinationSubject, scenario.Destination).Value!;

        var moved = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
            scenario.SourceSubject, scenario.DestinationSubject,
            scenario.Input, scenario.Output, scenario.Plan,
            scenario.SourceMapping, scenario.Source, sourcePrepare,
            scenario.DestinationMapping, scenario.Destination, destinationPrepare,
            () => 3, () => 5);

        Assert.True(moved.IsSuccess, moved.Message);
        Assert.False(scenario.Input.IsValid);
        Assert.True(scenario.Output.IsValid);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal((byte)0x3D, value));
        Assert.Equal(scenario.SourceSubject.Generation,
            moved.Value!.Receipt.SourceProcessGeneration);
        Assert.Equal(scenario.DestinationSubject.Generation,
            moved.Value.Receipt.DestinationProcessGeneration);
        Assert.Equal(2, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(2, scenario.Provider.MappingRevokeCalls);
    }

    [Fact]
    public void ProviderDmaResumeRejectsUnrelatedActiveSubmissionLegs()
    {
        var scenario = CreateCopyScenario(1529, 1810);
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        Assert.True(scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Source, sourcePrepare).IsSuccess);
        Assert.True(scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Destination, destinationPrepare).IsSuccess);

        var moved = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
            scenario.Subject, scenario.Subject, scenario.Input, scenario.Output, scenario.Plan,
            scenario.SourceMapping, scenario.Source, sourcePrepare,
            scenario.DestinationMapping, scenario.Destination, destinationPrepare,
            () => 3, () => 5);

        Assert.Equal(KernelError.PlatformFaulted, moved.Error);
        Assert.True(scenario.Input.IsValid);
        Assert.True(scenario.Output.IsValid);
        Assert.Equal(0, scenario.Provider.CopySubmitCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AmbiguousMalformedOrResetAtomicCopyPinsBothMappings(
        bool malformed, bool reset)
    {
        var scenario = CreateCopyScenario(1522, 1740);
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        scenario.Provider.MalformedCopySubmit = malformed;
        scenario.Provider.ResetDuringCopySubmit = reset;
        if (!malformed && !reset)
            scenario.Provider.CopySubmitStatus = PlatformAuthorityStatus.Faulted;

        var result = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
            scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination, destinationPrepare);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).Error);
    }

    [Fact]
    public void ExactPreparedCycleSubmitsBoundedPendingOperation()
    {
        var scenario = CreateScenario(
            1501,
            1510,
            PlatformDmaDirection.DeviceWritesMemory);

        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);

        var submit = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!);

        Assert.True(submit.IsSuccess, submit.Message);
        Assert.NotEqual(0UL, submit.Value!.OperationId.Value);
        Assert.NotEqual(0UL, submit.Value.Generation.Value);
        Assert.Equal(scenario.Grant.GrantId, submit.Value.GrantId);
        Assert.Equal(scenario.Grant.Generation, submit.Value.GrantGeneration);
        Assert.Equal(prepare.Value.Cycle, submit.Value.PreparedCycle);
        Assert.Equal(scenario.Grant.Range, submit.Value.Range);
        Assert.Equal(scenario.Grant.Direction, submit.Value.Direction);
        Assert.Equal(1, scenario.Provider.SubmitCalls);

        var feature = scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.DmaMapping);
        Assert.Equal(PlatformDmaPageFaultContract.ContractVersion, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, feature.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, feature.Availability);

        var surface = new[]
        {
            typeof(PlatformDmaOperationId),
            typeof(PlatformDmaOperationGeneration),
            typeof(PlatformDmaSubmission),
        };
        var forbidden = new[]
        {
            "PlatformProvider",
            "Neutral",
            "Physical",
            "BusAddress",
            "Iommu",
            "PageTable",
            "Pte",
            "Descriptor",
            "ScatterGather",
            "Queue",
            "Vector",
            "Controller",
            "Completion",
            "Receipt",
            "Vmcs",
            "Vmx",
            "Lane",
            "Opcode",
        };
        foreach (var type in surface)
        foreach (var member in type.GetMembers(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var signature = member.ToString() ?? member.Name;
            foreach (var term in forbidden)
                Assert.DoesNotContain(term, signature, StringComparison.OrdinalIgnoreCase);
        }

        var methods = typeof(RuntimeKernel)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(static method => method.Name)
            .ToArray();
        Assert.Contains(nameof(RuntimeKernel.SubmitPlatformDma), methods);
        Assert.DoesNotContain(methods, static name =>
            name.Contains("CompleteDma", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SubmitRequiresCurrentPreparedUnacquiredCycleBeforeProvider()
    {
        var scenario = CreateScenario(
            1502,
            1520,
            PlatformDmaDirection.DeviceWritesMemory);

        var noPrepare = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            default);
        Assert.Equal(KernelError.PlatformDenied, noPrepare.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        var first = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(first.IsSuccess, first.Message);
        var second = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(second.IsSuccess, second.Message);
        Assert.NotEqual(first.Value!.Cycle, second.Value!.Cycle);

        var replay = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            first.Value);
        Assert.Equal(KernelError.PlatformDenied, replay.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        var acquire = scenario.Kernel.AcquirePlatformDmaForCpu(
            scenario.Subject,
            scenario.Grant);
        Assert.True(acquire.IsSuccess, acquire.Message);

        var consumed = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            second.Value);
        Assert.Equal(KernelError.PlatformDenied, consumed.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        var fresh = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(fresh.IsSuccess, fresh.Message);
        Assert.True(scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            fresh.Value!).IsSuccess);
        Assert.Equal(1, scenario.Provider.SubmitCalls);
    }

    [Fact]
    public void ProviderResetBeforeSubmitRejectsStaleGrantWithoutCallingProvider()
    {
        var scenario = CreateScenario(1507, 1570, PlatformDmaDirection.DeviceWritesMemory);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.AdvanceIncarnation();

        var result = scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepare);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);
    }

    [Fact]
    public void ProviderResetDuringSubmitFaultPinsAmbiguousAcceptance()
    {
        var scenario = CreateScenario(1508, 1580, PlatformDmaDirection.DeviceWritesMemory);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.ResetDuringSubmit = true;

        var result = scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepare);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, scenario.Provider.SubmitCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrustedBackendResetDuringSubmitPinsPossibleEffectWithoutProviderIncarnationDrift(bool bound)
    {
        var scenario = CreateScenario(1550, 2010, PlatformDmaDirection.DeviceWritesMemory);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitEffect = () =>
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);

        var error = bound
            ? scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepare).Error
            : scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepare).Error;

        Assert.Equal(KernelError.PlatformFaulted, error);
        Assert.Equal(1, scenario.Provider.SubmitCalls);
        Assert.Equal(new PlatformProviderIncarnation(1), scenario.Provider.CurrentIncarnation);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject, scenario.Mapping).IsSuccess);
    }

    [Fact]
    public void TrustedBackendResetDuringAtomicCopyPinsBothPossibleEffects()
    {
        var scenario = CreateCopyScenario(1551, 2020);
        var sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        var destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        scenario.Provider.CopyEffect = () =>
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);

        var result = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
            scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination, destinationPrepare);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, scenario.Provider.CopySubmitCalls);
        Assert.Equal(new PlatformProviderIncarnation(1), scenario.Provider.CurrentIncarnation);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).Error);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
    }

    [Fact]
    public void StaleGrantOrForgedCycleFailsBeforeProviderSubmit()
    {
        var scenario = CreateScenario(
            1503,
            1530,
            PlatformDmaDirection.DeviceReadsMemory);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);

        var staleGrant = scenario.Grant with
        {
            Generation = new PlatformDmaGrantGeneration(scenario.Grant.Generation.Value + 1),
        };
        var stale = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            staleGrant,
            prepare.Value!);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        var forgedEvidence = prepare.Value! with
        {
            Cycle = new PlatformDmaVisibilityCycle(prepare.Value.Cycle.Value + 999),
        };
        var forged = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            forgedEvidence);
        Assert.Equal(KernelError.PlatformDenied, forged.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);
    }

    [Fact]
    public void SubmittedCycleBlocksAcquireReprepareSecondSubmitAndClosure()
    {
        var scenario = CreateScenario(
            1504,
            1540,
            PlatformDmaDirection.DeviceWritesMemory);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        Assert.True(scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!).IsSuccess);

        Assert.Equal(
            KernelError.PlatformBindingActive,
            scenario.Kernel.SubmitPlatformDma(
                scenario.Subject,
                scenario.Grant,
                prepare.Value!).Error);
        Assert.Equal(1, scenario.Provider.SubmitCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.PreparePlatformDmaForDevice(
                scenario.Subject,
                scenario.Grant).Error);
        Assert.Equal(1, scenario.Provider.PrepareCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.AcquirePlatformDmaForCpu(
                scenario.Subject,
                scenario.Grant).Error);
        Assert.Equal(0, scenario.Provider.AcquireCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(
                scenario.Subject,
                scenario.Grant).Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject,
                scenario.Mapping).Error);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDevice(
                scenario.Subject,
                scenario.Device).Error);
        Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);
    }

    [Fact]
    public void CapabilityRevokeStopsNewSubmitButKeepsExistingOperationPinned()
    {
        var scenario = CreateScenario(
            1505,
            1550,
            PlatformDmaDirection.DeviceReadsMemory);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        Assert.True(scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!).IsSuccess);

        var revokeCapability = scenario.Kernel.RevokeCapability(scenario.DeviceCapability);
        Assert.Equal(KernelError.PlatformBindingDraining, revokeCapability.Error);

        var submitAgain = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!);
        Assert.Equal(KernelError.CapabilityRevoked, submitAgain.Error);
        Assert.Equal(1, scenario.Provider.SubmitCalls);

        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(
                scenario.Subject,
                scenario.Grant).Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);

        var lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject,
            scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Active, lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
    }

    [Fact]
    public void ProcessTeardownDrainsWithoutReclaimWhileSubmissionIsPending()
    {
        var scenario = CreateScenario(
            1506,
            1560,
            PlatformDmaDirection.DeviceWritesMemory);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        Assert.True(scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!).IsSuccess);

        var terminate = scenario.Kernel.TerminateProcess(scenario.Subject);
        Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);

        var process = scenario.Kernel.Processes.Resolve(scenario.Subject);
        Assert.True(process.IsSuccess, process.Message);
        Assert.Equal(ProcessState.Exiting, process.Value!.State);

        var teardown = scenario.Kernel.QueryProcessTeardown(scenario.Subject);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformDraining, teardown.Value!.Phase);
        Assert.True(teardown.Value.ChannelsClosed);
        Assert.True(teardown.Value.LocalAuthorizationRevoked);
        Assert.False(teardown.Value.PlatformDomainClosed);
        Assert.False(teardown.Value.LocalReclaimCompleted);
        Assert.True(teardown.Value.PendingPlatformMappings > 0);

        var lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject,
            scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Active, lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);

        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);
    }

    [Fact]
    public void MalformedOrFaultedProviderSubmitPinsAuthorityFailClosed()
    {
        var malformed = CreateScenario(
            1507,
            1570,
            PlatformDmaDirection.DeviceWritesMemory);
        var malformedPrepare = malformed.Kernel.PreparePlatformDmaForDevice(
            malformed.Subject,
            malformed.Grant);
        Assert.True(malformedPrepare.IsSuccess, malformedPrepare.Message);
        malformed.Provider.MalformedSubmit = true;

        Assert.Equal(
            KernelError.PlatformFaulted,
            malformed.Kernel.SubmitPlatformDma(
                malformed.Subject,
                malformed.Grant,
                malformedPrepare.Value!).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            malformed.Kernel.RevokePlatformDma(
                malformed.Subject,
                malformed.Grant).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            malformed.Kernel.PreparePlatformDmaForDevice(
                malformed.Subject,
                malformed.Grant).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            malformed.Kernel.AcquirePlatformDmaForCpu(
                malformed.Subject,
                malformed.Grant).Error);
        Assert.Equal(0, malformed.Provider.DmaRevokeCalls);

        var faulted = CreateScenario(
            1508,
            1580,
            PlatformDmaDirection.DeviceReadsMemory);
        var faultedPrepare = faulted.Kernel.PreparePlatformDmaForDevice(
            faulted.Subject,
            faulted.Grant);
        Assert.True(faultedPrepare.IsSuccess, faultedPrepare.Message);
        faulted.Provider.SubmitStatus = PlatformAuthorityStatus.Faulted;
        Assert.Equal(
            KernelError.PlatformFaulted,
            faulted.Kernel.SubmitPlatformDma(
                faulted.Subject,
                faulted.Grant,
                faultedPrepare.Value!).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            faulted.Kernel.RevokePlatformDma(
                faulted.Subject,
                faulted.Grant).Error);

        var denied = CreateScenario(
            1509,
            1590,
            PlatformDmaDirection.DeviceReadsMemory);
        var deniedPrepare = denied.Kernel.PreparePlatformDmaForDevice(
            denied.Subject,
            denied.Grant);
        Assert.True(deniedPrepare.IsSuccess, deniedPrepare.Message);
        denied.Provider.SubmitStatus = PlatformAuthorityStatus.Denied;
        Assert.Equal(
            KernelError.PlatformDenied,
            denied.Kernel.SubmitPlatformDma(
                denied.Subject,
                denied.Grant,
                deniedPrepare.Value!).Error);
        denied.Provider.SubmitStatus = null;
        Assert.True(denied.Kernel.SubmitPlatformDma(
            denied.Subject,
            denied.Grant,
            deniedPrepare.Value!).IsSuccess);
        Assert.Equal(2, denied.Provider.SubmitCalls);
    }

    [Fact]
    public void ExactPendingDmaPageFaultRevalidatesAndResolvesOnce()
    {
        var scenario = CreateScenario(1511, 1610, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);

        var result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Write, 1);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(submission.OperationId, result.Value!.OperationId);
        Assert.Equal(submission.PreparedCycle, result.Value.PreparedCycle);
        Assert.False(result.Value.AuthorizesMapping);
        Assert.False(result.Value.AuthorizesMemoryAccess);
        Assert.Equal(1, scenario.Provider.PageFaultCalls);
        Assert.Equal(KernelError.PlatformDenied,
            scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
                new(40, 8), PlatformMemoryAccess.Write, 1).Error);
        Assert.Equal(1, scenario.Provider.PageFaultCalls);
    }

    [Fact]
    public void DmaPageFaultCannotWidenAdmittedRangeOrDirection()
    {
        var scenario = CreateScenario(1512, 1620, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);

        Assert.Equal(KernelError.PlatformDenied,
            scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
                new(95, 2), PlatformMemoryAccess.Write, 1).Error);
        Assert.Equal(KernelError.PlatformDenied,
            scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
                new(40, 8), PlatformMemoryAccess.Read, 2).Error);
        Assert.Equal(0, scenario.Provider.PageFaultCalls);
    }

    [Fact]
    public void ForgedDmaPageFaultSubmissionFailsBeforeProviderCallback()
    {
        var scenario = CreateScenario(1513, 1630, PlatformDmaDirection.Bidirectional);
        var submission = PrepareAndSubmit(scenario);
        var forged = submission with { Generation = new(submission.Generation.Value + 1) };

        var result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, forged,
            new(40, 8), PlatformMemoryAccess.Read | PlatformMemoryAccess.Write, 1);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, scenario.Provider.PageFaultCalls);
    }

    [Fact]
    public void MalformedDmaPageFaultEvidenceFaultPinsExactMapping()
    {
        var scenario = CreateScenario(1514, 1640, PlatformDmaDirection.DeviceReadsMemory);
        var submission = PrepareAndSubmit(scenario);
        scenario.Provider.MalformedPageFault = true;

        var result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Read, 1);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, scenario.Provider.PageFaultCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void ProviderResetDuringDmaPageFaultFaultPinsAmbiguousResolution()
    {
        var scenario = CreateScenario(1515, 1650, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);
        scenario.Provider.ResetDuringPageFault = true;

        var result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Write, 1);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, scenario.Provider.PageFaultCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void ProviderResetAfterSubmitBeforeObservationFaultPinsPendingWrite()
    {
        var scenario = CreateScenario(1532, 1830, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);
        scenario.Provider.AdvanceIncarnation();

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
                new(40, 8), PlatformMemoryAccess.Write, 1).Error);
        Assert.Equal(0, scenario.Provider.PageFaultCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        var lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.False(lifecycle.Value!.LocalReservationReleased);
    }

    [Fact]
    public async Task CompletionWaitsForInFlightPageFaultBeforeProviderObservation()
    {
        var scenario = CreateScenario(1522, 1730, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.PageFaultEntered = entered;
        scenario.Provider.PageFaultRelease = release;
        var fault = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submission, new(40, 8), PlatformMemoryAccess.Write, 1));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Error);
        }
        finally
        {
            release.Set();
        }

        Assert.True((await fault.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.True(scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submission).IsSuccess);
    }

    [Fact]
    public async Task PageFaultWaitsForInFlightCompletionBeforeProviderResolution()
    {
        var scenario = CreateScenario(1523, 1740, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.CompletionEntered = entered;
        scenario.Provider.CompletionRelease = release;
        var completion = Task.Run(() => scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submission));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.ResolvePlatformDmaPageFault(
                    scenario.Subject, submission, new(40, 8), PlatformMemoryAccess.Write, 1).Error);
            Assert.Equal(0, scenario.Provider.PageFaultCalls);
        }
        finally
        {
            release.Set();
        }

        Assert.True((await completion.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(KernelError.InvalidTransition,
            scenario.Kernel.ResolvePlatformDmaPageFault(
                scenario.Subject, submission, new(40, 8), PlatformMemoryAccess.Write, 2).Error);
    }

    [Fact]
    public async Task RevokeAndUnmapCannotCrossInFlightPageFault()
    {
        var scenario = CreateScenario(1524, 1750, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.PageFaultEntered = entered;
        scenario.Provider.PageFaultRelease = release;
        var fault = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submission, new(40, 8), PlatformMemoryAccess.Write, 1));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).Error);
            Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
            Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
            Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);
        }
        finally
        {
            release.Set();
        }

        Assert.True((await fault.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public async Task CapabilityRevokeDuringPageFaultKeepsSubmittedEffectPinned()
    {
        var scenario = CreateScenario(1525, 1760, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.PageFaultEntered = entered;
        scenario.Provider.PageFaultRelease = release;
        var fault = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submission, new(40, 8), PlatformMemoryAccess.Write, 1));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokeCapability(scenario.DeviceCapability).Error);
            Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
            Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
            Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);
        }
        finally
        {
            release.Set();
        }

        Assert.True((await fault.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        var mapping = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.False(mapping.Value!.LocalReservationReleased);
    }

    [Fact]
    public async Task ProcessTeardownDuringPageFaultKeepsSubmittedEffectPinned()
    {
        var scenario = CreateScenario(1526, 1770, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.PageFaultEntered = entered;
        scenario.Provider.PageFaultRelease = release;
        var fault = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submission, new(40, 8), PlatformMemoryAccess.Write, 1));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.TerminateProcess(scenario.Subject).Error);
            var teardown = scenario.Kernel.QueryProcessTeardown(scenario.Subject);
            Assert.True(teardown.IsSuccess, teardown.Message);
            Assert.Equal(ProcessTeardownPhase.PlatformDraining, teardown.Value!.Phase);
            Assert.False(teardown.Value.LocalReclaimCompleted);
            Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        }
        finally
        {
            release.Set();
        }

        Assert.True((await fault.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        var mapping = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.False(mapping.Value!.LocalReservationReleased);
    }

    [Fact]
    public async Task RegionOwnershipAndMutationCannotCrossPendingPageFault()
    {
        var scenario = CreateCopyScenario(1530, 1820);
        var (_, target) = TestFixtures.Create(scenario.Kernel, 1531, 1821);
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination);
        Assert.True(prepare.IsSuccess, prepare.Message);
        var submit = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject, scenario.Destination, prepare.Value!);
        Assert.True(submit.IsSuccess, submit.Message);
        var owner = new RegionOwner(new(1820), scenario.Subject.Generation);
        var before = scenario.Kernel.Regions.Validate(scenario.Output.Handle, owner);
        Assert.True(before.IsSuccess, before.Message);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.PageFaultEntered = entered;
        scenario.Provider.PageFaultRelease = release;
        var fault = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submit.Value!, new(40, 8), PlatformMemoryAccess.Write, 1));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingActive,
                scenario.Kernel.ReleaseRegion(scenario.Subject, scenario.Output).Error);
            Assert.Equal(KernelError.PlatformBindingActive,
                scenario.Kernel.TransferRegion(scenario.Subject, target, scenario.Output).Error);
            Assert.Equal(KernelError.RegionUseConflict,
                scenario.Kernel.AcquireRegionUse(scenario.Subject, scenario.Output.Handle,
                    RegionUseMode.ExclusiveWrite, new(40, 8)).Error);
            var during = scenario.Kernel.Regions.Validate(scenario.Output.Handle, owner);
            Assert.True(during.IsSuccess, during.Message);
            Assert.Equal(before.Value!.Handle.Generation, during.Value!.Handle.Generation);
            Assert.Equal(before.Value.MutationEpoch, during.Value.MutationEpoch);
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(
                scenario.Output.Handle, owner));
        }
        finally
        {
            release.Set();
        }

        Assert.True((await fault.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.ReleaseRegion(scenario.Subject, scenario.Output).Error);
    }

    [Fact]
    public void DmaPageFaultSurfaceCarriesNoRawAddressOrMappingAuthority()
    {
        Type[] surface =
        [
            typeof(PlatformProviderDmaPageFaultRequest),
            typeof(PlatformProviderDmaPageFaultEvidence),
            typeof(PlatformDmaPageFaultResolutionEvidence),
        ];
        string[] forbidden = ["Address", "Iova", "Pasid", "PageTable", "Pte", "Physical"];

        foreach (var property in surface.SelectMany(static type => type.GetProperties()))
        foreach (var term in forbidden)
            Assert.DoesNotContain(term, property.Name, StringComparison.OrdinalIgnoreCase);

        var providerEvidence = new PlatformProviderDmaPageFaultEvidence();
        var runtimeEvidence = new PlatformDmaPageFaultResolutionEvidence();
        Assert.False(providerEvidence.AuthorizesMapping);
        Assert.False(providerEvidence.AuthorizesMemoryAccess);
        Assert.False(runtimeEvidence.AuthorizesMapping);
        Assert.False(runtimeEvidence.AuthorizesMemoryAccess);
    }

    [Fact]
    public void V6BoundSubmitCarriesExactNonAuthoritativeGenerationsToProviderBoundary()
    {
        var scenario = CreateScenario(1516, 1660, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;

        var result = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);

        Assert.True(result.IsSuccess, result.Message);
        var providerBinding = Assert.IsType<DmaExecutionBindingV1>(scenario.Provider.LastBinding);
        Assert.Equal(DmaEffectStateV1.Admitted, providerBinding.EffectState);
        Assert.Equal(DmaEffectStateV1.EffectPossible, result.Value!.Binding.EffectState);
        Assert.Equal(scenario.Mapping.Region.Generation.Value, result.Value.Binding.RegionGeneration);
        Assert.Equal(scenario.Subject.Generation, result.Value.Binding.ProcessIncarnation);
        Assert.Equal(scenario.Mapping.Mapping.DomainBinding.Generation.Value,
            result.Value.Binding.AddressSpaceGeneration);
        Assert.Equal(scenario.Mapping.Mapping.Generation.Value,
            result.Value.Binding.TranslationGeneration);
        Assert.Equal(scenario.Device.Generation.Value, result.Value.Binding.DeviceLeaseGeneration);
        Assert.Equal(scenario.Grant.Generation.Value, result.Value.Binding.SessionGeneration);
        Assert.Equal(1UL, result.Value.Binding.ProviderGeneration);
        Assert.Equal(1UL, result.Value.Binding.ExternalOperationGeneration);
        Assert.False(result.Value.Binding.AuthorizesDma);
        Assert.False(result.Value.Binding.AuthorizesEffect);
        Assert.Equal(1, scenario.Provider.BoundSubmitCalls);
    }

    [Fact]
    public void V6BoundSubmitEmitsNonAuthoritativeTraceAfterOwnerCommit()
    {
        var scenario = CreateScenario(1533, 1840, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();

        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible],
            sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.All(sink.Events, item =>
        {
            Assert.False(item.AuthorizesExecution);
            Assert.False(item.AuthorizesEffect);
            Assert.False(item.AuthorizesPublication);
            Assert.Equal("singnext.platform-dma", item.Source);
        });
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Theory]
    [InlineData("backend-reset")]
    [InlineData("provider-reset")]
    [InlineData("faulted")]
    [InlineData("malformed")]
    public void V6AmbiguousSubmitTraceRetainsPossibleEffectAndQuarantine(string fault)
    {
        var scenario = CreateScenario(1552, 2030, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        if (fault == "backend-reset")
            scenario.Provider.SubmitEffect = () =>
                Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
        else if (fault == "provider-reset") scenario.Provider.ResetDuringSubmit = true;
        else if (fault == "faulted") scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Faulted;
        else scenario.Provider.MalformedSubmit = true;

        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);

        Assert.Equal(KernelError.PlatformFaulted, submitted.Error);
        Assert.Equal(1, scenario.Provider.BoundSubmitCalls);
        Assert.Equal(SemanticTraceEventKindV1.Submit, sink.Events[0].Kind);
        Assert.Equal(SemanticTraceEventKindV1.EffectPossible, sink.Events[1].Kind);
        Assert.Equal(SemanticTraceEventKindV1.Quarantined, sink.Events[^1].Kind);
        Assert.Equal(fault is "backend-reset" or "provider-reset" ? 4 : 3,
            sink.Events.Count);
        if (sink.Events.Count == 4)
            Assert.Equal(SemanticTraceEventKindV1.GenerationChanged, sink.Events[2].Kind);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
    }

    [Fact]
    public void V6DeniedSubmitQuarantinesPossibleEffectTrace()
    {
        var scenario = CreateScenario(1553, 2040, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Denied;
        var sink = new DmaTraceSink();

        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);

        Assert.False(submitted.IsSuccess);
        Assert.Equal(new[] { SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.Quarantined },
            sink.Events.Select(e => e.Kind));
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Fact]
    public void V6LostProviderIncarnationQuarantinesWithoutInventingGenerationDigest()
    {
        var scenario = CreateScenario(1556, 2070, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitEffect = scenario.Provider.LoseIncarnation;
        var sink = new DmaTraceSink();

        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);

        Assert.Equal(KernelError.PlatformFaulted, submitted.Error);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined], sink.Events.Select(static item => item.Kind));
        Assert.Equal(sink.Events[0].GenerationVectorDigest,
            sink.Events[^1].GenerationVectorDigest);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
    }

    [Fact]
    public void V6AmbiguousSubmitTraceSinkReentersAfterOwnerLockAndCannotReleaseGrant()
    {
        var scenario = CreateScenario(1554, 2050, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Faulted;
        var reentered = false;
        var sink = new DmaTraceSink
        {
            OnRecord = () =>
            {
                reentered = true;
                Assert.Equal(KernelError.PlatformFaulted,
                    scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
            },
        };

        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);

        Assert.Equal(KernelError.PlatformFaulted, submitted.Error);
        Assert.True(reentered);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Fact]
    public void V6AmbiguousSubmitTraceSinkFailureCannotReleaseGrant()
    {
        var scenario = CreateScenario(1555, 2060, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Faulted;
        var sink = new DmaTraceSink { ThrowOnRecord = true };

        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);

        Assert.Equal(KernelError.PlatformFaulted, submitted.Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundSubmitTraceSinkFailureCannotChangeCommittedEffect()
    {
        var scenario = CreateScenario(1534, 1850, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink { ThrowOnRecord = true };

        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(1, scenario.Provider.BoundSubmitCalls);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundDmaCompletionTraceFollowsCommittedOwnerOrder()
    {
        var scenario = CreateScenario(1535, 1860, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);

        var completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value!.Submission);

        Assert.True(completion.IsSuccess, completion.Message);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete],
            sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundDmaResetTraceMarksNewGenerationAndQuarantine()
    {
        var scenario = CreateScenario(1536, 1870, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        scenario.Provider.AdvanceIncarnation();

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject, submitted.Value!.Submission).Error);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined],
            sink.Events.Select(static item => item.Kind));
        Assert.NotEqual(sink.Events[1].GenerationVectorDigest,
            sink.Events[2].GenerationVectorDigest);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(0, scenario.Provider.CompletionCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundDmaProviderDriftAfterQuarantineRetainsGenerationObservation()
    {
        var scenario = CreateScenario(1547, 1980, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        scenario.Provider.MalformedPageFault = true;
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject,
                submitted.Value!.Submission, new(40, 8), PlatformMemoryAccess.Write, 1).Error);
        scenario.Provider.AdvanceIncarnation();

        var completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value.Submission);
        scenario.Provider.AdvanceIncarnation();
        var laterCompletion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value.Submission);

        Assert.Equal(KernelError.PlatformFaulted, completion.Error);
        Assert.Equal(KernelError.PlatformFaulted, laterCompletion.Error);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.GenerationChanged,
            SemanticTraceEventKindV1.GenerationChanged],
            sink.Events.Select(static item => item.Kind));
        Assert.NotEqual(sink.Events[2].GenerationVectorDigest,
            sink.Events[3].GenerationVectorDigest);
        Assert.NotEqual(sink.Events[3].GenerationVectorDigest,
            sink.Events[4].GenerationVectorDigest);
        var providerDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            (submitted.Value.Binding with
            {
                EffectState = DmaEffectStateV1.Admitted,
                ProviderGeneration = scenario.Provider.CurrentIncarnation.Value,
            }).SerializeCanonical()));
        Assert.Equal(providerDigest, sink.Events[4].GenerationVectorDigest);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(0, scenario.Provider.CompletionCalls);
    }

    [Fact]
    public void V6BoundDmaCompletionTraceSinkFailureCannotChangeOwnerResult()
    {
        var scenario = CreateScenario(1538, 1890, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        sink.ThrowOnRecord = true;

        var completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value!.Submission);

        Assert.True(completion.IsSuccess, completion.Message);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible],
            sink.Events.Select(static item => item.Kind));
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundDmaTraceSinkMayReenterAfterOwnerLock()
    {
        var scenario = CreateScenario(1537, 1880, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink
        {
            OnRecord = () => Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error),
        };

        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(2, sink.Events.Count);
    }

    [Theory]
    [InlineData(PlatformDmaDirection.DeviceReadsMemory)]
    [InlineData(PlatformDmaDirection.DeviceWritesMemory)]
    public void V6BoundDmaVisibilityTraceFollowsExactPostCompletion(
        PlatformDmaDirection direction)
    {
        var scenario = CreateScenario(1539, 1900, direction);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value!.Submission);
        Assert.True(completion.IsSuccess, completion.Message);

        var visible = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject, submitted.Value.Submission, completion.Value!);

        Assert.True(visible.IsSuccess, visible.Message);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible],
            sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(direction == PlatformDmaDirection.DeviceReadsMemory ? 0 : 1,
            scenario.Provider.AcquireCalls);
    }

    [Fact]
    public void V6BoundDmaResetDuringAcquireTraceQuarantinesWithoutVisibility()
    {
        var scenario = CreateScenario(1540, 1910, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value!.Submission);
        Assert.True(completion.IsSuccess, completion.Message);
        scenario.Provider.ResetDuringAcquire = true;

        var visible = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject, submitted.Value.Submission, completion.Value!);

        Assert.Equal(KernelError.PlatformFaulted, visible.Error);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined],
            sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundDmaTraceRetainsOrderedEventsAcrossReentrantRemoval()
    {
        var scenario = CreateScenario(1541, 1920, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        var reentered = false;
        sink.OnRecord = () =>
        {
            if (reentered) return;
            reentered = true;
            var exact = scenario.Kernel.GetOrObservePlatformDmaCompletion(
                scenario.Subject, submitted.Value!.Submission);
            Assert.True(exact.IsSuccess, exact.Message);
            var visible = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
                scenario.Subject, submitted.Value.Submission, exact.Value!);
            Assert.True(visible.IsSuccess, visible.Message);
        };

        var completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value!.Submission);

        Assert.True(completion.IsSuccess, completion.Message);
        Assert.True(reentered);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible],
            sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Fact]
    public void V6BoundDmaMalformedPageFaultTraceQuarantinesExactSubmission()
    {
        var scenario = CreateScenario(1542, 1930, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        scenario.Provider.MalformedPageFault = true;

        var resolution = scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submitted.Value!.Submission, new(40, 8),
            PlatformMemoryAccess.Write, 1);

        Assert.Equal(KernelError.PlatformFaulted, resolution.Error);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined],
            sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundDmaPageFaultResetTraceMarksGenerationAndQuarantine()
    {
        var scenario = CreateScenario(1543, 1940, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        scenario.Provider.ResetDuringPageFault = true;

        var resolution = scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submitted.Value!.Submission, new(40, 8),
            PlatformMemoryAccess.Write, 1);

        Assert.Equal(KernelError.PlatformFaulted, resolution.Error);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined],
            sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundDmaPageFaultBackendResetDoesNotObserveLaterProviderDrift()
    {
        var scenario = CreateScenario(1552, 2030, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        ulong observedBackendEpoch = 0;
        scenario.Provider.PageFaultEffect = () =>
        {
            var reset = scenario.Kernel.ObservePlatformBackendReset();
            Assert.True(reset.IsSuccess, reset.Message);
            observedBackendEpoch = reset.Value!.CurrentEpoch.Value;
            scenario.Provider.AdvanceIncarnation();
        };

        var resolution = scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submitted.Value!.Submission, new(40, 8),
            PlatformMemoryAccess.Write, 1);

        Assert.Equal(KernelError.PlatformFaulted, resolution.Error);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined],
            sink.Events.Select(static item => item.Kind));
        var admittedDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            (submitted.Value.Binding with { EffectState = DmaEffectStateV1.Admitted }).SerializeCanonical()));
        var epochDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"dma-owner/backend-epoch/v1|{admittedDigest}|{observedBackendEpoch}"))));
        Assert.Equal(epochDigest, sink.Events[2].GenerationVectorDigest);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundDmaBackendResetTraceMarksLocalEpochAndQuarantine()
    {
        var scenario = CreateScenario(1544, 1950, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);

        var reset = scenario.Kernel.ObservePlatformBackendReset();

        Assert.True(reset.IsSuccess, reset.Message);
        Assert.Equal(1, reset.Value!.FaultPinnedDmaSubmissions);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined],
            sink.Events.Select(static item => item.Kind));
        Assert.NotEqual(sink.Events[1].GenerationVectorDigest,
            sink.Events[2].GenerationVectorDigest);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(0, scenario.Provider.CompletionCalls);
        Assert.False(scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value!.Submission).IsSuccess);
    }

    [Fact]
    public void V6BoundDmaRepeatedBackendResetPreservesEveryEpochAfterQuarantine()
    {
        var scenario = CreateScenario(1545, 1960, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);

        var first = scenario.Kernel.ObservePlatformBackendReset();
        var second = scenario.Kernel.ObservePlatformBackendReset();

        Assert.True(first.IsSuccess, first.Message);
        Assert.True(second.IsSuccess, second.Message);
        Assert.Equal(1, first.Value!.FaultPinnedDmaSubmissions);
        Assert.Equal(0, second.Value!.FaultPinnedDmaSubmissions);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.GenerationChanged],
            sink.Events.Select(static item => item.Kind));
        Assert.NotEqual(sink.Events[2].GenerationVectorDigest,
            sink.Events[4].GenerationVectorDigest);
        var admittedDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            (submitted.Value.Binding with { EffectState = DmaEffectStateV1.Admitted }).SerializeCanonical()));
        var expectedCurrentEpochDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"dma-owner/backend-epoch/v1|{admittedDigest}|{second.Value.CurrentEpoch.Value}"))));
        Assert.Equal(expectedCurrentEpochDigest, sink.Events[4].GenerationVectorDigest);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundDmaBackendResetDoesNotInventUnobservedProviderGeneration()
    {
        var scenario = CreateScenario(1548, 1990, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);

        Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
        scenario.Provider.AdvanceIncarnation();
        var nextReset = scenario.Kernel.ObservePlatformBackendReset();
        Assert.True(nextReset.IsSuccess, nextReset.Message);
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value!.Submission);

        Assert.False(completion.IsSuccess);
        Assert.Equal(0, scenario.Provider.CompletionCalls);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.GenerationChanged],
            sink.Events.Select(static item => item.Kind));
        var admittedDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            (submitted.Value.Binding with { EffectState = DmaEffectStateV1.Admitted }).SerializeCanonical()));
        var observedEpochDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"dma-owner/backend-epoch/v1|{admittedDigest}|{nextReset.Value!.CurrentEpoch.Value}"))));
        Assert.Equal(observedEpochDigest, sink.Events[4].GenerationVectorDigest);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Fact]
    public void V6BoundDmaBackendResetPreservesLastObservedProviderGeneration()
    {
        var scenario = CreateScenario(1549, 2000, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        var submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        scenario.Provider.MalformedPageFault = true;
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject,
                submitted.Value!.Submission, new(40, 8), PlatformMemoryAccess.Write, 1).Error);
        scenario.Provider.AdvanceIncarnation();
        var observedGeneration = scenario.Provider.CurrentIncarnation.Value;
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject,
                submitted.Value.Submission).Error);

        var reset = scenario.Kernel.ObservePlatformBackendReset();
        Assert.True(reset.IsSuccess, reset.Message);
        scenario.Provider.AdvanceIncarnation();
        var laterReset = scenario.Kernel.ObservePlatformBackendReset();
        Assert.True(laterReset.IsSuccess, laterReset.Message);

        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.GenerationChanged,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.GenerationChanged],
            sink.Events.Select(static item => item.Kind));
        var providerDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            (submitted.Value.Binding with
            {
                EffectState = DmaEffectStateV1.Admitted,
                ProviderGeneration = observedGeneration,
            }).SerializeCanonical()));
        var expected = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"dma-owner/backend-epoch/v1|{providerDigest}|{laterReset.Value!.CurrentEpoch.Value}"))));
        Assert.Equal(expected, sink.Events[^1].GenerationVectorDigest);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(0, scenario.Provider.CompletionCalls);
    }

    [Fact]
    public void V6BoundProviderDenialFaultPinsPreparedCycle()
    {
        var scenario = CreateScenario(1517, 1670, PlatformDmaDirection.DeviceReadsMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Denied;

        var denied = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);
        scenario.Provider.SubmitStatus = null;
        var retried = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);

        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        Assert.Equal(KernelError.PlatformFaulted, retried.Error);
        Assert.Equal(1, scenario.Provider.BoundSubmitCalls);
    }

    [Fact]
    public void V6BoundExplicitNotAcceptedLeavesPreparedCycleRetryable()
    {
        var scenario = CreateScenario(1518, 1680, PlatformDmaDirection.DeviceReadsMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.NotAccepted;
        var notAccepted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);
        scenario.Provider.SubmitStatus = null;
        var retried = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);
        Assert.False(notAccepted.IsSuccess);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(2, scenario.Provider.BoundSubmitCalls);
    }

    [Fact]
    public void V6BoundTransportUnavailableRetainsPossibleWriteAndBlocksRevoke()
    {
        var scenario = CreateScenario(1519, 1690, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        var sink = new DmaTraceSink();
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Unavailable;

        var unavailable = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        scenario.Provider.SubmitStatus = null;
        var retried = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);

        Assert.False(unavailable.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, retried.Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(1, scenario.Provider.BoundSubmitCalls);
        Assert.Equal(new[] { SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.Quarantined },
            sink.Events.Select(e => e.Kind));
    }

    [Fact]
    public void ProviderResetDuringV6BoundSubmitFaultPinsMapping()
    {
        var scenario = CreateScenario(1518, 1680, PlatformDmaDirection.DeviceWritesMemory);
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.ResetDuringSubmit = true;

        var result = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, scenario.Provider.BoundSubmitCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void ProviderResetBeforeGrantRevokeAfterCompletionPinsMappingWithoutCallback()
    {
        var scenario = CreateScenario(1545, 1960, PlatformDmaDirection.DeviceReadsMemory);
        CompleteDmaForGrantRevoke(scenario);
        scenario.Provider.AdvanceIncarnation();

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject, scenario.Mapping).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderResetDuringGrantRevokeCannotTurnReceiptIntoClosure(bool revokedReceipt)
    {
        var scenario = CreateScenario(1546, 1970, PlatformDmaDirection.DeviceReadsMemory);
        CompleteDmaForGrantRevoke(scenario);
        scenario.Provider.ResetDuringRevoke = true;
        scenario.Provider.RevokedDuringRevoke = revokedReceipt;

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject, scenario.Mapping).IsSuccess);
    }

    [Fact]
    public void ProviderThrowDuringGrantRevokePinsMappingAfterPossibleClosure()
    {
        var scenario = CreateScenario(1547, 1980, PlatformDmaDirection.DeviceReadsMemory);
        CompleteDmaForGrantRevoke(scenario);
        scenario.Provider.ThrowDuringRevoke = true;

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        scenario.Provider.ThrowDuringRevoke = false;
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject, scenario.Mapping).IsSuccess);
    }

    [Fact]
    public void BackendResetDuringGrantRevokeCannotTurnReceiptIntoClosure()
    {
        var scenario = CreateScenario(1548, 1990, PlatformDmaDirection.DeviceReadsMemory);
        CompleteDmaForGrantRevoke(scenario);
        scenario.Provider.RevokeEffect = () =>
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject, scenario.Mapping).IsSuccess);
    }

    [Fact]
    public void ProviderResetBeforeMappingRevokeKeepsExactReservationPinned()
    {
        var scenario = CreateScenario(1549, 2000, PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(
            scenario.Subject, scenario.Grant).IsSuccess);
        scenario.Provider.AdvanceIncarnation();

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject, scenario.Mapping).Error);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        var lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Faulted,
            lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderResetDuringMappingClosureCannotMakeReceiptReusable(bool duringBegin)
    {
        var scenario = CreateScenario(1550, 2010, PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(
            scenario.Subject, scenario.Grant).IsSuccess);
        scenario.Provider.ResetDuringMappingBegin = duringBegin;
        scenario.Provider.ResetDuringMappingObserve = !duringBegin;

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject, scenario.Mapping).Error);
        var lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Faulted,
            lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
    }

    [Fact]
    public void ProviderThrowDuringMappingObservationKeepsReservationPinned()
    {
        var scenario = CreateScenario(1551, 2020, PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(
            scenario.Subject, scenario.Grant).IsSuccess);
        scenario.Provider.ThrowDuringMappingObserve = true;

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject, scenario.Mapping).Error);
        var lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Faulted,
            lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
    }

    [Fact]
    public void ProviderResetDuringExactMappingAdmissionReturnsQuarantinedHandle()
    {
        var scenario = CreateScenario(1552, 2030, PlatformDmaDirection.DeviceReadsMemory,
            resetDuringMappingAdmission: true);

        var lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Faulted,
            lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device,
                scenario.Mapping, 32, 64, PlatformDmaDirection.DeviceReadsMemory).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject, scenario.Mapping).Error);
    }

    [Fact]
    public void BackendResetDuringMappingObservationCannotReleaseReservation()
    {
        var scenario = CreateScenario(1558, 2090, PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(
            scenario.Subject, scenario.Grant).IsSuccess);
        scenario.Provider.MappingObserveEffect = () =>
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject, scenario.Mapping).Error);
        Assert.False(scenario.Kernel.RevokePlatformDevice(
            scenario.Subject, scenario.Device).IsSuccess);
    }

    [Fact]
    public void MalformedLegacyMappingCleanupFailureRetainsRegionReservation()
    {
        var provider = new SubmissionProvider
        {
            MalformedLegacyMapAdmission = true,
            FailMappingCleanup = true,
        };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1561, 2120);
        var (_, target) = TestFixtures.Create(kernel, 1562, 2130);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var buffer = kernel.AllocateBuffer<byte>(subject, 64).Value!;
        var capability = Mint(kernel, subject, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read);

        var mapped = kernel.MapPlatformOwnedRegion(subject, binding, capability,
            buffer.Handle, PlatformMemoryAccess.Read);

        Assert.Equal(KernelError.PlatformFaulted, mapped.Error);
        Assert.Equal(1, provider.MappingRevokeCalls);
        Assert.True(kernel.PlatformAuthority.HasActiveMapping(buffer.Handle));
        Assert.False(kernel.TransferRegion(subject, target, buffer).IsSuccess);
    }

    [Fact]
    public void MalformedLegacyMappingCleanupReceiptAfterProviderResetCannotReleaseReservation()
    {
        var provider = new SubmissionProvider
        {
            MalformedLegacyMapAdmission = true,
            ResetDuringMappingCleanup = true,
        };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1563, 2140);
        var (_, target) = TestFixtures.Create(kernel, 1564, 2150);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var buffer = kernel.AllocateBuffer<byte>(subject, 64).Value!;
        var capability = Mint(kernel, subject, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read);

        var mapped = kernel.MapPlatformOwnedRegion(subject, binding, capability,
            buffer.Handle, PlatformMemoryAccess.Read);

        Assert.Equal(KernelError.PlatformFaulted, mapped.Error);
        Assert.Equal(1, provider.MappingRevokeCalls);
        Assert.True(kernel.PlatformAuthority.HasActiveMapping(buffer.Handle));
        Assert.False(kernel.TransferRegion(subject, target, buffer).IsSuccess);
    }

    [Fact]
    public void AmbiguousLegacyMappingFailureWithoutLeaseRetainsRegionReservation()
    {
        var provider = new SubmissionProvider
        {
            LegacyMapFailureStatus = PlatformAuthorityStatus.Faulted,
        };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1565, 2160);
        var (_, target) = TestFixtures.Create(kernel, 1566, 2170);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var buffer = kernel.AllocateBuffer<byte>(subject, 64).Value!;
        var capability = Mint(kernel, subject, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read);

        var mapped = kernel.MapPlatformOwnedRegion(subject, binding, capability,
            buffer.Handle, PlatformMemoryAccess.Read);

        Assert.Equal(KernelError.PlatformFaulted, mapped.Error);
        Assert.True(kernel.PlatformAuthority.HasActiveMapping(buffer.Handle));
        Assert.False(kernel.TransferRegion(subject, target, buffer).IsSuccess);

        Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
        Assert.True(kernel.PlatformAuthority.HasActiveMapping(buffer.Handle));
        Assert.False(kernel.TransferRegion(subject, target, buffer).IsSuccess);
    }

    [Fact]
    public void ProviderResetBeforeDeviceRevokeAfterDmaClosureKeepsLeasePinned()
    {
        var scenario = CreateScenario(1553, 2040, PlatformDmaDirection.DeviceReadsMemory);
        CloseGrantAndMappingForDeviceRevoke(scenario);
        scenario.Provider.AdvanceIncarnation();

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDevice(
                scenario.Subject, scenario.Device).Error);
        Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);
    }

    [Fact]
    public void ProviderResetDuringDeviceRevokeCannotCloseLease()
    {
        var scenario = CreateScenario(1554, 2050, PlatformDmaDirection.DeviceReadsMemory);
        CloseGrantAndMappingForDeviceRevoke(scenario);
        scenario.Provider.ResetDuringDeviceRevoke = true;

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDevice(
                scenario.Subject, scenario.Device).Error);
        Assert.Equal(1, scenario.Provider.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDevice(
                scenario.Subject, scenario.Device).Error);
        Assert.Equal(1, scenario.Provider.DeviceRevokeCalls);
    }

    [Fact]
    public void ProviderThrowDuringDeviceRevokeKeepsLeasePinned()
    {
        var scenario = CreateScenario(1555, 2060, PlatformDmaDirection.DeviceReadsMemory);
        CloseGrantAndMappingForDeviceRevoke(scenario);
        scenario.Provider.ThrowDuringDeviceRevoke = true;

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDevice(
                scenario.Subject, scenario.Device).Error);
        Assert.Equal(1, scenario.Provider.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDevice(
                scenario.Subject, scenario.Device).Error);
    }

    [Fact]
    public void BackendResetDuringDeviceRevokeCannotCloseLease()
    {
        var scenario = CreateScenario(1556, 2070, PlatformDmaDirection.DeviceReadsMemory);
        CloseGrantAndMappingForDeviceRevoke(scenario);
        scenario.Provider.DeviceRevokeEffect = () =>
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDevice(
                scenario.Subject, scenario.Device).Error);
        Assert.Equal(1, scenario.Provider.DeviceRevokeCalls);
    }

    [Fact]
    public void ProviderResetDuringDeviceAdmissionReturnsFaultPinnedLease()
    {
        var scenario = CreateScenario(1557, 2080, PlatformDmaDirection.DeviceReadsMemory,
            resetDuringDeviceAdmission: true);

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.PlatformAuthority.ValidateDeviceLease(
                scenario.Device, scenario.Device.DomainBinding.Subject).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDevice(
                scenario.Subject, scenario.Device).Error);
        Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);
    }

    [Fact]
    public void MalformedExactMappingAdmissionRetainsFaultedReservation()
    {
        var scenario = CreateScenario(1559, 2100, PlatformDmaDirection.DeviceReadsMemory,
            malformedMappingAdmission: true);

        var lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Faulted,
            lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device,
                scenario.Mapping, 32, 64, PlatformDmaDirection.DeviceReadsMemory).Error);
    }

    private static void CloseGrantAndMappingForDeviceRevoke(Scenario scenario)
    {
        Assert.True(scenario.Kernel.RevokePlatformDma(
            scenario.Subject, scenario.Grant).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject, scenario.Mapping).IsSuccess);
    }

    private static void CompleteDmaForGrantRevoke(Scenario scenario)
    {
        var submission = PrepareAndSubmit(scenario);
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submission);
        Assert.True(completion.IsSuccess, completion.Message);
        var visibility = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject, submission, completion.Value!);
        Assert.True(visibility.IsSuccess, visibility.Message);
    }

    private static PlatformDmaSubmission PrepareAndSubmit(Scenario scenario)
    {
        var prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        return scenario.Kernel.SubmitPlatformDma(
            scenario.Subject, scenario.Grant, prepared).Value!;
    }

    private static Scenario CreateScenario(
        ulong processId,
        ulong domainId,
        PlatformDmaDirection direction,
        bool resetDuringMappingAdmission = false,
        bool resetDuringDeviceAdmission = false,
        bool malformedMappingAdmission = false)
    {
        var provider = new SubmissionProvider();
        provider.ResetDuringMapAdmission = resetDuringMappingAdmission;
        provider.ResetDuringDeviceAdmission = resetDuringDeviceAdmission;
        provider.MalformedMapAdmission = malformedMappingAdmission;
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, processId, domainId);
        var binding = kernel.BindPlatformDomain(subject).Value!;

        var deviceRights = PlatformDeviceRights.Configure;
        var mappingAccess = PlatformMemoryAccess.None;
        switch (direction)
        {
            case PlatformDmaDirection.DeviceReadsMemory:
                deviceRights |= PlatformDeviceRights.Read;
                mappingAccess |= PlatformMemoryAccess.Read;
                break;
            case PlatformDmaDirection.DeviceWritesMemory:
                deviceRights |= PlatformDeviceRights.Write;
                mappingAccess |= PlatformMemoryAccess.Write;
                break;
            case PlatformDmaDirection.Bidirectional:
                deviceRights |= PlatformDeviceRights.Read | PlatformDeviceRights.Write;
                mappingAccess |= PlatformMemoryAccess.Read | PlatformMemoryAccess.Write;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(direction));
        }

        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma-submit0",
            ToCapabilityRights(deviceRights));
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            deviceRights).Value!;

        var buffer = kernel.AllocateBuffer<byte>(subject, 512).Value!;
        var memoryRights = CapabilityRights.Map;
        if ((mappingAccess & PlatformMemoryAccess.Read) != 0) memoryRights |= CapabilityRights.Read;
        if ((mappingAccess & PlatformMemoryAccess.Write) != 0) memoryRights |= CapabilityRights.Write;
        var memoryCapability = Mint(
            kernel,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            memoryRights);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            subject,
            binding,
            memoryCapability,
            buffer.Handle,
            64,
            256,
            mappingAccess).Value!;
        var grant = resetDuringMappingAdmission || resetDuringDeviceAdmission ||
                    malformedMappingAdmission
            ? default
            : kernel.BindPlatformDma(
                subject,
                device,
                mapping,
                32,
                64,
                direction).Value!;

        return new Scenario(
            kernel,
            provider,
            subject,
            deviceCapability,
            device,
            mapping,
            grant);
    }

    private static CopyScenario CreateCopyScenario(ulong processId, ulong domainId)
    {
        var provider = new SubmissionProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, processId, domainId);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        const PlatformDeviceRights deviceRights = PlatformDeviceRights.Read |
            PlatformDeviceRights.Write | PlatformDeviceRights.Configure;
        var deviceCapability = Mint(kernel, subject, ResourceKind.Device, "device/dma-copy0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(subject, binding, deviceCapability, deviceRights).Value!;

        (OwnedBuffer<byte> Buffer, PlatformOwnedRegionSliceMapping Mapping, PlatformDmaGrant Grant)
            Grant(PlatformMemoryAccess access, PlatformDmaDirection direction)
        {
            var buffer = kernel.AllocateBuffer<byte>(subject, 64).Value!;
            var rights = CapabilityRights.Map |
                ((access & PlatformMemoryAccess.Read) != 0 ? CapabilityRights.Read : 0) |
                ((access & PlatformMemoryAccess.Write) != 0 ? CapabilityRights.Write : 0);
            var capability = Mint(kernel, subject, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), rights);
            var mapping = kernel.MapPlatformOwnedRegionSlice(subject, binding, capability,
                buffer.Handle, 0, 64, access).Value!;
            var grant = kernel.BindPlatformDma(subject, device, mapping, 0, 64, direction).Value!;
            return (buffer, mapping, grant);
        }

        var source = Grant(PlatformMemoryAccess.Read, PlatformDmaDirection.DeviceReadsMemory);
        var destination = Grant(PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);
        var descriptor = kernel.Regions.Validate(source.Buffer.Handle,
            new RegionOwner(new(domainId), subject.Generation)).Value!;
        var plan = new DataMotionPlanV1(1, "dma-source", "dma-destination", 3, 5,
            source.Buffer.Handle.Generation.Value, descriptor.MutationEpoch.Value, 64, 64,
            DataMotionModeV1.ProviderDmaThenReleaseSource).Validate();
        return new(kernel, provider, subject, source.Buffer, destination.Buffer,
            source.Mapping, destination.Mapping, source.Grant, destination.Grant, plan);
    }

    private static CrossOwnerCopyScenario CreateCrossOwnerCopyScenario(
        ulong sourceProcessId,
        ulong sourceDomainId,
        ulong destinationProcessId,
        ulong destinationDomainId)
    {
        var provider = new SubmissionProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, sourceSubject) = TestFixtures.Create(kernel, sourceProcessId, sourceDomainId);
        var (_, destinationSubject) = TestFixtures.Create(
            kernel, destinationProcessId, destinationDomainId);
        var sourceBinding = kernel.BindPlatformDomain(sourceSubject).Value!;
        var destinationBinding = kernel.BindPlatformDomain(destinationSubject).Value!;
        const PlatformDeviceRights deviceRights = PlatformDeviceRights.Read |
            PlatformDeviceRights.Write | PlatformDeviceRights.Configure;

        (OwnedBuffer<byte> Buffer, PlatformOwnedRegionSliceMapping Mapping,
            PlatformDmaGrant Grant) Leg(
                ProcessHandle subject,
                PlatformDomainBinding binding,
                string deviceName,
                PlatformMemoryAccess access,
                PlatformDmaDirection direction)
        {
            var deviceCapability = Mint(kernel, subject, ResourceKind.Device, deviceName,
                CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
            var device = kernel.BindPlatformDevice(
                subject, binding, deviceCapability, deviceRights).Value!;
            var buffer = kernel.AllocateBuffer<byte>(subject, 64).Value!;
            var rights = CapabilityRights.Map |
                ((access & PlatformMemoryAccess.Read) != 0 ? CapabilityRights.Read : 0) |
                ((access & PlatformMemoryAccess.Write) != 0 ? CapabilityRights.Write : 0);
            var capability = Mint(kernel, subject, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), rights);
            var mapping = kernel.MapPlatformOwnedRegionSlice(
                subject, binding, capability, buffer.Handle, 0, 64, access).Value!;
            var grant = kernel.BindPlatformDma(
                subject, device, mapping, 0, 64, direction).Value!;
            return (buffer, mapping, grant);
        }

        var source = Leg(sourceSubject, sourceBinding, "device/dma-copy-source",
            PlatformMemoryAccess.Read, PlatformDmaDirection.DeviceReadsMemory);
        var destination = Leg(destinationSubject, destinationBinding,
            "device/dma-copy-destination", PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);
        var descriptor = kernel.Regions.Validate(source.Buffer.Handle,
            new RegionOwner(new(sourceDomainId), sourceSubject.Generation)).Value!;
        var plan = new DataMotionPlanV1(1, "dma-source", "dma-destination", 3, 5,
            source.Buffer.Handle.Generation.Value, descriptor.MutationEpoch.Value, 64, 64,
            DataMotionModeV1.ProviderDmaThenReleaseSource).Validate();
        return new(kernel, provider, sourceSubject, destinationSubject,
            source.Buffer, destination.Buffer, source.Mapping, destination.Mapping,
            source.Grant, destination.Grant, plan);
    }

    private static CapabilityId Mint(
        RuntimeKernel kernel,
        ProcessHandle subject,
        ResourceKind kind,
        string resourceId,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var minted = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            kind,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }

    private static CapabilityRights ToCapabilityRights(PlatformDeviceRights rights)
    {
        var result = CapabilityRights.None;
        if ((rights & PlatformDeviceRights.Read) != 0) result |= CapabilityRights.Read;
        if ((rights & PlatformDeviceRights.Write) != 0) result |= CapabilityRights.Write;
        if ((rights & PlatformDeviceRights.Configure) != 0) result |= CapabilityRights.Configure;
        return result;
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        SubmissionProvider Provider,
        ProcessHandle Subject,
        CapabilityId DeviceCapability,
        PlatformDeviceLease Device,
        PlatformOwnedRegionSliceMapping Mapping,
        PlatformDmaGrant Grant);

    private sealed class DmaTraceSink : ISemanticTraceSinkV1
    {
        public List<SemanticTraceEventV1> Events { get; } = [];
        public bool ThrowOnRecord { get; set; }
        public Action? OnRecord { get; set; }

        public bool TryRecord(SemanticTraceEventV1 traceEvent)
        {
            if (ThrowOnRecord) throw new InvalidOperationException("Injected trace sink failure.");
            OnRecord?.Invoke();
            Events.Add(traceEvent);
            return true;
        }
    }

    private sealed record CopyScenario(RuntimeKernel Kernel, SubmissionProvider Provider,
        ProcessHandle Subject, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output,
        PlatformOwnedRegionSliceMapping SourceMapping,
        PlatformOwnedRegionSliceMapping DestinationMapping,
        PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan);

    private sealed record CrossOwnerCopyScenario(
        RuntimeKernel Kernel,
        SubmissionProvider Provider,
        ProcessHandle SourceSubject,
        ProcessHandle DestinationSubject,
        OwnedBuffer<byte> Input,
        OwnedBuffer<byte> Output,
        PlatformOwnedRegionSliceMapping SourceMapping,
        PlatformOwnedRegionSliceMapping DestinationMapping,
        PlatformDmaGrant Source,
        PlatformDmaGrant Destination,
        DataMotionPlanV1 Plan);

    private sealed class SubmissionProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformDmaGrantProvider,
        IPlatformDmaVisibilityProvider,
        IPlatformDmaBoundSubmissionProvider,
        IPlatformDmaCopySubmissionProvider,
        IPlatformDmaCompletionProvider,
        IPlatformDmaPageFaultProvider,
        IPlatformRegionRevocationProvider,
        IPlatformProviderIncarnationSource
    {
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, PlatformProviderOwnedRegionMapping> _mappings = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaGrant> _grants = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaVisibilityCycle> _cycles = [];
        private readonly HashSet<PlatformProviderDmaGrantId> _acquired = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaSubmission> _submissions = [];
        private readonly Dictionary<PlatformOperationId,
            (PlatformOperationIdentity Operation, PlatformProviderRegionMappingId Mapping)> _revocations = [];
        private readonly Dictionary<PlatformProviderDomainLeaseId,
            PlatformProviderDomainLease> _domains = [];
        private ulong _nextDomain = 1;
        private ulong _nextDevice = 1;
        private ulong _nextMapping = 1;
        private ulong _nextGrant = 1;
        private ulong _nextCycle = 1;
        private ulong _nextSubmission = 1;
        private ulong _nextOperation = 1;

        public int PrepareCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public int SubmitCalls { get; private set; }
        public int BoundSubmitCalls { get; private set; }
        public int CopySubmitCalls { get; private set; }
        public int PageFaultCalls { get; private set; }
        public int CompletionCalls { get; private set; }
        public int DmaRevokeCalls { get; private set; }
        public int MappingRevokeCalls { get; private set; }
        public int DeviceRevokeCalls { get; private set; }
        public bool MalformedSubmit { get; set; }
        public bool MalformedCopySubmit { get; set; }
        public PlatformAuthorityStatus? SubmitStatus { get; set; }
        public PlatformAuthorityStatus? CopySubmitStatus { get; set; }
        public bool ResetDuringSubmit { get; set; }
        public bool ResetDuringCopySubmit { get; set; }
        public bool KeepWriteCompletionPending { get; set; }
        public Action? CopyEffect { get; set; }
        public Action? SubmitEffect { get; set; }
        public bool ResetDuringPageFault { get; set; }
        public Action? PageFaultEffect { get; set; }
        public bool ResetDuringAcquire { get; set; }
        public bool ResetDuringRevoke { get; set; }
        public bool RevokedDuringRevoke { get; set; }
        public bool ThrowDuringRevoke { get; set; }
        public Action? RevokeEffect { get; set; }
        public bool ResetDuringMappingBegin { get; set; }
        public bool ResetDuringMappingObserve { get; set; }
        public bool ThrowDuringMappingObserve { get; set; }
        public bool ResetDuringMapAdmission { get; set; }
        public Action? MappingObserveEffect { get; set; }
        public bool ResetDuringDeviceRevoke { get; set; }
        public bool ResetDuringDeviceAdmission { get; set; }
        public bool MalformedMapAdmission { get; set; }
        public bool MalformedLegacyMapAdmission { get; set; }
        public PlatformAuthorityStatus? LegacyMapFailureStatus { get; set; }
        public bool FailMappingCleanup { get; set; }
        public bool ResetDuringMappingCleanup { get; set; }
        public bool ThrowDuringDeviceRevoke { get; set; }
        public Action? DeviceRevokeEffect { get; set; }
        public bool MalformedPageFault { get; set; }
        public ManualResetEventSlim? PageFaultEntered { get; set; }
        public ManualResetEventSlim? PageFaultRelease { get; set; }
        public ManualResetEventSlim? CompletionEntered { get; set; }
        public ManualResetEventSlim? CompletionRelease { get; set; }
        public DmaExecutionBindingV1? LastBinding { get; private set; }
        public PlatformProviderIncarnation CurrentIncarnation { get; private set; } = new(1);

        public void AdvanceIncarnation() =>
            CurrentIncarnation = new PlatformProviderIncarnation(CurrentIncarnation.Value + 1);

        public void LoseIncarnation() => CurrentIncarnation = new PlatformProviderIncarnation(0);

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("dma-submit-model"),
            1,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.OwnedRegionMapping,
                PlatformOwnedRegionMappingContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.IoDomainBinding,
                PlatformDeviceLeaseContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.DmaMapping,
                PlatformDmaPageFaultContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(_nextDomain++),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _domains.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            if (_devices.Values.Any(device => device.DomainLease == lease))
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Device authority remains live.");
            return _domains.Remove(lease.LeaseId)
                ? PlatformAuthorityResult.Ok()
                : PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Stale, "Domain lease is unavailable.");
        }

        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(
            PlatformProviderDomainLease domainLease,
            PlatformDeviceIdentity device,
            PlatformDeviceRights rights)
        {
            if (!_domains.TryGetValue(domainLease.LeaseId, out var domain) ||
                domain != domainLease)
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(
                    PlatformAuthorityStatus.WrongDomain, "Wrong domain.");
            var lease = new PlatformProviderDeviceLease(
                new PlatformProviderDeviceLeaseId(_nextDevice++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                device,
                rights);
            _devices.Add(lease.LeaseId, lease);
            if (ResetDuringDeviceAdmission) AdvanceIncarnation();
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            DeviceRevokeCalls++;
            if (_grants.Values.Any(grant => grant.DeviceLease == lease))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "DMA grant remains live.");
            }

            _devices.Remove(lease.LeaseId);
            DeviceRevokeEffect?.Invoke();
            if (ResetDuringDeviceRevoke) AdvanceIncarnation();
            if (ThrowDuringDeviceRevoke)
                throw new InvalidOperationException("Injected device receipt loss.");
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access)
        {
            var mapped = MapOwnedRegionSlice(
                domainLease,
                new PlatformRegionSlice(region, 0, region.ByteLength, access));
            if (mapped.IsSuccess && LegacyMapFailureStatus is { } failureStatus)
                return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                    failureStatus, "Injected failure after possible mapping effect.");
            return mapped.IsSuccess
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(
                    MalformedLegacyMapAdmission
                        ? mapped.Value!.Lease with { Access = PlatformMemoryAccess.None }
                        : mapped.Value!.Lease)
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                    mapped.Status,
                    mapped.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
            PlatformProviderDomainLease domainLease,
            PlatformRegionSlice slice)
        {
            if (!_domains.TryGetValue(domainLease.LeaseId, out var domain) ||
                domain != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Wrong domain.");
            }

            var lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                slice.Region,
                slice.Access);
            var mapped = new PlatformProviderOwnedRegionMapping(lease, slice);
            _mappings.Add(lease.MappingId, mapped);
            if (ResetDuringMapAdmission) AdvanceIncarnation();
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(
                MalformedMapAdmission ? mapped with { Slice = slice with { Length = 1 } } : mapped);
        }

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            MappingRevokeCalls++;
            if (FailMappingCleanup)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                    "Injected legacy mapping cleanup failure.");
            if (ResetDuringMappingCleanup) AdvanceIncarnation();
            if (_grants.Values.Any(grant => grant.MappingLease == mapping))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "DMA grant remains live.");
            }

            _mappings.Remove(mapping.MappingId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformRegionRevocationTicket>
            BeginRegionMappingRevocation(
                PlatformProviderRegionMappingLease mapping,
                PlatformRegionRevocationPolicy policy)
        {
            if (!_mappings.TryGetValue(mapping.MappingId, out var exact) ||
                exact.Lease != mapping ||
                _grants.Values.Any(grant => grant.MappingLease == mapping))
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "The exact mapping is unavailable or still has a DMA grant.");
            var operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++),
                new PlatformOperationGeneration(1),
                mapping.DomainLease);
            _revocations.Add(operation.OperationId, (operation, mapping.MappingId));
            if (ResetDuringMappingBegin) AdvanceIncarnation();
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(new(
                mapping.MappingId, mapping.Generation, operation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            if (!_revocations.Remove(operation.OperationId, out var revocation) ||
                revocation.Operation != operation || !_mappings.Remove(revocation.Mapping))
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Denied, "The exact revocation operation is unavailable.");
            MappingRevokeCalls++;
            MappingObserveEffect?.Invoke();
            if (ResetDuringMappingObserve) AdvanceIncarnation();
            if (ThrowDuringMappingObserve)
                throw new InvalidOperationException("Injected mapping receipt loss.");
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(new(
                operation.OperationId, operation.Generation, operation.DomainLease,
                PlatformCompletionState.Closed));
        }

        public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(
            PlatformDmaGrantRequest request)
        {
            var validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    validation.Status,
                    validation.Message!);
            }

            if (!_devices.ContainsKey(request.DeviceLease.LeaseId) ||
                !_mappings.TryGetValue(request.MappingLease.MappingId, out var mapped) ||
                mapped.Slice != request.MappingSlice)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown exact device or mapping authority.");
            }

            var grant = new PlatformProviderDmaGrant(
                new PlatformProviderDmaGrantId(_nextGrant++),
                new PlatformProviderLeaseGeneration(1),
                request.DeviceLease,
                request.MappingLease,
                request.Range,
                request.Direction);
            _grants.Add(grant.GrantId, grant);
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Ok(grant);
        }

        public PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant)
        {
            DmaRevokeCalls++;
            if (_submissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Submitted DMA operation remains pending.");
            }

            _grants.Remove(grant.GrantId);
            _cycles.Remove(grant.GrantId);
            _acquired.Remove(grant.GrantId);
            RevokeEffect?.Invoke();
            if (ResetDuringRevoke) AdvanceIncarnation();
            if (ThrowDuringRevoke)
                throw new InvalidOperationException("Injected revoke receipt loss.");
            if (RevokedDuringRevoke)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Revoked,
                    "The provider reports an already revoked grant.");
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> PrepareDmaGrantVisibility(
            PlatformProviderDmaGrant grant)
        {
            PrepareCalls++;
            if (!_grants.TryGetValue(grant.GrantId, out var exact) || exact != grant)
            {
                return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown grant.");
            }

            if (_submissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Submitted operation remains pending.");
            }

            var cycle = new PlatformProviderDmaVisibilityCycle(_nextCycle++);
            _cycles[grant.GrantId] = cycle;
            _acquired.Remove(grant.GrantId);
            return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Ok(
                new PlatformProviderDmaPrepareEvidence(
                    grant.GrantId,
                    grant.Generation,
                    cycle,
                    grant.Direction,
                    PlatformMemoryVisibilityRequirement.PublicationFence,
                    PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));
        }

        public PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence> AcquireDmaGrantVisibility(
            PlatformProviderDmaGrant grant)
        {
            AcquireCalls++;
            if (_submissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Completion has not been proven.");
            }

            if (grant.Direction == PlatformDmaDirection.DeviceReadsMemory)
            {
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Read-only DMA does not require acquire.");
            }

            if (!_grants.TryGetValue(grant.GrantId, out var exact) || exact != grant ||
                !_cycles.TryGetValue(grant.GrantId, out var cycle) ||
                _acquired.Contains(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "No exact unacquired prepared cycle.");
            }

            _acquired.Add(grant.GrantId);
            if (ResetDuringAcquire) AdvanceIncarnation();
            return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Ok(
                new PlatformProviderDmaAcquireEvidence(
                    grant.GrantId,
                    grant.Generation,
                    cycle,
                    grant.Direction,
                    PlatformMemoryAcquireRequirement.AcquisitionFence,
                    PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied));
        }

        public PlatformAuthorityResult<PlatformProviderDmaSubmission> SubmitDma(
            PlatformProviderDmaSubmitRequest request)
        {
            SubmitCalls++;
            var requestValidation = PlatformDmaSubmissionContract.ValidateRequest(request);
            if (!requestValidation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    requestValidation.Status,
                    requestValidation.Message!);
            }

            var grant = request.Grant;
            if (!_grants.TryGetValue(grant.GrantId, out var exact) || exact != grant ||
                !_cycles.TryGetValue(grant.GrantId, out var cycle) ||
                cycle != request.PreparedCycle ||
                _acquired.Contains(grant.GrantId) ||
                _submissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Submission does not match the exact prepared unacquired grant cycle.");
            }

            if (SubmitStatus is { } status)
            {
                if (status == PlatformAuthorityStatus.Success)
                    throw new InvalidOperationException("Use null to model successful submit.");

                if (status == PlatformAuthorityStatus.Faulted)
                    _submissions[grant.GrantId] = CreateSubmission(request);

                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    status,
                    "Injected DMA submit result.");
            }

            var submission = CreateSubmission(request);
            _submissions.Add(grant.GrantId, submission);
            SubmitEffect?.Invoke();
            if (ResetDuringSubmit)
                AdvanceIncarnation();
            if (MalformedSubmit)
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Ok(
                    submission with
                    {
                        PreparedCycle = new PlatformProviderDmaVisibilityCycle(
                            submission.PreparedCycle.Value + 1),
                    });
            }

            return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Ok(submission);
        }

        public PlatformAuthorityResult<PlatformProviderDmaSubmission> SubmitDmaBound(
            PlatformProviderDmaSubmitRequest request,
            DmaExecutionBindingV1 binding)
        {
            BoundSubmitCalls++;
            try { binding.Validate(); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    PlatformAuthorityStatus.Denied, exception.Message);
            }
            if (binding.EffectState != DmaEffectStateV1.Admitted ||
                binding.RegionGeneration != request.Grant.MappingLease.Region.Handle.Generation.Value ||
                binding.ProcessIncarnation != request.Grant.MappingLease.DomainLease.Subject.ProcessGeneration ||
                binding.AddressSpaceGeneration != request.Grant.MappingLease.DomainLease.Generation.Value ||
                binding.TranslationGeneration != request.Grant.MappingLease.Generation.Value ||
                binding.DeviceLeaseGeneration != request.Grant.DeviceLease.Generation.Value ||
                binding.ProviderGeneration != CurrentIncarnation.Value ||
                binding.SessionGeneration != request.Grant.Generation.Value ||
                binding.ExternalOperationGeneration != 1 || binding.AuthorizesDma)
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    PlatformAuthorityStatus.Stale,
                    "The v6 DMA binding does not match the exact provider request generations.");
            LastBinding = binding;
            return SubmitDma(request);
        }

        public PlatformAuthorityResult<PlatformProviderDmaCopySubmission> SubmitDmaCopy(
            PlatformProviderDmaCopySubmitRequest request)
        {
            CopySubmitCalls++;
            var validation = PlatformDmaCopySubmissionContract.ValidateRequest(request);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(
                    validation.Status, validation.Message!);
            if (CopySubmitStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(
                    status, "Injected atomic DMA copy submit result.");
            if (_submissions.ContainsKey(request.Source.Grant.GrantId) ||
                _submissions.ContainsKey(request.Destination.Grant.GrantId))
                return PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(
                    PlatformAuthorityStatus.NotAccepted, "A copy leg is already submitted.");
            var source = CreateSubmission(request.Source);
            var destination = CreateSubmission(request.Destination);
            _submissions.Add(source.GrantId, source);
            _submissions.Add(destination.GrantId, destination);
            CopyEffect?.Invoke();
            if (ResetDuringCopySubmit) AdvanceIncarnation();
            if (MalformedCopySubmit)
                destination = destination with { SubmissionId = source.SubmissionId };
            return PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Ok(
                new(new(1), new(1), source, destination));
        }

        public PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence> ObserveDmaCompletion(
            PlatformProviderDmaSubmission submission)
        {
            CompletionCalls++;
            if (!_submissions.TryGetValue(submission.GrantId, out var exact) || exact != submission ||
                !_grants.TryGetValue(submission.GrantId, out var grant))
                return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Fail(
                    PlatformAuthorityStatus.Stale, "The exact DMA submission is unavailable.");
            CompletionEntered?.Set();
            _ = CompletionRelease?.Wait(TimeSpan.FromSeconds(10));
            var state = KeepWriteCompletionPending &&
                        submission.Direction == PlatformDmaDirection.DeviceWritesMemory
                ? PlatformProviderDmaCompletionState.Pending
                : PlatformProviderDmaCompletionState.Completed;
            if (state == PlatformProviderDmaCompletionState.Completed)
                _submissions.Remove(submission.GrantId);
            return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Ok(new(
                submission.SubmissionId, submission.Generation, grant.GrantId, grant.Generation,
                submission.PreparedCycle, submission.Range, submission.Direction, state));
        }

        private PlatformProviderDmaSubmission CreateSubmission(
            PlatformProviderDmaSubmitRequest request) =>
            new(
                new PlatformProviderDmaSubmissionId(_nextSubmission++),
                new PlatformProviderDmaSubmissionGeneration(1),
                request.Grant.GrantId,
                request.Grant.Generation,
                request.PreparedCycle,
                request.Grant.Range,
                request.Grant.Direction);

        public PlatformAuthorityResult<PlatformProviderDmaPageFaultEvidence> ResolveDmaPageFault(
            PlatformProviderDmaPageFaultRequest request)
        {
            PageFaultCalls++;
            var validation = PlatformDmaPageFaultContract.ValidateRequest(request);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderDmaPageFaultEvidence>.Fail(
                    validation.Status, validation.Message!);
            if (!_submissions.TryGetValue(request.Grant.GrantId, out var submission) ||
                submission != request.Submission ||
                !_grants.TryGetValue(request.Grant.GrantId, out var grant) || grant != request.Grant)
                return PlatformAuthorityResult<PlatformProviderDmaPageFaultEvidence>.Fail(
                    PlatformAuthorityStatus.Stale, "The exact submitted DMA grant is unavailable.");
            PageFaultEntered?.Set();
            _ = PageFaultRelease?.Wait(TimeSpan.FromSeconds(10));
            var evidence = new PlatformProviderDmaPageFaultEvidence(
                submission.SubmissionId, submission.Generation, grant.GrantId, grant.Generation,
                submission.PreparedCycle, request.FaultRange, request.RequestedAccess,
                MalformedPageFault ? request.FaultSequence + 1 : request.FaultSequence,
                PlatformProviderDmaPageFaultOutcome.Resolved);
            PageFaultEffect?.Invoke();
            if (ResetDuringPageFault) AdvanceIncarnation();
            return PlatformAuthorityResult<PlatformProviderDmaPageFaultEvidence>.Ok(evidence);
        }
    }
}
