using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed partial class PlatformDmaSubmissionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void ActualDataMotionCleanupTailConsumesOriginalFactsWithoutResubmission(int fault)
    {
        CrossOwnerCopyScenario s = CreateCrossOwnerCopyScenario(1602, 2520, 1603, 2530);
        PlatformDmaPrepareEvidence sp = s.Kernel.PreparePlatformDmaForDevice(s.SourceSubject, s.Source).Value!;
        PlatformDmaPrepareEvidence dp = s.Kernel.PreparePlatformDmaForDevice(s.DestinationSubject, s.Destination).Value!;
        s.Provider.CopyEffect = () => { Assert.Throws<InvalidOperationException>(() => s.Input.Span.ToArray()); Assert.Throws<InvalidOperationException>(() => s.Output.Span.ToArray()); };
        if (fault is 2 or 3) s.Provider.MappingObserveEffect = () =>
        {
            if (s.Provider.MappingRevokeCalls == (fault == 2 ? 1 : 2))
                s.Provider.OnIncarnationRead = () => throw new InvalidOperationException("Known mapping closure late loss.");
        };
        else s.Provider.RevokeEffect = () =>
        {
            if (s.Provider.DmaRevokeCalls != (fault == 1 ? 2 : 1)) return;
            if (fault == 4) throw new OutOfMemoryException("Unknown grant closure outcome.");
            if (fault == 5) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            else s.Provider.OnIncarnationRead = () => throw new InvalidOperationException("Known grant closure late loss.");
        };
        KernelResult<V6DataMotionExecution<byte>> Move(PlatformDmaPrepareEvidence prepare, PlatformDmaPrepareEvidence destinationPrepare) => s.Kernel.ExecuteV6ProviderDmaDataMotion(
            s.SourceSubject, s.DestinationSubject, s.Input, s.Output, s.Plan,
            s.SourceMapping, s.Source, prepare, s.DestinationMapping, s.Destination, destinationPrepare, () => 3, () => 5);
        KernelResult<V6DataMotionExecution<byte>> first = Move(sp, dp);
        Assert.False(first.IsSuccess);
        Assert.True(s.Input.IsValid); Assert.True(s.Output.IsValid);
        Assert.Equal(1, s.Provider.CopySubmitCalls); Assert.Equal(2, s.Provider.CompletionCalls); Assert.Equal(1, s.Provider.AcquireCalls);
        s.Provider.RevokeEffect = null; s.Provider.MappingObserveEffect = null; s.Provider.OnIncarnationRead = null;
        int grants = s.Provider.DmaRevokeCalls; int mappings = s.Provider.MappingRevokeCalls;
        PlatformDmaPrepareEvidence sourceRetry = fault switch
        {
            6 => sp with { Cycle = new(sp.Cycle.Value + 1) },
            7 => sp with { Direction = PlatformDmaDirection.DeviceWritesMemory },
            8 => sp with { Requirement = (PlatformMemoryVisibilityRequirement)int.MaxValue },
            9 => sp with { Outcome = (PlatformMemoryVisibilityOutcome)int.MaxValue },
            _ => sp
        };
        PlatformDmaPrepareEvidence destinationRetry = fault switch
        {
            10 => dp with { Cycle = new(dp.Cycle.Value + 1) },
            11 => dp with { Direction = PlatformDmaDirection.DeviceReadsMemory },
            12 => dp with { Requirement = (PlatformMemoryVisibilityRequirement)int.MaxValue },
            13 => dp with { Outcome = (PlatformMemoryVisibilityOutcome)int.MaxValue },
            _ => dp
        };
        KernelResult<V6DataMotionExecution<byte>> retry = Move(sourceRetry, destinationRetry);
        Assert.Equal(fault < 4, retry.IsSuccess);
        Assert.Equal(1, s.Provider.CopySubmitCalls); Assert.Equal(2, s.Provider.CompletionCalls); Assert.Equal(1, s.Provider.AcquireCalls);
        if (retry.IsSuccess)
        {
            Assert.False(s.Input.IsValid); Assert.True(s.Output.IsValid);
            Assert.Equal(2, s.Provider.DmaRevokeCalls); Assert.Equal(2, s.Provider.MappingRevokeCalls);
            Assert.False(retry.Value!.Receipt.AuthorizesMovement);
        }
        else
        {
            Assert.True(s.Input.IsValid); Assert.True(s.Output.IsValid);
            Assert.Equal(grants, s.Provider.DmaRevokeCalls); Assert.Equal(mappings, s.Provider.MappingRevokeCalls);
            if (fault >= 6)
            {
                Assert.Equal(KernelError.StaleGeneration, retry.Error);
                KernelResult<V6DataMotionExecution<byte>> original = Move(sp, dp);
                Assert.True(original.IsSuccess, original.Message);
                Assert.False(s.Input.IsValid); Assert.True(s.Output.IsValid);
                Assert.Equal(1, s.Provider.CopySubmitCalls);
                Assert.Equal(2, s.Provider.CompletionCalls); Assert.Equal(1, s.Provider.AcquireCalls);
                Assert.Equal(2, s.Provider.DmaRevokeCalls); Assert.Equal(2, s.Provider.MappingRevokeCalls);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PairPartialProgressRetainsBothLifetimesAndRetriesKnownReceiptWithoutEffects(int fault)
    {
        CrossOwnerCopyScenario s = CreateCrossOwnerCopyScenario(1602, 2520, 1603, 2530);
        PlatformDmaPrepareEvidence sp = s.Kernel.PreparePlatformDmaForDevice(s.SourceSubject, s.Source).Value!;
        PlatformDmaPrepareEvidence dp = s.Kernel.PreparePlatformDmaForDevice(s.DestinationSubject, s.Destination).Value!;
        DmaTraceSink sourceSink = new DmaTraceSink(); DmaTraceSink destinationSink = new DmaTraceSink();
        s.Provider.CopyEffect = () => { Assert.Throws<InvalidOperationException>(() => s.Input.Span.ToArray()); Assert.Throws<InvalidOperationException>(() => s.Output.Span.ToArray()); };
        if (fault == 0) s.Provider.CompletionEffect = () =>
        {
            if (s.Provider.CompletionCalls == 2) s.Provider.OnIncarnationRead = () => throw new InvalidOperationException("Known destination completion late loss.");
        };
        else s.Provider.AcquireEffect = () =>
        {
            if (fault == 1) s.Provider.OnIncarnationRead = () => throw new InvalidOperationException("Known destination acquire late loss.");
            if (fault == 2) throw new OutOfMemoryException("Unknown destination acquire outcome.");
            if (fault == 3) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 4) s.Provider.AdvanceIncarnation();
        };
        KernelResult<V6DataMotionExecution<byte>> Move() => s.Kernel.ExecuteV6ProviderDmaDataMotion(
            s.SourceSubject, s.DestinationSubject, s.Input, s.Output, s.Plan,
            s.SourceMapping, s.Source, sp, s.DestinationMapping, s.Destination, dp,
            () => 3, () => 5, sourceSink, destinationSink);
        KernelResult<V6DataMotionExecution<byte>> first = Move();
        Assert.False(first.IsSuccess);
        Assert.Equal(2, s.Provider.CompletionCalls);
        Assert.True(s.Kernel.PlatformAuthority.HasActiveDmaSubmission(s.Source.GrantId));
        Assert.True(s.Kernel.PlatformAuthority.HasActiveDmaSubmission(s.Destination.GrantId));
        Assert.True(s.Input.IsValid); Assert.True(s.Output.IsValid);
        Assert.False(s.Kernel.RevokePlatformDma(s.SourceSubject, s.Source).IsSuccess);
        Assert.False(s.Kernel.RevokePlatformDma(s.DestinationSubject, s.Destination).IsSuccess);
        Assert.Equal(0, s.Provider.DmaRevokeCalls); Assert.Equal(0, s.Provider.MappingRevokeCalls);
        s.Provider.CompletionEffect = null; s.Provider.AcquireEffect = null; s.Provider.OnIncarnationRead = null;
        if (fault == 1)
        {
            V6PlatformDmaCopyExecution active = s.Kernel.GetActiveV6PlatformDmaCopy(s.SourceSubject, s.Source, s.DestinationSubject, s.Destination).Value!;
            PlatformDmaCompletionEvidence completed = s.Kernel.GetOrObservePlatformDmaCompletion(s.SourceSubject, active.Source.Submission).Value!;
            Assert.Equal(KernelError.PlatformDenied, s.Kernel.FinalizePlatformDmaPostCompletionVisibility(
                s.SourceSubject, active.Source.Submission, completed).Error);
            PlatformDomainIdentity subject = new PlatformDomainIdentity(s.Source.DeviceLease.DomainBinding.Subject.DomainId, s.SourceSubject);
            Assert.Equal(KernelError.StaleGeneration, s.Kernel.PlatformAuthority.GetProvenDmaCopyVisibility(
                active.Source.Submission, completed with { OperationGeneration = new(2) }, subject).Error);
            Assert.Equal(4, sourceSink.Events.Count); // No replayed Visible event.
        }
        int acquireCalls = s.Provider.AcquireCalls;
        KernelResult<V6DataMotionExecution<byte>> retry = Move();
        Assert.Equal(fault is 0 or 1, retry.IsSuccess);
        Assert.Equal(1, s.Provider.CopySubmitCalls); Assert.Equal(2, s.Provider.CompletionCalls);
        if (fault == 0) Assert.Equal(acquireCalls + 1, s.Provider.AcquireCalls);
        else Assert.Equal(acquireCalls, s.Provider.AcquireCalls);
        if (retry.IsSuccess)
        {
            Assert.False(s.Input.IsValid); Assert.True(s.Output.IsValid);
            Assert.False(s.Kernel.PlatformAuthority.HasActiveDmaSubmission(s.Source.GrantId));
            Assert.False(s.Kernel.PlatformAuthority.HasActiveDmaSubmission(s.Destination.GrantId));
            Assert.Equal(2, s.Provider.DmaRevokeCalls); Assert.Equal(2, s.Provider.MappingRevokeCalls);
            Assert.Equal(4, sourceSink.Events.Count); Assert.Equal(4, destinationSink.Events.Count);
        }
        else
        {
            Assert.True(s.Input.IsValid); Assert.True(s.Output.IsValid);
            Assert.Equal(0, s.Provider.DmaRevokeCalls); Assert.Equal(0, s.Provider.MappingRevokeCalls);
        }
        foreach (DmaTraceSink? sink in new[] { sourceSink, destinationSink }) Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void ActualProviderDataMotionProducesSeparateOwnerTracesThroughRetryOrRefusal(int fault)
    {
        CrossOwnerCopyScenario s = CreateCrossOwnerCopyScenario(1602, 2520, 1603, 2530);
        PlatformDmaPrepareEvidence sp = s.Kernel.PreparePlatformDmaForDevice(s.SourceSubject, s.Source).Value!;
        PlatformDmaPrepareEvidence dp = s.Kernel.PreparePlatformDmaForDevice(s.DestinationSubject, s.Destination).Value!;
        DmaTraceSink sourceSink = new DmaTraceSink(); DmaTraceSink destinationSink = new DmaTraceSink();
        Action outside = () =>
        {
            foreach ((object Target, string Field) tuple in new[] { (Target: (object)s.Kernel, Field: "_platformMemoryUseGate"),
                (Target: (object)s.Kernel.PlatformAuthority, Field: "_secureDomainLifecycleGate"),
                (Target: (object)s.Kernel.PlatformAuthority, Field: "_dsc1Gate"),
                (Target: (object)s.Kernel.PlatformAuthority, Field: "_dmaCompletionGate") })
                Assert.False(Monitor.IsEntered(tuple.Target.GetType().GetField(tuple.Field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tuple.Target)!));
        };
        sourceSink.OnRecord = destinationSink.OnRecord = () =>
        {
            outside();
            if (fault == 10)
            {
                Assert.False(s.Kernel.RevokePlatformDma(s.SourceSubject, s.Source).IsSuccess);
                Assert.False(s.Kernel.RevokePlatformDma(s.DestinationSubject, s.Destination).IsSuccess);
                Assert.False(s.Kernel.SubmitV6PlatformDmaCopy(s.SourceSubject, s.Source, sp,
                    s.DestinationSubject, s.Destination, dp).IsSuccess);
            }
        };
        s.Provider.CopyEffect = () =>
        {
            outside();
            Assert.Throws<InvalidOperationException>(() => s.Input.Span.ToArray()); Assert.Throws<InvalidOperationException>(() => s.Output.Span.ToArray());
            if (fault == 2) Assert.True(s.Kernel.Regions.QuarantineSubrange(s.Destination.Mapping.Region,
                new(s.Destination.DeviceLease.DomainBinding.Subject.DomainId, s.DestinationSubject.Generation),
                new(1, new("copy-model", "bank-0", 1, 1), 1, ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8))).IsSuccess);
            if (fault == 3) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 4) s.Provider.OnIncarnationRead = () => throw new InvalidOperationException("Known pair late source loss.");
            if (fault is 5 or 10) throw new OutOfMemoryException("Unknown pair effect.");
            if (fault == 8) s.Provider.AdvanceIncarnation();
        };
        s.Provider.KeepWriteCompletionPending = fault == 1;
        if (fault == 6) s.Provider.CopySubmitStatus = PlatformAuthorityStatus.NotAccepted;
        if (fault == 7) sourceSink.ThrowOnRecord = true;
        if (fault == 9) s.Provider.MalformedCopySubmit = true;
        KernelResult<V6DataMotionExecution<byte>> Move() => s.Kernel.ExecuteV6ProviderDmaDataMotion(
            s.SourceSubject, s.DestinationSubject, s.Input, s.Output, s.Plan,
            s.SourceMapping, s.Source, sp, s.DestinationMapping, s.Destination, dp,
            () => 3, () => 5, sourceSink, destinationSink);
        KernelResult<V6DataMotionExecution<byte>> result = Move();
        if (fault is 1 or 6)
        {
            Assert.False(result.IsSuccess);
            Assert.True(s.Input.IsValid);
            if (fault == 6) { Assert.Empty(sourceSink.Events); Assert.Empty(destinationSink.Events); }
            else { Assert.Equal(3, sourceSink.Events.Count); Assert.Equal(2, destinationSink.Events.Count); }
            s.Provider.KeepWriteCompletionPending = false; s.Provider.CopySubmitStatus = null;
            result = Move();
        }
        bool success = fault is 0 or 1 or 6 or 7;
        Assert.Equal(success, result.IsSuccess);
        Assert.Equal(fault == 6 ? 2 : 1, s.Provider.CopySubmitCalls);
        foreach (DmaTraceSink? sink in new[] { sourceSink, destinationSink })
        {
            if (fault == 7 && ReferenceEquals(sink, sourceSink))
            {
                Assert.Empty(sink.Events);
                Assert.False(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
                continue; // Sink failure is absent evidence, never qualification success.
            }
            Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
            Assert.Equal(Enumerable.Range(1, sink.Events.Count).Select(i => (ulong)i), sink.Events.Select(e => e.Sequence));
            Assert.All(sink.Events, e => { Assert.False(e.AuthorizesExecution); Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });

            Assert.Equal(success ? 4 : fault is 3 or 8 ? 4 : 3, sink.Events.Count);
            Assert.Equal(SemanticTraceEventKindV1.Submit, sink.Events[0].Kind);
            Assert.Equal(SemanticTraceEventKindV1.EffectPossible, sink.Events[1].Kind);
            Assert.Equal(success ? SemanticTraceEventKindV1.Visible : SemanticTraceEventKindV1.Quarantined, sink.Events[^1].Kind);
        }
        if (fault != 7) Assert.NotEqual(sourceSink.Events[0].OperationCorrelation, destinationSink.Events[0].OperationCorrelation);
        if (success)
        {
            Assert.False(s.Input.IsValid); Assert.True(s.Output.IsValid);
            Assert.Equal(2, s.Provider.DmaRevokeCalls); Assert.Equal(2, s.Provider.MappingRevokeCalls);
            Assert.False(result.Value!.Receipt.AuthorizesMovement);
            if (fault != 7) Assert.NotEqual(sourceSink.Events[0].OperationCorrelation, destinationSink.Events[0].OperationCorrelation);
        }
        else
        {
            Assert.True(s.Input.IsValid); Assert.True(s.Output.IsValid);
            Assert.Equal(0, s.Provider.DmaRevokeCalls); Assert.Equal(0, s.Provider.MappingRevokeCalls);
            Assert.True(s.Kernel.PlatformAuthority.HasFaultPinnedDmaSubmission(s.Source.GrantId));
            Assert.True(s.Kernel.PlatformAuthority.HasFaultPinnedDmaSubmission(s.Destination.GrantId));
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(false, 5)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    public void CachedAndEventEarlySourceTraceIsDeliveredWithoutAuthorityOrDuplicateEffect(bool endpointConsumer, int fault)
    {
        Scenario s = CreateScenario(1615, 2650, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = s.Kernel.PreparePlatformDmaForDevice(s.Subject, s.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        PlatformDmaSubmission submission = s.Kernel.SubmitV6PlatformDma(s.Subject, s.Grant, prepared, sink).Value!.Submission;
        KernelEventEndpoint endpoint = s.Kernel.CreateKernelEventEndpoint(s.Subject).Value!;
        if (!endpointConsumer) Assert.True(s.Kernel.ObservePlatformDmaCompletion(s.Subject, submission).IsSuccess);
        int prefix = sink.Events.Count;
        int completionCalls = s.Provider.CompletionCalls;
        int delivered = 0;
        sink.OnRecord = () =>
        {
            delivered++;
            foreach ((object Target, string Field) tuple in new[] { (Target: (object)s.Kernel, Field: "_platformMemoryUseGate"),
                (Target: (object)s.Kernel.PlatformAuthority, Field: "_secureDomainLifecycleGate"),
                (Target: (object)s.Kernel.PlatformAuthority, Field: "_dmaCompletionGate") })
                Assert.False(Monitor.IsEntered(tuple.Target.GetType().GetField(tuple.Field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tuple.Target)!));
            if (fault == 5)
            {
                Assert.Equal(KernelError.PlatformFaulted, s.Kernel.GetOrObservePlatformDmaCompletion(s.Subject, submission).Error);
                Assert.False(s.Kernel.RevokePlatformDma(s.Subject, s.Grant).IsSuccess);
            }
        };
        if (fault == 4) sink.ThrowOnRecord = true;
        s.Provider.OnIncarnationRead = () =>
        {
            if (fault is 1 or 4 or 5) s.Provider.AdvanceIncarnation();
            if (fault == 2) throw new InvalidOperationException("Early identity source loss.");
            if (fault == 3) s.Provider.LoseIncarnation();
        };
        KernelResult<PlatformDmaCompletionEvidence> result = endpointConsumer ? s.Kernel.ObservePlatformDmaCompletion(s.Subject, submission, endpoint)
            : s.Kernel.GetOrObservePlatformDmaCompletion(s.Subject, submission);
        if (fault == 0)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(completionCalls + (endpointConsumer ? 1 : 0), s.Provider.CompletionCalls);
            Assert.Equal(endpointConsumer ? prefix + 1 : prefix, sink.Events.Count);
            if (endpointConsumer) Assert.True(s.Kernel.ConsumeKernelEvent(s.Subject, endpoint).IsSuccess);
            Assert.True(s.Kernel.FinalizePlatformDmaPostCompletionVisibility(s.Subject, submission, result.Value!).IsSuccess);
            CloseGrantAndMappingForDeviceRevoke(s);
        }
        else
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Equal(completionCalls, s.Provider.CompletionCalls);
            Assert.Equal(KernelError.ResponseNotAvailable, s.Kernel.ConsumeKernelEvent(s.Subject, endpoint).Error);
            Assert.True(s.Kernel.PlatformAuthority.HasFaultPinnedDmaSubmission(s.Grant.GrantId));
            Assert.False(s.Kernel.RevokePlatformDma(s.Subject, s.Grant).IsSuccess);
            if (fault == 4)
            {
                Assert.Equal(prefix, sink.Events.Count);
                sink.ThrowOnRecord = false;
            }
            else
            {
                int suffix = fault is 1 or 5 ? 2 : 1;
                Assert.Equal(suffix, delivered);
                Assert.Equal(prefix + suffix, sink.Events.Count);
                Assert.Equal(SemanticTraceEventKindV1.Quarantined, sink.Events[^1].Kind);
                if (suffix == 2) Assert.Equal(SemanticTraceEventKindV1.GenerationChanged, sink.Events[^2].Kind);
                else Assert.Equal(sink.Events[0].GenerationVectorDigest, sink.Events[^1].GenerationVectorDigest);
            }
            int count = sink.Events.Count;
            Assert.False(s.Kernel.GetOrObservePlatformDmaCompletion(s.Subject, submission).IsSuccess);
            Assert.Equal(count, sink.Events.Count);
            Assert.Equal(completionCalls, s.Provider.CompletionCalls);
            SingProcess process = s.Kernel.Processes.Resolve(s.Subject).Value!;
            Assert.True(s.Kernel.Regions.HasPlatformMappingReservation(s.Mapping.Region, new(process.DomainId, s.Subject.Generation)));
        }
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(Enumerable.Range(1, sink.Events.Count).Select(i => (ulong)i), sink.Events.Select(e => e.Sequence));
        Assert.All(sink.Events, e => { Assert.False(e.AuthorizesExecution); Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void CopySubmitOutsideBothOwnersRetainsExactPairBeforeLateRefusal(int fault)
    {
        CrossOwnerCopyScenario s = CreateCrossOwnerCopyScenario(1602, 2520, 1603, 2530);
        PlatformDmaPrepareEvidence sp = s.Kernel.PreparePlatformDmaForDevice(s.SourceSubject, s.Source).Value!;
        PlatformDmaPrepareEvidence dp = s.Kernel.PreparePlatformDmaForDevice(s.DestinationSubject, s.Destination).Value!;
        KernelResult<V6PlatformDmaCopyExecution> Submit() => s.Kernel.SubmitV6PlatformDmaCopy(s.SourceSubject, s.Source, sp, s.DestinationSubject, s.Destination, dp);
        void Outside()
        {
            foreach ((object Target, string Field) tuple in new[] { (Target: (object)s.Kernel, Field: "_platformMemoryUseGate"),
                (Target: (object)s.Kernel.PlatformAuthority, Field: "_secureDomainLifecycleGate"),
                (Target: (object)s.Kernel.PlatformAuthority, Field: "_dsc1Gate"),
                (Target: (object)s.Kernel.PlatformAuthority, Field: "_dmaCompletionGate") })
                Assert.False(Monitor.IsEntered(tuple.Target.GetType().GetField(tuple.Field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tuple.Target)!));
        }
        CapabilityId sourceCapability = s.Kernel.PlatformAuthority.DeviceEffectSource(s.Source.DeviceLease, s.Source.DeviceLease.DomainBinding.Subject).Value!;
        CapabilityId destinationCapability = s.Kernel.PlatformAuthority.DeviceEffectSource(s.Destination.DeviceLease, s.Destination.DeviceLease.DomainBinding.Subject).Value!;
        bool late = false;
        s.Provider.OnIncarnationRead = Outside;
        s.Provider.CopyEffect = () =>
        {
            Outside();
            Assert.Equal(KernelError.PlatformBindingDraining, Submit().Error);
            bool destination = fault is 2 or 4 or 6;
            PlatformDmaGrant grant = destination ? s.Destination : s.Source;
            ProcessHandle subject = destination ? s.DestinationSubject : s.SourceSubject;
            RegionOwner owner = new RegionOwner(grant.DeviceLease.DomainBinding.Subject.DomainId, subject.Generation);
            if (fault is 1 or 2) Assert.True(s.Kernel.Regions.QuarantineSubrange(grant.Mapping.Region, owner,
                new(1, new("copy-model", "bank-0", 1, 1), 1, ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8))).IsSuccess);
            if (fault is 3 or 4)
            {
                ExternalOperationHandle operation = s.Kernel.ExternalOperations.Prepare(owner, [new(grant.Mapping.Region, RegionUseMode.ExclusiveWrite, new(0, 8))],
                    ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
                Assert.True(s.Kernel.ExternalOperations.AdmitForExactPlatformMappings(operation, new(1, 1)).IsSuccess);
                Assert.True(s.Kernel.ExternalOperations.Cancel(operation, false).IsSuccess);
                Assert.True(s.Kernel.ExternalOperations.Release(operation, new(false, false)).IsSuccess);
            }
            if (fault is 5 or 6) Assert.False(s.Kernel.TerminateProcess(subject).IsSuccess);
            if (fault == 8) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 9) throw new OutOfMemoryException("Unknown copy effect.");
            if (fault == 10) s.Provider.AdvanceIncarnation();
            if (fault is 11 or 12) _ = s.Kernel.RevokeCapability(fault == 11 ? sourceCapability : destinationCapability);
            if (fault == 0)
            {
                Task<(KernelError, KernelError)> concurrent = Task.Run(() => (
                    s.Kernel.RevokePlatformDma(s.SourceSubject, s.Source).Error,
                    s.Kernel.RevokePlatformDma(s.DestinationSubject, s.Destination).Error));
                Assert.True(concurrent.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal((KernelError.PlatformBindingDraining, KernelError.PlatformBindingDraining), concurrent.Result);
            }
            s.Provider.OnIncarnationRead = () =>
            {
                Outside(); late = true;
                IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_activeDmaSubmissions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!;
                foreach ((PlatformDmaGrant, ProcessHandle) leg in new[] { (s.Source, s.SourceSubject), (s.Destination, s.DestinationSubject) })
                {
                    Assert.True(s.Kernel.PlatformAuthority.HasActiveDmaSubmission(leg.Item1.GrantId));
                    object record = records[leg.Item1.GrantId]!;
                    Assert.Equal(new PlatformProviderDmaCopySubmissionId(1), record.GetType().GetProperty("CopySubmissionId")!.GetValue(record));
                    Assert.Equal(new PlatformProviderDmaCopySubmissionGeneration(1), record.GetType().GetProperty("CopyGeneration")!.GetValue(record));
                    PlatformDmaSubmission exact = (PlatformDmaSubmission)record.GetType().GetProperty("Submission")!.GetValue(record)!;
                    Assert.False(s.Kernel.ObservePlatformDmaCompletion(leg.Item2, exact).IsSuccess);
                    Assert.False(s.Kernel.RevokePlatformDma(leg.Item2, leg.Item1).IsSuccess);
                }
                if (fault == 7) throw new InvalidOperationException("Known copy late source loss.");
            };
        };
        KernelResult<V6PlatformDmaCopyExecution> result = Submit();
        Assert.Equal(fault == 0, result.IsSuccess);
        Assert.Equal(fault != 9, late);
        Assert.Equal(1, s.Provider.CopySubmitCalls);
        s.Provider.CopyEffect = null; s.Provider.OnIncarnationRead = null;
        if (result.IsSuccess)
        {
            Assert.Equal(DmaEffectStateV1.EffectPossible, result.Value.Source.Binding.EffectState);
            Assert.Equal(DmaEffectStateV1.EffectPossible, result.Value.Destination.Binding.EffectState);
            Assert.False(result.Value.Source.Binding.AuthorizesDma);
            Assert.False(result.Value.Destination.Binding.AuthorizesDma);
            Assert.True(s.Kernel.CompleteV6PlatformDmaCopy(s.SourceSubject, s.DestinationSubject, result.Value).IsSuccess);
            Assert.True(s.Kernel.RevokePlatformDma(s.SourceSubject, s.Source).IsSuccess);
            Assert.True(s.Kernel.RevokePlatformDma(s.DestinationSubject, s.Destination).IsSuccess);
            Assert.True(s.Kernel.RevokePlatformRegionMapping(s.SourceSubject, s.SourceMapping).IsSuccess);
            Assert.True(s.Kernel.RevokePlatformRegionMapping(s.DestinationSubject, s.DestinationMapping).IsSuccess);
        }
        else foreach ((PlatformDmaGrant, ProcessHandle) leg in new[] { (s.Source, s.SourceSubject), (s.Destination, s.DestinationSubject) })
        {
            Assert.Equal(fault != 9, s.Kernel.PlatformAuthority.HasActiveDmaSubmission(leg.Item1.GrantId));
            Assert.True(s.Kernel.PlatformAuthority.HasFaultPinnedDmaSubmission(leg.Item1.GrantId));
            Assert.False(s.Kernel.RevokePlatformDma(leg.Item2, leg.Item1).IsSuccess);
            Assert.True(s.Kernel.Regions.HasPlatformMappingReservation(leg.Item1.Mapping.Region, new(leg.Item1.DeviceLease.DomainBinding.Subject.DomainId, leg.Item2.Generation)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DirectSubmitRefusalStillObservesLateSourceBeforeFreshRetry(int fault)
    {
        Scenario scenario = CreateScenario(1599, 2490, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Denied;
        bool late = false;
        scenario.Provider.OnIncarnationRead = () => scenario.Provider.OnIncarnationRead = () =>
        {
            late = true;
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge)
                .GetField("_dmaCompletionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!));
            if (fault == 1) scenario.Provider.AdvanceIncarnation();
            if (fault == 2) throw new OutOfMemoryException("Refusal late generation query.");
        };
        KernelResult<PlatformDmaSubmission> result = scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared);
        Assert.True(late);
        Assert.Equal(fault == 0 ? KernelError.PlatformDenied : KernelError.PlatformFaulted, result.Error);
        Assert.False(scenario.Kernel.PlatformAuthority.HasActiveDmaSubmission(scenario.Grant.GrantId));
        Assert.Equal(fault != 0, scenario.Kernel.PlatformAuthority.HasFaultPinnedDmaSubmission(scenario.Grant.GrantId));
        if (fault == 0)
        {
            scenario.Provider.SubmitStatus = null;
            KernelResult<PlatformDmaSubmission> retry = scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared);
            Assert.True(retry.IsSuccess, retry.Message);
            PlatformDmaCompletionEvidence completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, retry.Value!).Value!;
            Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, retry.Value!, completion).IsSuccess);
            CloseGrantAndMappingForDeviceRevoke(scenario);
        }
        else Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void BoundSubmitOutsideOwnersPreservesCanonicalBindingAndSingleTrace(int fault)
    {
        Scenario scenario = CreateScenario(1599, 2490, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        void Outside()
        {
            foreach ((object Target, string Field) tuple in new[] {
                (Target: (object)scenario.Kernel, Field: "_platformMemoryUseGate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_secureDomainLifecycleGate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_dsc1Gate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_dmaCompletionGate") })
                Assert.False(Monitor.IsEntered(tuple.Target.GetType().GetField(tuple.Field,
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tuple.Target)!));
        }
        DmaTraceSink sink = new DmaTraceSink { OnRecord = Outside };
        scenario.Provider.OnIncarnationRead = Outside;
        scenario.Provider.SubmitEffect = () =>
        {
            Outside();
            if (fault == 1) Assert.True(scenario.Kernel.Regions.QuarantineSubrange(scenario.Mapping.Mapping.Region, owner,
                new(1, new("test-provider", "bank-0", 1, 1), 1, ProviderHealthStateV1.Degraded,
                    ProviderFaultClassV1.Omission, new(40, 8))).IsSuccess);
            if (fault == 2) Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            if (fault == 3) _ = scenario.Kernel.RevokeCapability(scenario.DeviceCapability);
            if (fault == 4) scenario.Provider.OnIncarnationRead = () => { Outside(); throw new InvalidOperationException("Known submit late source loss."); };
            if (fault == 5) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 6) throw new OutOfMemoryException("Unknown submit effect.");
            if (fault is 7 or 8)
            {
                Assert.Equal(KernelError.PlatformBindingDraining,
                    scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared).Error);
                Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
                scenario.Provider.OnIncarnationRead = () =>
                {
                    Outside();
                    IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
                        .GetField("_activeDmaSubmissions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
                    object record = records[scenario.Grant.GrantId]!;
                    PlatformDmaSubmission exact = (PlatformDmaSubmission)record.GetType().GetProperty("Submission")!.GetValue(record)!;
                    Assert.Equal(KernelError.PlatformBindingDraining,
                        scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, exact).Error);
                    if (fault == 8) Assert.True(scenario.Kernel.Regions.QuarantineSubrange(scenario.Mapping.Mapping.Region, owner,
                        new(1, new("test-provider", "bank-0", 1, 1), 1, ProviderHealthStateV1.Degraded,
                            ProviderFaultClassV1.Omission, new(40, 8))).IsSuccess);
                };
            }
        };
        KernelResult<V6PlatformDmaExecution> execution = scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, sink);
        KernelResult<PlatformDmaSubmission> result = execution.IsSuccess ? KernelResult<PlatformDmaSubmission>.Ok(execution.Value!.Submission)
            : KernelResult<PlatformDmaSubmission>.Fail(execution.Error, execution.Message!);
        Assert.Equal(1, scenario.Provider.BoundSubmitCalls);
        if (execution.IsSuccess) Assert.Equal(DmaEffectStateV1.EffectPossible, execution.Value!.Binding.EffectState);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(Enumerable.Range(1, sink.Events.Count).Select(i => (ulong)i), sink.Events.Select(e => e.Sequence));
        Assert.Equal(fault is 0 or 7 ? 2 : fault == 5 ? 4 : 3, sink.Events.Count);
        Assert.Equal(fault is 0 or 7, result.IsSuccess);
        scenario.Provider.OnIncarnationRead = null;
        scenario.Provider.SubmitEffect = null;
        Assert.Equal(1, scenario.Provider.SubmitCalls);
        if (result.IsSuccess)
        {
            PlatformDmaCompletionEvidence completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, result.Value!).Value!;
            Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, result.Value!, completion).IsSuccess);
            CloseGrantAndMappingForDeviceRevoke(scenario);
            Assert.False(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region, owner));
        }
        else
        {
            Assert.Equal(fault != 6, scenario.Kernel.PlatformAuthority.HasActiveDmaSubmission(scenario.Grant.GrantId));
            Assert.True(scenario.Kernel.PlatformAuthority.HasFaultPinnedDmaSubmission(scenario.Grant.GrantId));
            Assert.False(scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).IsSuccess);
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region, owner));
            Assert.Equal(1, scenario.Provider.SubmitCalls);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void DirectSubmitOutsideOwnersRefusesLatePublicationAndRetainsOriginalEffect(int fault)
    {
        Scenario scenario = CreateScenario(1599, 2490, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        void Outside()
        {
            foreach ((object Target, string Field) tuple in new[] {
                (Target: (object)scenario.Kernel, Field: "_platformMemoryUseGate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_secureDomainLifecycleGate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_dsc1Gate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_dmaCompletionGate") })
                Assert.False(Monitor.IsEntered(tuple.Target.GetType().GetField(tuple.Field,
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tuple.Target)!));
        }
        scenario.Provider.OnIncarnationRead = Outside;
        scenario.Provider.SubmitEffect = () =>
        {
            Outside();
            if (fault == 1) Assert.True(scenario.Kernel.Regions.QuarantineSubrange(scenario.Mapping.Mapping.Region, owner,
                new(1, new("test-provider", "bank-0", 1, 1), 1, ProviderHealthStateV1.Degraded,
                    ProviderFaultClassV1.Omission, new(40, 8))).IsSuccess);
            if (fault == 2) Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            if (fault == 3) _ = scenario.Kernel.RevokeCapability(scenario.DeviceCapability);
            if (fault == 4) scenario.Provider.OnIncarnationRead = () => { Outside(); throw new InvalidOperationException("Known submit late source loss."); };
            if (fault == 5) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 6) throw new OutOfMemoryException("Unknown submit effect.");
            if (fault == 7)
            {
                Assert.Equal(KernelError.PlatformBindingDraining,
                    scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).Error);
                Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
                scenario.Provider.OnIncarnationRead = () =>
                {
                    Outside();
                    IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
                        .GetField("_activeDmaSubmissions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
                    object record = records[scenario.Grant.GrantId]!;
                    PlatformDmaSubmission exact = (PlatformDmaSubmission)record.GetType().GetProperty("Submission")!.GetValue(record)!;
                    Assert.Equal(KernelError.PlatformBindingDraining,
                        scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, exact).Error);
                };
            }
        };
        KernelResult<PlatformDmaSubmission> result = scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared);
        Assert.Equal(fault is 0 or 7, result.IsSuccess);
        scenario.Provider.OnIncarnationRead = null;
        scenario.Provider.SubmitEffect = null;
        Assert.Equal(1, scenario.Provider.SubmitCalls);
        if (result.IsSuccess)
        {
            PlatformDmaCompletionEvidence completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, result.Value!).Value!;
            Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, result.Value!, completion).IsSuccess);
            CloseGrantAndMappingForDeviceRevoke(scenario);
            Assert.False(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region, owner));
        }
        else
        {
            Assert.Equal(fault != 6, scenario.Kernel.PlatformAuthority.HasActiveDmaSubmission(scenario.Grant.GrantId));
            Assert.True(scenario.Kernel.PlatformAuthority.HasFaultPinnedDmaSubmission(scenario.Grant.GrantId));
            Assert.False(scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).IsSuccess);
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region, owner));
            Assert.Equal(1, scenario.Provider.SubmitCalls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentitySourceQueryDoesNotBlockActualClosureOrPublishRemovedSubmission(bool cached)
    {
        Scenario scenario = CreateScenario(1615, 2650, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        KernelEventEndpoint endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        PlatformDmaCompletionEvidence completed = cached ? scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value! : default;
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        scenario.Provider.OnIncarnationRead = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Task<KernelResult<PlatformDmaCompletionEvidence>> query = Task.Run(() => cached
            ? scenario.Kernel.PlatformAuthority.GetProvenDmaCompletion(submission, new(process.DomainId, scenario.Subject))
            : scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission, endpoint));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task close = Task.Run(() =>
            {
                if (!cached) completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value!;
                Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submission, completed).IsSuccess);
                CloseGrantAndMappingForDeviceRevoke(scenario);
            });
            await close.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(scenario.Kernel.Regions.HasPlatformMappingReservation(
                scenario.Mapping.Mapping.Region, new RegionOwner(process.DomainId, scenario.Subject.Generation)));
        }
        finally { release.Set(); }
        KernelResult<PlatformDmaCompletionEvidence> result = await query.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(KernelError.PlatformBindingNotFound, result.Error);
        Assert.Equal(KernelError.ResponseNotAvailable, scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void CompletionOutsideOwnersRetainsKnownReplyForFreshCleanup(int fault)
    {
        Scenario scenario = CreateScenario(1613, 2630, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        void Outside()
        {
            foreach ((object Target, string Field) tuple in new[] {
                (Target: (object)scenario.Kernel, Field: "_platformMemoryUseGate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_secureDomainLifecycleGate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_dmaCompletionGate") })
                Assert.False(Monitor.IsEntered(tuple.Target.GetType().GetField(tuple.Field,
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tuple.Target)!));
        }
        scenario.Provider.OnIncarnationRead = Outside;
        scenario.Provider.CompletionEffect = () =>
        {
            Outside();
            scenario.Provider.OnIncarnationRead = () =>
            {
                Outside();
                if (fault == 1) throw new InvalidOperationException("Known completion late source query.");
                if (fault == 2) throw new OutOfMemoryException("Known completion late source OOM.");
            };
            if (fault == 3) throw new OutOfMemoryException("Unknown completion effect.");
            if (fault == 4) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 5) Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            if (fault == 6)
            {
                Assert.Equal(KernelError.PlatformBindingDraining,
                    scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Error);
                Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            }
        };
        KernelResult<PlatformDmaCompletionEvidence> result = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.Equal(fault is 0 or 5 or 6, result.IsSuccess);
        scenario.Provider.OnIncarnationRead = null;
        scenario.Provider.CompletionEffect = null;
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeDmaSubmissions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        object record = records[scenario.Grant.GrantId]!;
        Assert.False((bool)record.GetType().GetProperty("CompletionObservationInFlight")!.GetValue(record)!);
        Assert.Equal(fault != 3, record.GetType().GetProperty("KnownCompletionEvidence")!.GetValue(record) is not null);
        if (fault is 3 or 4)
        {
            Assert.False(scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).IsSuccess);
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region, owner));
        }
        else
        {
            if (fault is 1 or 2)
            {
                Assert.False((bool)record.GetType().GetProperty("CompletionProven")!.GetValue(record)!);
                Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
                result = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
                Assert.True(result.IsSuccess, result.Message);
            }
            Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submission, result.Value!).IsSuccess);
            if (fault == 5)
            {
                KernelResult<ProcessTeardownSnapshot> reclaimed = scenario.Kernel.ObserveProcessTeardown(scenario.Subject);
                Assert.True(reclaimed.IsSuccess, reclaimed.Message);
                Assert.True(reclaimed.Value!.LocalReclaimCompleted);
            }
            else CloseGrantAndMappingForDeviceRevoke(scenario);
            Assert.False(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region, owner));
        }
        Assert.Equal(1, scenario.Provider.CompletionCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void PageFaultOutsideOwnersRefusesLatePublicationAndRetainsExactEffect(int fault)
    {
        Scenario scenario = CreateScenario(1611, 2610, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        RegionHandle region = scenario.Mapping.Mapping.Region;
        void AssertOutsideOwners()
        {
            foreach ((object Target, string Field) tuple in new[] {
                (Target: (object)scenario.Kernel, Field: "_platformMemoryUseGate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_secureDomainLifecycleGate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_dmaCompletionGate") })
                Assert.False(Monitor.IsEntered(tuple.Target.GetType().GetField(tuple.Field,
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tuple.Target)!));
        }
        scenario.Provider.OnIncarnationRead = AssertOutsideOwners;
        scenario.Provider.PageFaultEffect = () =>
        {
            AssertOutsideOwners();
            if (fault == 1) Assert.True(scenario.Kernel.Regions.QuarantineSubrange(region, owner,
                new(1, new("test-provider", "bank-0", 1, 1), 1, ProviderHealthStateV1.Degraded,
                    ProviderFaultClassV1.Omission, new(40, 8))).IsSuccess);
            if (fault == 2) Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            if (fault == 3) scenario.Provider.OnIncarnationRead = () => { AssertOutsideOwners(); throw new InvalidOperationException("Known reply late query."); };
            if (fault == 4) throw new OutOfMemoryException("Unknown page fault effect.");
            if (fault == 5) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
        };
        KernelResult<PlatformDmaPageFaultResolutionEvidence> result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Write, 1);
        Assert.Equal(fault == 0, result.IsSuccess);
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeDmaSubmissions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        object record = records[scenario.Grant.GrantId]!;
        Assert.Empty((HashSet<ulong>)record.GetType().GetProperty("PageFaultsInFlight")!.GetValue(record)!);
        Assert.Equal(fault == 0 ? 1 : 0, ((HashSet<ulong>)record.GetType().GetProperty("ResolvedPageFaults")!.GetValue(record)!).Count);
        Assert.Equal(fault == 4 ? 0 : 1, ((global::System.Collections.IDictionary)record.GetType()
            .GetProperty("KnownPageFaultEvidence")!.GetValue(record)!).Count);
        scenario.Provider.PageFaultEffect = null;
        scenario.Provider.OnIncarnationRead = null;
        Assert.False(scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Write, 1).IsSuccess);
        Assert.Equal(1, scenario.Provider.PageFaultCalls);
        if (fault is 4 or 5)
        {
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(region, owner));
            return;
        }
        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.True(completion.IsSuccess, completion.Message);
        Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submission, completion.Value!).IsSuccess);
        if (fault == 2)
        {
            KernelResult<ProcessTeardownSnapshot> teardown = scenario.Kernel.ObserveProcessTeardown(scenario.Subject);
            Assert.True(teardown.IsSuccess, teardown.Message);
            Assert.True(teardown.Value!.LocalReclaimCompleted);
        }
        else
        {
            CloseGrantAndMappingForDeviceRevoke(scenario);
            Assert.False(scenario.Kernel.Regions.HasPlatformMappingReservation(region, owner));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void DeviceAdmissionGenerationGetterPreservesParentAndCapability(int fault)
    {
        Scenario scenario = CreateScenario(1618, 2680, PlatformDmaDirection.DeviceWritesMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).IsSuccess);
        CapabilityId capability = scenario.DeviceCapability;
        CapabilityId revoked = capability;
        if (fault is 8 or 10)
        {
            revoked = Mint(scenario.Kernel, scenario.Subject, ResourceKind.Device, scenario.Device.Device.ResourceId,
                CapabilityRights.Write | CapabilityRights.Configure | CapabilityRights.Delegate);
            capability = scenario.Kernel.DelegateCapability(scenario.Subject, scenario.Subject, revoked,
                CapabilityRights.Write | CapabilityRights.Configure).Value!.CapabilityId;
        }
        int before = scenario.Provider.DeviceBindCalls;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scenario.Kernel.PlatformAuthority)!;
        object domainRecord = domains[scenario.Device.DomainBinding.BindingId]!;
        KernelError? nested = null;
        Action getter = () =>
        {
            scenario.Provider.DeviceBindEffect = null;
            if (fault == 1) nested = scenario.Kernel.RevokePlatformDomain(scenario.Subject, scenario.Device.DomainBinding).Error;
            if (fault == 2) throw new InvalidOperationException("Injected pre-device generation read loss.");
            if (fault == 3) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 9) scenario.Provider.ThrowDuringDeviceRevoke = true;
            if (fault is 4 or 5 or 8 or 9) nested = scenario.Kernel.RevokeCapability(revoked).Error;
            if (fault == 6) nested = scenario.Kernel.BindPlatformDevice(scenario.Subject, scenario.Device.DomainBinding,
                capability, scenario.Device.Rights).Error;
            if (fault == 7) Assert.True(scenario.Kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
        };
        if (fault is 4 or 8 or 9) scenario.Provider.DeviceBindEffect = () => scenario.Provider.OnIncarnationRead = getter;
        else scenario.Provider.OnIncarnationRead = getter;
        KernelResult<PlatformDeviceLease> result = scenario.Kernel.BindPlatformDevice(scenario.Subject, scenario.Device.DomainBinding,
            capability, scenario.Device.Rights);
        Assert.Equal(fault is 2 or 3 or 5 or 7 ? 0 : 1, scenario.Provider.DeviceBindCalls - before);
        if (fault is 0 or 1 or 6 or 10)
        {
            Assert.True(result.IsSuccess, result.Message);
            if (fault == 1) Assert.Equal(KernelError.PlatformBindingActive, nested);
            if (fault == 6) Assert.Equal(KernelError.PlatformBindingDraining, nested);
            if (fault == 10)
            {
                Assert.True(scenario.Kernel.RevokeCapability(revoked).IsSuccess);
                Assert.Equal(KernelError.CapabilityRevoked, scenario.Kernel.PlatformAuthority.ValidateDeviceLease(
                    result.Value!, scenario.Device.DomainBinding.Subject).Error);
            }
            else Assert.True(scenario.Kernel.RevokePlatformDevice(scenario.Subject, result.Value!).IsSuccess);
        }
        else
        {
            Assert.Equal(fault is 2 or 3 ? KernelError.PlatformFaulted : KernelError.CapabilityRevoked, result.Error);
            if (fault is 4 or 5 or 8 or 9) Assert.Equal(KernelError.PlatformBindingDraining, nested);
            if (fault is 4 or 5 or 7 or 8) Assert.True(scenario.Kernel.RevokeCapability(revoked).IsSuccess);
            if (fault is 2 or 3) Assert.False(scenario.Kernel.RevokePlatformDomain(scenario.Subject, scenario.Device.DomainBinding).IsSuccess);
            if (fault == 9)
            {
                Assert.False(scenario.Kernel.RevokeCapability(revoked).IsSuccess);
                Assert.False(scenario.Kernel.RevokePlatformDomain(scenario.Subject, scenario.Device.DomainBinding).IsSuccess);
            }
        }
        HashSet<CapabilityId> pending = (HashSet<CapabilityId>)typeof(RuntimeKernel).GetField("_pendingPlatformDeviceCapabilities",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel)!;
        Assert.Empty(pending);
        Assert.Equal(0, (int)domainRecord.GetType().GetProperty("PendingDeviceBinds")!.GetValue(domainRecord)!);
    }

    [Fact]
    public async Task DeviceAdmissionConcurrentRevokeCommitsBeforeProviderReturns()
    {
        Scenario scenario = CreateScenario(1619, 2690, PlatformDmaDirection.DeviceWritesMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).IsSuccess);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        scenario.Provider.DeviceBindEffect = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test provider release missing.");
        };
        Task<KernelResult<PlatformDeviceLease>> admission = Task.Run(() => scenario.Kernel.BindPlatformDevice(scenario.Subject,
            scenario.Device.DomainBinding, scenario.DeviceCapability, scenario.Device.Rights));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            KernelResult revoked = await Task.Run(() => scenario.Kernel.RevokeCapability(scenario.DeviceCapability))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(KernelError.PlatformBindingDraining, revoked.Error);
            SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
            Assert.Equal(KernelError.CapabilityRevoked, scenario.Kernel.CapabilityAuthority.Validate(
                scenario.DeviceCapability, process.DomainId, scenario.Subject.Generation, CapabilityRights.Configure).Error);
            Assert.Equal(KernelError.PlatformBindingActive, scenario.Kernel.RevokePlatformDomain(
                scenario.Subject, scenario.Device.DomainBinding).Error);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.CapabilityRevoked, (await admission.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        Assert.True(scenario.Kernel.RevokeCapability(scenario.DeviceCapability).IsSuccess);
    }

    [Fact]
    public void DeviceAdmissionIdentityExhaustionRefusesBeforeProviderEffect()
    {
        Scenario scenario = CreateScenario(1620, 2700, PlatformDmaDirection.DeviceWritesMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).IsSuccess);
        typeof(PlatformAuthorityBridge).GetField("_nextDeviceLeaseId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(scenario.Kernel.PlatformAuthority, ulong.MaxValue);
        int before = scenario.Provider.DeviceBindCalls;
        Assert.Equal(KernelError.CapacityExhausted, scenario.Kernel.BindPlatformDevice(scenario.Subject,
            scenario.Device.DomainBinding, scenario.DeviceCapability, scenario.Device.Rights).Error);
        Assert.Equal(before, scenario.Provider.DeviceBindCalls);
        Assert.True(scenario.Kernel.RevokePlatformDomain(scenario.Subject, scenario.Device.DomainBinding).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void DmaGrantAdmissionGenerationGetterPreservesOwnerContinuity(int fault)
    {
        Scenario scenario = CreateScenario(1616, 2660, PlatformDmaDirection.DeviceWritesMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        int before = scenario.Provider.GrantBindCalls;
        KernelError? nested = null;
        Action getter = () =>
        {
            scenario.Provider.GrantBindEffect = null;
            if (fault is 1 or 9) nested = scenario.Kernel.BindPlatformDma(scenario.Subject,
                scenario.Device, scenario.Mapping, 32, 64, PlatformDmaDirection.DeviceWritesMemory).Error;
            if (fault is 2 or 6) nested = scenario.Kernel.RevokeCapability(scenario.DeviceCapability).Error;
            if (fault is 3 or 7) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault is 4 or 5) throw new InvalidOperationException("Injected grant admission generation read loss.");
            if (fault == 8)
            {
                SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
                Assert.True(scenario.Kernel.Regions.QuarantineSubrange(scenario.Mapping.Mapping.Region,
                    new RegionOwner(process.DomainId, scenario.Subject.Generation),
                    new(1, new("test-provider", "bank-0", 1, 1), 1,
                        ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(40, 8))).IsSuccess);
            }
        };
        if (fault is 5 or 6 or 7 or 9) scenario.Provider.GrantBindEffect = () => scenario.Provider.OnIncarnationRead = getter;
        else scenario.Provider.OnIncarnationRead = getter;
        KernelResult<PlatformDmaGrant> result = scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
            32, 64, PlatformDmaDirection.DeviceWritesMemory);
        Assert.Equal(fault is 2 or 3 or 4 or 8 ? 0 : 1, scenario.Provider.GrantBindCalls - before);
        if (fault is 1 or 9) Assert.Equal(KernelError.PlatformBindingActive, nested);
        if (fault is 2 or 6) Assert.Equal(KernelError.PlatformBindingDraining, nested);
        if (fault is 0 or 1 or 9)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, result.Value!).IsSuccess);
        }
        else
        {
            Assert.False(result.IsSuccess);
            Assert.Equal(fault is 2 or 6 ? KernelError.CapabilityRevoked : fault == 8
                ? KernelError.Quarantined : KernelError.PlatformFaulted, result.Error);
            SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region,
                new RegionOwner(process.DomainId, scenario.Subject.Generation)));
            if (fault == 5)
            {
                int beforeCleanup = scenario.Provider.DmaRevokeCalls;
                Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
                Assert.Equal(beforeCleanup, scenario.Provider.DmaRevokeCalls);
                Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            }
            if (fault is 3 or 4 or 7)
                Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
            if (fault is 2 or 6) Assert.True(scenario.Kernel.RevokeCapability(scenario.DeviceCapability).IsSuccess);
        }
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_dmaGrants", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scenario.Kernel.PlatformAuthority)!;
        foreach (object? record in records.Values)
            Assert.False((bool)record.GetType().GetProperty("PreparationInFlight")!.GetValue(record)!);
    }

    [Fact]
    public void DmaGrantAdmissionIdentityExhaustionRefusesBeforeProviderEffect()
    {
        Scenario scenario = CreateScenario(1617, 2670, PlatformDmaDirection.DeviceWritesMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        int before = scenario.Provider.GrantBindCalls;
        typeof(PlatformAuthorityBridge).GetField("_nextDmaGrantId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(scenario.Kernel.PlatformAuthority, ulong.MaxValue);
        KernelResult<PlatformDmaGrant> result = scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
            32, 64, PlatformDmaDirection.DeviceWritesMemory);
        Assert.Equal(KernelError.CapacityExhausted, result.Error);
        Assert.Equal(before, scenario.Provider.GrantBindCalls);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
    }

    public static IEnumerable<object[]> IdentityGetterCases()
    {
        foreach (bool bound in new[] { false, true })
        foreach (bool cached in new[] { false, true })
        for (int fault = 0; fault < 5; fault++) yield return [bound, cached, fault];
    }

    [Theory]
    [MemberData(nameof(IdentityGetterCases))]
    public void DmaIdentityGenerationGetterRevalidatesCachedAndEventConsumers(bool bound, bool cached, int fault)
    {
        Scenario scenario = CreateScenario(1615, 2650, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        PlatformDmaSubmission submission = bound
            ? scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, new DmaTraceSink()).Value!.Submission
            : scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).Value!;
        KernelEventEndpoint endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        PlatformDmaCompletionEvidence completed = cached ? scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value! : default;
        scenario.Provider.OnIncarnationRead = () =>
        {
            foreach ((object Target, string Field) tuple in new[] {
                (Target: (object)scenario.Kernel, Field: "_platformMemoryUseGate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_secureDomainLifecycleGate"),
                (Target: (object)scenario.Kernel.PlatformAuthority, Field: "_dmaCompletionGate") })
                Assert.False(Monitor.IsEntered(tuple.Target.GetType().GetField(tuple.Field,
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tuple.Target)!));
            if (fault == 1) throw new InvalidOperationException("Injected identity generation read loss.");
            if (fault == 2)
            {
                if (!cached) completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value!;
                Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submission, completed).IsSuccess);
            }
            if (fault == 3) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 4) scenario.Provider.AdvanceIncarnation();
        };
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        KernelResult<PlatformDmaCompletionEvidence> result = cached
            ? scenario.Kernel.PlatformAuthority.GetProvenDmaCompletion(submission, new(process.DomainId, scenario.Subject))
            : scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission, endpoint);
        if (fault == 0)
        {
            Assert.True(result.IsSuccess, result.Message);
            if (!cached) Assert.True(scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).IsSuccess);
            Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
                scenario.Subject, submission, result.Value!).IsSuccess);
            CloseGrantAndMappingForDeviceRevoke(scenario);
            Assert.False(scenario.Kernel.Regions.HasPlatformMappingReservation(
                scenario.Mapping.Mapping.Region, new RegionOwner(process.DomainId, scenario.Subject.Generation)));
        }
        else
        {
            Assert.Equal(fault == 2 ? KernelError.PlatformBindingNotFound : KernelError.PlatformFaulted, result.Error);
            Assert.Equal(KernelError.ResponseNotAvailable, scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region,
                new RegionOwner(process.DomainId, scenario.Subject.Generation)));
            if (fault != 2) Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        }
        Assert.Equal(cached || fault is 0 or 2 ? 1 : 0, scenario.Provider.CompletionCalls);
    }

    public static IEnumerable<object[]> PostCompletionGetterCases()
    {
        foreach (bool bound in new[] { false, true })
        foreach (bool reads in new[] { false, true })
        for (int fault = 0; fault < 8; fault++)
            if (!reads || fault is 0 or 1 or 2 or 3 or 6)
                yield return [bound, reads, fault];
    }

    [Theory]
    [MemberData(nameof(PostCompletionGetterCases))]
    public void PostCompletionGenerationGetterPreservesVisibilityLifetime(bool bound, bool reads, int fault)
    {
        Scenario scenario = CreateScenario(1614, 2640, reads
            ? PlatformDmaDirection.DeviceReadsMemory : PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        PlatformDmaSubmission submission = bound
            ? scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, new DmaTraceSink()).Value!.Submission
            : scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).Value!;
        PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value!;
        KernelError? nested = null;
        Action getter = () =>
        {
            if (fault is 1 or 5) nested = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
                scenario.Subject, submission, completed).Error;
            if (fault is 2 or 4) throw new InvalidOperationException("Injected post-completion generation read loss.");
            if (fault is 3 or 7) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 6) nested = scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error;
        };
        if (fault is 4 or 5 or 7) scenario.Provider.AcquireEffect = () => scenario.Provider.OnIncarnationRead = getter;
        else scenario.Provider.OnIncarnationRead = getter;
        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> result = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submission, completed);
        Assert.Equal(!reads && fault is not (2 or 3) ? 1 : 0, scenario.Provider.AcquireCalls);
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeDmaSubmissions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scenario.Kernel.PlatformAuthority)!;
        if (fault is 2 or 3 or 4 or 7)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.True(records.Contains(scenario.Grant.GrantId));
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
            Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
            SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region,
                new RegionOwner(process.DomainId, scenario.Subject.Generation)));
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(result.Value!.IsSatisfied);
            Assert.False(records.Contains(scenario.Grant.GrantId));
            if (fault is 1 or 5 or 6) Assert.Equal(KernelError.PlatformBindingDraining, nested);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        }
        IDictionary grants = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_dmaGrants", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scenario.Kernel.PlatformAuthority)!;
        object grantRecord = grants[scenario.Grant.GrantId]!;
        Assert.False((bool)grantRecord.GetType().GetProperty("AcquisitionInFlight")!.GetValue(grantRecord)!);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    [InlineData(false, 6)]
    [InlineData(true, 6)]
    [InlineData(false, 7)]
    [InlineData(true, 7)]
    public void CompletionGenerationGetterPreservesObservationInterlock(bool bound, int fault)
    {
        Scenario scenario = CreateScenario(1613, 2630, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        PlatformDmaSubmission submission = bound
            ? scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, new DmaTraceSink()).Value!.Submission
            : scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).Value!;
        KernelError? nested = null;
        Action getter = () =>
        {
            if (fault == 1) nested = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Error;
            if (fault is 2 or 4) throw new InvalidOperationException("Injected completion generation read loss.");
            if (fault is 3 or 5) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault is 6 or 7) nested = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject,
                submission, new(40, 8), PlatformMemoryAccess.Write, 1).Error;
        };
        if (fault is 4 or 5 or 7) scenario.Provider.CompletionEffect = () => scenario.Provider.OnIncarnationRead = getter;
        else scenario.Provider.OnIncarnationRead = getter;
        KernelResult<PlatformDmaCompletionEvidence> result = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeDmaSubmissions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scenario.Kernel.PlatformAuthority)!;
        object record = records[scenario.Grant.GrantId]!;
        Assert.False((bool)record.GetType().GetProperty("CompletionObservationInFlight")!.GetValue(record)!);
        Assert.Equal(fault is 2 or 3 ? 0 : 1, scenario.Provider.CompletionCalls);
        Assert.Equal(0, scenario.Provider.PageFaultCalls);
        if (fault is 2 or 3 or 5)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.False((bool)record.GetType().GetProperty("CompletionProven")!.GetValue(record)!);
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
            Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
            SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region,
                new RegionOwner(process.DomainId, scenario.Subject.Generation)));
        }
        else if (fault == 4)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            KernelResult<PlatformDmaCompletionEvidence> retry = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
            Assert.True(retry.IsSuccess, retry.Message);
            Assert.Equal(1, scenario.Provider.CompletionCalls);
            Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submission, retry.Value!).IsSuccess);
            CloseGrantAndMappingForDeviceRevoke(scenario);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            if (fault is 1 or 6 or 7) Assert.Equal(KernelError.PlatformBindingDraining, nested);
            Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submission, result.Value!).IsSuccess);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    [InlineData(false, 6)]
    [InlineData(true, 6)]
    [InlineData(false, 7)]
    [InlineData(true, 7)]
    public void PageFaultGenerationGetterPreservesPendingResolutionAndRegionOwner(bool bound, int fault)
    {
        Scenario scenario = CreateScenario(1612, 2620, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        PlatformDmaSubmission submission = bound
            ? scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, new DmaTraceSink()).Value!.Submission
            : scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).Value!;
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        KernelError? nested = null;
        bool called = false;
        Action getter = () =>
        {
            called = true;
            if (fault == 1) nested = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Error;
            if (fault is 2 or 4) throw new InvalidOperationException("Injected page-fault generation read loss.");
            if (fault is 3 or 5) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 6) Assert.True(scenario.Kernel.Regions.QuarantineSubrange(
                scenario.Mapping.Mapping.Region, owner, new(1, new("test-provider", "bank-0", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(40, 8))).IsSuccess);
            if (fault == 7) nested = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject,
                submission, new(40, 8), PlatformMemoryAccess.Write, 1).Error;
        };
        if (fault is 4 or 5) scenario.Provider.PageFaultEffect = () => scenario.Provider.OnIncarnationRead = getter;
        else scenario.Provider.OnIncarnationRead = getter;
        KernelResult<PlatformDmaPageFaultResolutionEvidence> result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Write, 1);
        Assert.True(called);
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeDmaSubmissions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scenario.Kernel.PlatformAuthority)!;
        object record = records[scenario.Grant.GrantId]!;
        Assert.Empty((HashSet<ulong>)record.GetType().GetProperty("PageFaultsInFlight")!.GetValue(record)!);
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region, owner));
        Assert.Equal(fault is 2 or 3 or 6 ? 0 : 1, scenario.Provider.PageFaultCalls);
        if (fault is 2 or 3 or 4 or 5 or 6)
        {
            Assert.Equal(fault == 6 ? KernelError.Quarantined : KernelError.PlatformFaulted, result.Error);
            Assert.Empty((HashSet<ulong>)record.GetType().GetProperty("ResolvedPageFaults")!.GetValue(record)!);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            if (fault == 1) Assert.Equal(KernelError.PlatformBindingDraining, nested);
            if (fault == 7) Assert.Equal(KernelError.PlatformDenied, nested);
            PlatformDmaCompletionEvidence completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value!;
            Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submission, completion).IsSuccess);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
            Assert.False(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region, owner));
        }
    }

    [Fact]
    public void PageFaultCallbackDamageRetainsEffectUntilExactClosure()
    {
        Scenario scenario = CreateScenario(1611, 2610, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        RegionHandle region = scenario.Mapping.Mapping.Region;
        bool? damage = null;
        scenario.Provider.PageFaultEffect = () => damage = scenario.Kernel.Regions.QuarantineSubrange(region, owner,
            new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(40, 8))).IsSuccess;
        KernelResult<PlatformDmaPageFaultResolutionEvidence> result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Write, 1);
        Assert.True(damage);
        Assert.Equal(KernelError.Quarantined, result.Error);
        Assert.Equal(KernelError.Quarantined, scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submission, new(48, 8), PlatformMemoryAccess.Write, 2).Error);
        Assert.Equal(1, scenario.Provider.PageFaultCalls);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(region, owner));
        PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value!;
        Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject, submission, completed).IsSuccess);
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(region, owner));
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        Assert.False(scenario.Kernel.Regions.HasPlatformMappingReservation(region, owner));
        Assert.Single(scenario.Kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public void DamagedMappingDeniesPageFaultResolutionBeforeProvider()
    {
        Scenario scenario = CreateScenario(1610, 2600, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        RegionHandle region = scenario.Mapping.Mapping.Region;
        Assert.True(scenario.Kernel.Regions.QuarantineSubrange(region, owner,
            new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(40, 8))).IsSuccess);
        Assert.Equal(KernelError.Quarantined, scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submission, new(40, 8), PlatformMemoryAccess.Write, 1).Error);
        Assert.Equal(0, scenario.Provider.PageFaultCalls);
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(region, owner));
        PlatformDmaCompletionEvidence completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value!;
        Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject, submission, completion).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        Assert.Single(scenario.Kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public async Task ParallelDistinctPageFaultsCommitIndependentlyBeforeExactClosure()
    {
        Scenario scenario = CreateScenario(1609, 2590, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        using CountdownEvent entered = new CountdownEvent(2);
        using ManualResetEventSlim release = new ManualResetEventSlim();
        scenario.Provider.PageFaultEffect = () =>
        {
            entered.Signal();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test callback release missing.");
        };
        Task<KernelResult<PlatformDmaPageFaultResolutionEvidence>> first = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Write, 1));
        Task<KernelResult<PlatformDmaPageFaultResolutionEvidence>> second = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(48, 8), PlatformMemoryAccess.Write, 2));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
            Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        }
        finally { release.Set(); }
        KernelResult<PlatformDmaPageFaultResolutionEvidence>[] results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Message));
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeDmaSubmissions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scenario.Kernel.PlatformAuthority)!;
        object record = records[scenario.Grant.GrantId]!;
        Assert.Empty((HashSet<ulong>)record.GetType().GetProperty("PageFaultsInFlight")!.GetValue(record)!);
        Assert.Equal(new ulong[] { 1, 2 }, ((HashSet<ulong>)record.GetType()
            .GetProperty("ResolvedPageFaults")!.GetValue(record)!).Order());
        PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value!;
        Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject, submission, completed).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
    }

    [Fact]
    public async Task ParallelPageFaultReceiptLossPreventsLateResolutionCommit()
    {
        Scenario scenario = CreateScenario(1608, 2580, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        int callbacks = 0;
        scenario.Provider.PageFaultEffect = () =>
        {
            if (Interlocked.Increment(ref callbacks) == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test callback release missing.");
            }
            else throw new InvalidOperationException("Parallel sequence receipt lost.");
        };
        Task<KernelResult<PlatformDmaPageFaultResolutionEvidence>> first = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Write, 1));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            KernelResult<PlatformDmaPageFaultResolutionEvidence> second = await Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
                new(48, 8), PlatformMemoryAccess.Write, 2));
            Assert.Equal(KernelError.PlatformFaulted, second.Error);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.PlatformFaulted, (await first).Error);
        Assert.Equal(2, callbacks);
        Assert.Equal(2, scenario.Provider.PageFaultCalls);
        // Inspect the existing owner only after both resolver tasks have completed.
        IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeDmaSubmissions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scenario.Kernel.PlatformAuthority)!;
        object record = records[scenario.Grant.GrantId]!;
        Assert.Empty((HashSet<ulong>)record.GetType().GetProperty("PageFaultsInFlight")!.GetValue(record)!);
        Assert.Empty((HashSet<ulong>)record.GetType().GetProperty("ResolvedPageFaults")!.GetValue(record)!);
        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region,
            new RegionOwner(process.DomainId, scenario.Subject.Generation)));
    }

    [Fact]
    public void PageFaultCallbackCannotPublishAfterOtherSequenceLosesReceipt()
    {
        Scenario scenario = CreateScenario(1607, 2570, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        KernelError? nestedError = null;
        scenario.Provider.PageFaultEffect = () =>
        {
            scenario.Provider.PageFaultEffect = () => throw new InvalidOperationException("Other sequence receipt lost.");
            nestedError = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
                new(48, 8), PlatformMemoryAccess.Write, 2).Error;
            scenario.Provider.PageFaultEffect = null;
        };
        KernelResult<PlatformDmaPageFaultResolutionEvidence> result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Write, 1);
        Assert.Equal(KernelError.PlatformFaulted, nestedError);
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(2, scenario.Provider.PageFaultCalls);
        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PageFaultCallbackDuplicateAdmissionPreservesExactEffectLifecycle(int fault)
    {
        Scenario scenario = CreateScenario(1606, 2560, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        scenario.Provider.ResetDuringPageFault = fault == 2;
        KernelError? nestedError = null;
        scenario.Provider.PageFaultEffect = () =>
        {
            scenario.Provider.PageFaultEffect = null;
            nestedError = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
                new(40, 8), PlatformMemoryAccess.Write, 1).Error;
            if (fault == 1) throw new InvalidOperationException("Page fault receipt lost.");
        };
        KernelResult<PlatformDmaPageFaultResolutionEvidence> result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Write, 1);
        Assert.Equal(KernelError.PlatformDenied, nestedError);
        Assert.Equal(fault == 0, result.IsSuccess);
        Assert.Equal(1, scenario.Provider.PageFaultCalls);
        if (fault == 0)
        {
            Assert.False(result.Value!.AuthorizesMapping);
            Assert.False(result.Value.AuthorizesMemoryAccess);
            PlatformDmaCompletionEvidence completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value!;
            Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
                scenario.Subject, submission, completion).IsSuccess);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        }
        else
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.False(scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
                new(40, 8), PlatformMemoryAccess.Write, 2).IsSuccess);
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.Equal(1, scenario.Provider.PageFaultCalls);
            Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
            SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region,
                new RegionOwner(process.DomainId, scenario.Subject.Generation)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PostCompletionAcquireCallbackCannotReenterFinalization(int fault)
    {
        Scenario scenario = CreateScenario(1605, 2550, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value!;
        scenario.Provider.ResetDuringAcquire = fault == 2;
        KernelError? nestedError = null;
        scenario.Provider.AcquireEffect = () =>
        {
            scenario.Provider.AcquireEffect = null;
            nestedError = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
                scenario.Subject, submission, completed).Error;
            if (fault == 1) throw new InvalidOperationException("Post-completion acquire receipt lost.");
        };
        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> result = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submission, completed);
        Assert.Equal(KernelError.PlatformBindingDraining, nestedError);
        Assert.Equal(fault == 0, result.IsSuccess);
        Assert.Equal(1, scenario.Provider.AcquireCalls);
        if (fault == 0)
        {
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        }
        else
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Equal(KernelError.PlatformFaulted, scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
                scenario.Subject, submission, completed).Error);
            Assert.NotEqual(KernelError.PlatformBindingDraining,
                scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Error);
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.Equal(1, scenario.Provider.AcquireCalls);
            Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
            SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region,
                new RegionOwner(process.DomainId, scenario.Subject.Generation)));
        }
    }

    [Fact]
    public void LegacyAcquireCallbackCannotReenterGrantAdmissionOrClosure()
    {
        Scenario scenario = CreateScenario(1604, 2540, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.AcquireEffect = () =>
        {
            scenario.Provider.AcquireEffect = null;
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        };
        Assert.True(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(1, scenario.Provider.PrepareCalls);
        Assert.Equal(1, scenario.Provider.AcquireCalls);
        Assert.Equal(0, scenario.Provider.SubmitCalls);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyAcquireReceiptLossCannotPublishVisibilityOrReleaseMapping(bool reset)
    {
        Scenario scenario = CreateScenario(1603, 2530, PlatformDmaDirection.DeviceWritesMemory);
        Assert.True(scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).IsSuccess);
        scenario.Provider.ResetDuringAcquire = reset;
        scenario.Provider.AcquireEffect = () =>
        {
            if (!reset) throw new InvalidOperationException("Acquire receipt lost.");
        };
        KernelResult<PlatformDmaAcquireEvidence> result = scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant);
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.False(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(1, scenario.Provider.AcquireCalls);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region,
            new RegionOwner(process.DomainId, scenario.Subject.Generation)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GrantRevokeCallbackCannotReenterAdmissionOrClosure(int fault)
    {
        Scenario scenario = CreateScenario(1602, 2520, PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.ThrowDuringRevoke = fault == 1;
        scenario.Provider.ResetDuringRevoke = fault == 2;
        scenario.Provider.RevokedDuringRevoke = fault == 3;
        scenario.Provider.RevokeEffect = () =>
        {
            scenario.Provider.RevokeEffect = null;
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        };
        KernelResult closed = scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant);
        Assert.Equal(fault == 0, closed.IsSuccess);
        Assert.Equal(1, scenario.Provider.PrepareCalls);
        Assert.Equal(0, scenario.Provider.SubmitCalls);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        if (fault == 0)
            Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        else
        {
            Assert.Equal(KernelError.PlatformFaulted, closed.Error);
            Assert.NotEqual(KernelError.PlatformBindingDraining,
                scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Error);
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
            SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Mapping.Region,
                new RegionOwner(process.DomainId, scenario.Subject.Generation)));
        }
    }

    [Fact]
    public void CopySubmitCallbackCannotReenterEitherGrantAdmissionOrClosure()
    {
        CopyScenario scenario = CreateCopyScenario(1601, 2510);
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Destination).Value!;
        scenario.Provider.CopyEffect = () =>
        {
            scenario.Provider.CopyEffect = null;
            foreach (PlatformDmaGrant grant in new[] { scenario.Source, scenario.Destination })
            {
                Assert.Equal(KernelError.PlatformBindingDraining,
                    scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, grant).Error);
                Assert.Equal(KernelError.PlatformBindingDraining,
                    scenario.Kernel.RevokePlatformDma(scenario.Subject, grant).Error);
            }
            Assert.Equal(KernelError.PlatformBindingDraining, scenario.Kernel.SubmitV6PlatformDmaCopy(
                scenario.Subject, scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination, destinationPrepare).Error);
        };
        KernelResult<V6PlatformDmaCopyExecution> result = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject, scenario.Source, sourcePrepare,
            scenario.Subject, scenario.Destination, destinationPrepare);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, scenario.Provider.CopySubmitCalls);
        Assert.Equal(2, scenario.Provider.PrepareCalls);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.True(scenario.Kernel.CompleteV6PlatformDmaCopy(scenario.Subject, scenario.Subject, result.Value!).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubmitCallbackCannotReenterGrantAdmissionOrClosure(bool sidecar)
    {
        Scenario scenario = CreateScenario(1600, 2500, PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitEffect = () =>
        {
            scenario.Provider.SubmitEffect = null;
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        };
        PlatformDmaSubmission submission = sidecar
            ? scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, new DmaTraceSink()).Value!.Submission
            : scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).Value!;
        Assert.Equal(1, scenario.Provider.PrepareCalls);
        Assert.Equal(1, scenario.Provider.SubmitCalls);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission).Value!;
        Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submission, completed).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
    }

    [Fact]
    public void PrepareCallbackCannotReenterGrantAdmissionOrClosure()
    {
        Scenario scenario = CreateScenario(1599, 2490, PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaPrepareEvidence old = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.PrepareEffect = () =>
        {
            scenario.Provider.PrepareEffect = null;
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, old).Error);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        };
        KernelResult<PlatformDmaPrepareEvidence> fresh = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.True(fresh.IsSuccess, fresh.Message);
        Assert.Equal(2, scenario.Provider.PrepareCalls);
        Assert.Equal(0, scenario.Provider.SubmitCalls);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
    }

    [Fact]
    public void LostPrepareReceiptReturnsFaultAndRetainsGrantPin()
    {
        Scenario scenario = CreateScenario(1598, 2480, PlatformDmaDirection.DeviceReadsMemory);
        scenario.Provider.PrepareEffect = () => throw new InvalidOperationException("Lost prepare receipt.");
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(1, scenario.Provider.PrepareCalls);
        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
    }

    [Fact]
    public void BackendResetInsidePrepareCallbackCannotPublishLateCycle()
    {
        Scenario scenario = CreateScenario(1597, 2470, PlatformDmaDirection.DeviceReadsMemory);
        scenario.Provider.PrepareEffect = () => Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
        KernelResult<PlatformDmaPrepareEvidence> prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.Equal(KernelError.PlatformFaulted, prepared.Error);
        Assert.Equal(1, scenario.Provider.PrepareCalls);
        Assert.Equal(0, scenario.Provider.SubmitCalls);
        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
    }

    [Theory]
    [InlineData(PlatformDmaDirection.DeviceReadsMemory, false)]
    [InlineData(PlatformDmaDirection.DeviceWritesMemory, false)]
    [InlineData(PlatformDmaDirection.DeviceReadsMemory, true)]
    [InlineData(PlatformDmaDirection.DeviceWritesMemory, true)]
    public void PrepareCallbackDamageCannotAuthorizeSubmit(PlatformDmaDirection direction, bool sidecar)
    {
        Scenario scenario = CreateScenario(1596, 2460, direction);
        KernelResult<PlatformDmaPrepareEvidence> previous = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.True(previous.IsSuccess, previous.Message);
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        RegionHandle region = scenario.Mapping.Mapping.Region;
        scenario.Provider.PrepareEffect = () => Assert.True(scenario.Kernel.Regions.QuarantineSubrange(region, owner,
            new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(96, 8))).IsSuccess);
        KernelResult<PlatformDmaPrepareEvidence> prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.False(prepared.IsSuccess);
        Assert.Equal(KernelError.Quarantined, prepared.Error);
        Assert.Equal(2, scenario.Provider.PrepareCalls);
        KernelError error = sidecar
            ? scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, previous.Value!, new DmaTraceSink()).Error
            : scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, previous.Value!).Error;
        Assert.Equal(KernelError.Quarantined, error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);
        Assert.Equal(0, scenario.Provider.BoundSubmitCalls);
        Assert.False(scenario.Kernel.PlatformAuthority.HasPendingDmaSubmission(scenario.Grant.GrantId));
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(region, owner));
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Single(scenario.Kernel.Regions.SnapshotDamage());
    }

    [Theory]
    [InlineData(PlatformDmaDirection.DeviceReadsMemory)]
    [InlineData(PlatformDmaDirection.DeviceWritesMemory)]
    public void ExistingGrantDamageDeniesPrepareBeforeProvider(PlatformDmaDirection direction)
    {
        Scenario scenario = CreateScenario(1595, 2450, direction);
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        RegionHandle region = scenario.Mapping.Mapping.Region;
        Assert.True(scenario.Kernel.Regions.QuarantineSubrange(region, owner,
            new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(96, 8))).IsSuccess);
        KernelResult<PlatformDmaPrepareEvidence> prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.Equal(KernelError.Quarantined, prepared.Error);
        Assert.Equal(0, scenario.Provider.PrepareCalls);
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(region, owner));
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Single(scenario.Kernel.Regions.SnapshotDamage());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyCallbackDamageRefusesPublicationAndRetainsBothExactEffects(bool damageDestination)
    {
        CopyScenario scenario = CreateCopyScenario(1594, 2440);
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Destination).Value!;
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        RegionHandle region = (damageDestination ? scenario.Destination : scenario.Source).Mapping.Region;
        scenario.Provider.CopyEffect = () => Assert.True(scenario.Kernel.Regions.QuarantineSubrange(region, owner,
            new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8))).IsSuccess);
        KernelResult<V6PlatformDmaCopyExecution> execution = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject, scenario.Source, sourcePrepare,
            scenario.Subject, scenario.Destination, destinationPrepare);
        Assert.Equal(KernelError.Quarantined, execution.Error);
        Assert.True(scenario.Kernel.PlatformAuthority.HasPendingDmaSubmission(scenario.Source.GrantId));
        Assert.True(scenario.Kernel.PlatformAuthority.HasPendingDmaSubmission(scenario.Destination.GrantId));
        Assert.Equal(KernelError.Quarantined, scenario.Kernel.SubmitV6PlatformDmaCopy(
            scenario.Subject, scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination, destinationPrepare).Error);
        Assert.Equal(1, scenario.Provider.CopySubmitCalls);
        Assert.True(scenario.Kernel.PlatformAuthority.HasFaultPinnedDmaSubmission(scenario.Source.GrantId));
        Assert.True(scenario.Kernel.PlatformAuthority.HasFaultPinnedDmaSubmission(scenario.Destination.GrantId));
        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).IsSuccess);
        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).IsSuccess);
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Source.Mapping.Region, owner));
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Destination.Mapping.Region, owner));
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.Single(scenario.Kernel.Regions.SnapshotDamage());
    }

    [Theory]
    [InlineData(PlatformDmaDirection.DeviceReadsMemory, false)]
    [InlineData(PlatformDmaDirection.DeviceWritesMemory, false)]
    [InlineData(PlatformDmaDirection.DeviceReadsMemory, true)]
    [InlineData(PlatformDmaDirection.DeviceWritesMemory, true)]
    public void CallbackDamageRefusesPublicationAndRetainsAcceptedEffect(PlatformDmaDirection direction, bool sidecar)
    {
        Scenario scenario = CreateScenario(1593, 2430, direction);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        RegionHandle region = scenario.Mapping.Mapping.Region;
        scenario.Provider.SubmitEffect = () => Assert.True(scenario.Kernel.Regions.QuarantineSubrange(region, owner,
            new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(96, 8))).IsSuccess);

        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> execution = sidecar ? scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, sink) : default;
        KernelResult<PlatformDmaSubmission> refused = sidecar
            ? KernelResult<PlatformDmaSubmission>.Fail(execution.Error, execution.Message!)
            : scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared);
        Assert.Equal(KernelError.Quarantined, refused.Error);
        Assert.True(scenario.Kernel.PlatformAuthority.HasPendingDmaSubmission(scenario.Grant.GrantId));
        Assert.True(scenario.Kernel.PlatformAuthority.HasFaultPinnedDmaSubmission(scenario.Grant.GrantId));
        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(region, owner));
        Assert.Single(scenario.Kernel.Regions.SnapshotDamage());
        Assert.Equal(1, scenario.Provider.SubmitCalls);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        if (sidecar)
        {
            Assert.Equal(new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.Quarantined }, sink.Events.Select(e => e.Kind));
            Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        }
        // No exported refused-publication handle/reclaim consumer yet; retain original pins.
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DamagedCopyLegDeniesPairBeforeProvider(bool damageDestination)
    {
        CopyScenario scenario = CreateCopyScenario(1592, 2420);
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Destination).Value!;
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        RegionHandle region = (damageDestination ? scenario.Destination : scenario.Source).Mapping.Region;
        Assert.True(scenario.Kernel.Regions.QuarantineSubrange(region, owner,
            new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8))).IsSuccess);

        KernelResult<V6PlatformDmaCopyExecution> result = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject, scenario.Source, sourcePrepare,
            scenario.Subject, scenario.Destination, destinationPrepare);
        Assert.Equal(KernelError.Quarantined, result.Error);
        Assert.Equal(0, scenario.Provider.CopySubmitCalls);
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Source.Mapping.Region, owner));
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Destination.Mapping.Region, owner));
        Assert.True(scenario.Kernel.PlatformAuthority.ValidateDmaGrant(scenario.Source, scenario.Source.Mapping.Mapping.DomainBinding.Subject).IsSuccess);
        Assert.True(scenario.Kernel.PlatformAuthority.ValidateDmaGrant(scenario.Destination, scenario.Destination.Mapping.Mapping.DomainBinding.Subject).IsSuccess);
    }

    [Theory]
    [InlineData(PlatformDmaDirection.DeviceReadsMemory, false)]
    [InlineData(PlatformDmaDirection.DeviceWritesMemory, false)]
    [InlineData(PlatformDmaDirection.DeviceReadsMemory, true)]
    [InlineData(PlatformDmaDirection.DeviceWritesMemory, true)]
    public void ExistingGrantDamageDeniesSubmitBeforeProvider(PlatformDmaDirection direction, bool sidecar)
    {
        Scenario scenario = CreateScenario(1591, 2410, direction);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        RegionHandle region = scenario.Mapping.Mapping.Region;
        Assert.True(scenario.Kernel.Regions.QuarantineSubrange(region, owner,
            new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(96, 8))).IsSuccess);
        KernelError error = sidecar
            ? scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, new DmaTraceSink()).Error
            : scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).Error;
        Assert.Equal(KernelError.Quarantined, error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);
        Assert.Equal(0, scenario.Provider.BoundSubmitCalls);
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(region, owner));
    }

    [Theory]
    [InlineData(PlatformDmaDirection.DeviceReadsMemory)]
    [InlineData(PlatformDmaDirection.DeviceWritesMemory)]
    public void V6GrantClosureSidecarProjectsExactVisiblePrefixWithoutPublication(PlatformDmaDirection direction)
    {
        Scenario scenario = CreateScenario(1590, 2400, direction);
        DmaTraceSink sink = new DmaTraceSink();
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        V6PlatformDmaExecution submitted = scenario.Kernel.SubmitV6PlatformDma(scenario.Subject,
            scenario.Grant, prepared, sink).Value!;
        Assert.False(scenario.Kernel.QueryV6PlatformDmaGrantClosure(scenario.Subject, scenario.Grant).IsSuccess);
        PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submitted.Submission).Value!;
        Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject, submitted.Submission, completed).IsSuccess);
        Assert.False(scenario.Kernel.QueryV6PlatformDmaGrantClosure(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        DmaGrantClosureObservationV1 observation = scenario.Kernel.QueryV6PlatformDmaGrantClosure(scenario.Subject, scenario.Grant).Value!;
        DmaGrantClosureObservationV1 decoded = DmaGrantClosureObservationV1.ParseCanonical(observation.SerializeCanonical());
        Assert.Equal(observation, decoded);
        string identity = PlatformAuthorityBridge.DmaGrantIdentityDigest(scenario.Grant);
        Assert.True(DmaGrantClosureProjectionV1.Validate(sink.Events, decoded,
            identity, scenario.Provider.CurrentIncarnation.Value, scenario.Kernel.PlatformAuthority.BackendEpoch.Value));
        Assert.False(observation.AuthorizesReclaim);
        Assert.False(observation.AuthorizesEffect);
        Assert.False(observation.ProvesPublication);
        Assert.False(observation.ProvesSettlement);
        Assert.Equal(PlatformExternalClosureState.Active,
            scenario.Kernel.QueryPlatformRegionMappingLifecycle(scenario.Subject, scenario.Mapping.Mapping).Value!.PlatformClosure);
        Assert.Equal(4, sink.Events.Count);
        Assert.Equal(SemanticTraceEventKindV1.Visible, sink.Events[^1].Kind);
        Assert.False(DmaGrantClosureProjectionV1.Validate(sink.Events, observation,
            new string('f', 64), observation.ProviderGeneration, observation.BackendEpoch));
        Assert.False(DmaGrantClosureProjectionV1.Validate(sink.Events, observation,
            identity, observation.ProviderGeneration + 1, observation.BackendEpoch));
        Assert.False(DmaGrantClosureProjectionV1.Validate(sink.Events, observation,
            identity, observation.ProviderGeneration, observation.BackendEpoch + 1));
        Assert.False(DmaGrantClosureProjectionV1.Validate(sink.Events.Take(3), observation,
            identity, observation.ProviderGeneration, observation.BackendEpoch));
        Assert.Equal(observation, scenario.Kernel.QueryV6PlatformDmaGrantClosure(scenario.Subject, scenario.Grant).Value!);
        PlatformDmaGrant stale = scenario.Grant with { Generation = new PlatformDmaGrantGeneration(scenario.Grant.Generation.Value + 1) };
        Assert.Equal(KernelError.StaleGeneration, scenario.Kernel.QueryV6PlatformDmaGrantClosure(scenario.Subject, stale).Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void V6GrantClosureSidecarDoesNotExistAfterAmbiguousRevoke(int fault)
    {
        Scenario scenario = CreateScenario(1591, 2410, PlatformDmaDirection.DeviceWritesMemory);
        DmaTraceSink sink = new DmaTraceSink();
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        V6PlatformDmaExecution submitted = scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, sink).Value!;
        PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submitted.Submission).Value!;
        Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submitted.Submission, completed).IsSuccess);
        scenario.Provider.ResetDuringRevoke = fault == 0;
        scenario.Provider.ThrowDuringRevoke = fault == 1;
        scenario.Provider.RevokedDuringRevoke = fault == 2;
        if (fault == 3) scenario.Provider.RevokeEffect = () => scenario.Kernel.ObservePlatformBackendReset();
        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.False(scenario.Kernel.QueryV6PlatformDmaGrantClosure(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V6GrantClosureSidecarDoesNotReuseAnEarlierVisibleCycle(bool traceLastCycle)
    {
        Scenario scenario = CreateScenario(1593, 2430, PlatformDmaDirection.DeviceWritesMemory);
        DmaTraceSink firstSink = new DmaTraceSink();
        DmaTraceSink lastSink = new DmaTraceSink();
        for (int cycle = 0; cycle < 2; cycle++)
        {
            PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
            V6PlatformDmaExecution submitted = scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant,
                prepared, cycle == 0 ? firstSink : traceLastCycle ? lastSink : null).Value!;
            PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submitted.Submission).Value!;
            Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submitted.Submission, completed).IsSuccess);
        }
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        KernelResult<DmaGrantClosureObservationV1> result = scenario.Kernel.QueryV6PlatformDmaGrantClosure(scenario.Subject, scenario.Grant);
        if (!traceLastCycle)
        {
            Assert.Equal(KernelError.PlatformUnsupported, result.Error);
            return;
        }
        Assert.True(result.IsSuccess, result.Message);
        DmaGrantClosureObservationV1 observation = result.Value!;
        Assert.False(DmaGrantClosureProjectionV1.Validate(firstSink.Events, observation,
            observation.GrantIdentityDigest, observation.ProviderGeneration, observation.BackendEpoch));
        Assert.True(DmaGrantClosureProjectionV1.Validate(lastSink.Events, observation,
            observation.GrantIdentityDigest, observation.ProviderGeneration, observation.BackendEpoch));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V6GrantClosureSidecarGenerationReadFailureKeepsGrantPinned(bool afterCallback)
    {
        Scenario scenario = CreateScenario(1594, 2440, PlatformDmaDirection.DeviceWritesMemory);
        DmaTraceSink sink = new DmaTraceSink();
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        V6PlatformDmaExecution submitted = scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, sink).Value!;
        PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submitted.Submission).Value!;
        Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submitted.Submission, completed).IsSuccess);
        if (afterCallback) scenario.Provider.RevokeEffect = () => scenario.Provider.ThrowIncarnationRead = true;
        else scenario.Provider.ThrowIncarnationRead = true;
        KernelResult result = scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant);
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(afterCallback ? 1 : 0, scenario.Provider.DmaRevokeCalls);
        scenario.Provider.ThrowIncarnationRead = false;
        Assert.False(scenario.Kernel.QueryV6PlatformDmaGrantClosure(scenario.Subject, scenario.Grant).IsSuccess);
        if (afterCallback)
        {
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
            Assert.True(scenario.Kernel.QueryV6PlatformDmaGrantClosure(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            return;
        }
        // No effect-capable Revoke was invoked before the failed source read.
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
    }
    [Fact]
    public void V6GrantClosureSidecarCannotRepairTraceSinkLoss()
    {
        Scenario scenario = CreateScenario(1592, 2420, PlatformDmaDirection.DeviceWritesMemory);
        DmaTraceSink sink = new DmaTraceSink { ThrowOnRecord = true };
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        V6PlatformDmaExecution submitted = scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, sink).Value!;
        PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submitted.Submission).Value!;
        Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, submitted.Submission, completed).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        DmaGrantClosureObservationV1 observation = scenario.Kernel.QueryV6PlatformDmaGrantClosure(scenario.Subject, scenario.Grant).Value!;
        Assert.False(DmaGrantClosureProjectionV1.Validate(sink.Events, observation,
            observation.GrantIdentityDigest, observation.ProviderGeneration, observation.BackendEpoch));
        Assert.Empty(sink.Events);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void V6GrantClosureSidecarGenerationReadReentrancyCannotBypassClosureInterlock(int fault)
    {
        Scenario scenario = CreateScenario(1595, 2450, PlatformDmaDirection.DeviceWritesMemory);
        KernelResult? nested = null;
        bool called = false;
        scenario.Provider.OnIncarnationRead = () =>
        {
            called = true;
            if (fault == 1) nested = scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant);
            if (fault == 2) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
        };
        KernelResult result = scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant);
        Assert.True(called);
        if (fault == 2)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
            Assert.Equal(KernelError.StaleGeneration, scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
            Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            if (fault == 1) Assert.Equal(KernelError.PlatformBindingDraining, nested!.Value.Error);
            Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
            Assert.Equal(KernelError.PlatformBindingRevoked, scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        }
    }
    [Fact]
    public void V6AtomicCopySubmitCreatesBothPendingLifetimesAtOneProviderBoundary()
    {
        CopyScenario scenario = CreateCopyScenario(1520, 1720);
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;

        KernelResult<V6PlatformDmaCopyExecution> result = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
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
        CopyScenario scenario = CreateCopyScenario(1521, 1730);
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        scenario.Provider.CopySubmitStatus = PlatformAuthorityStatus.NotAccepted;

        KernelResult<V6PlatformDmaCopyExecution> denied = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
            scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination, destinationPrepare);
        scenario.Provider.CopySubmitStatus = null;
        KernelResult<V6PlatformDmaCopyExecution> retried = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
            scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination, destinationPrepare);

        Assert.False(denied.IsSuccess);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(2, scenario.Provider.CopySubmitCalls);
    }

    [Fact]
    public void AtomicCopyCompletionClosesBothPendingLifetimesAfterDestinationAcquire()
    {
        CopyScenario scenario = CreateCopyScenario(1523, 1750);
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        V6PlatformDmaCopyExecution execution = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
            scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination,
            destinationPrepare).Value!;

        KernelResult<V6PlatformDmaCopyCompletion> completion = scenario.Kernel.CompleteV6PlatformDmaCopy(
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
        CopyScenario scenario = CreateCopyScenario(1524, 1760);
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        V6PlatformDmaCopyExecution execution = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
            scenario.Source, sourcePrepare, scenario.Subject, scenario.Destination,
            destinationPrepare).Value!;
        scenario.Provider.KeepWriteCompletionPending = true;

        KernelResult<V6PlatformDmaCopyCompletion> completion = scenario.Kernel.CompleteV6PlatformDmaCopy(
            scenario.Subject, scenario.Subject, execution);

        Assert.Equal(KernelError.PlatformBindingDraining, completion.Error);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).Error);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).Error);

        scenario.Provider.KeepWriteCompletionPending = false;
        KernelResult<V6PlatformDmaCopyCompletion> retried = scenario.Kernel.CompleteV6PlatformDmaCopy(
            scenario.Subject, scenario.Subject, execution);

        Assert.True(retried.IsSuccess, retried.Message);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Source).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Destination).IsSuccess);
    }

    [Fact]
    public void ProviderDmaDataMotionClosesAuthorityBeforeSourceReleaseAndCanonicalReceipt()
    {
        CopyScenario scenario = CreateCopyScenario(1525, 1770);
        scenario.Input.Span.Fill(0x6B);
        scenario.Provider.CopyEffect = () => Assert.Throws<InvalidOperationException>(() => scenario.Input.Span.ToArray());
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;

        KernelResult<V6DataMotionExecution<byte>> moved = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
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
        CopyScenario scenario = CreateCopyScenario(1526, 1780);
        scenario.Provider.CopyEffect = () => Assert.Throws<InvalidOperationException>(() => scenario.Input.Span.ToArray());
        scenario.Provider.KeepWriteCompletionPending = true;
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;

        KernelResult<V6DataMotionExecution<byte>> moved = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
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
        KernelResult<V6DataMotionExecution<byte>> resumed = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
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
        CopyScenario scenario = CreateCopyScenario(duringCopySubmit ? 1550UL : 1551UL,
            duringCopySubmit ? 1990UL : 2000UL);
        scenario.Provider.CopyEffect = () => Assert.Throws<InvalidOperationException>(() => scenario.Input.Span.ToArray());
        scenario.Provider.ResetDuringCopySubmit = duringCopySubmit;
        scenario.Provider.ResetDuringRevoke = !duringCopySubmit;
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;

        KernelResult<V6DataMotionExecution<byte>> moved = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
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
        CopyScenario scenario = CreateCopyScenario(1552, 2010);
        scenario.Provider.CopyEffect = () => Assert.Throws<InvalidOperationException>(() => scenario.Input.Span.ToArray());
        scenario.Provider.RevokeEffect = () =>
        {
            if (scenario.Provider.DmaRevokeCalls == 2)
                scenario.Provider.AdvanceIncarnation();
        };
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;

        KernelResult<V6DataMotionExecution<byte>> first = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
            scenario.Subject, scenario.Subject, scenario.Input, scenario.Output, scenario.Plan,
            scenario.SourceMapping, scenario.Source, sourcePrepare,
            scenario.DestinationMapping, scenario.Destination, destinationPrepare,
            () => 3, () => 5);
        KernelResult<V6DataMotionExecution<byte>> repeated = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
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
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        PlatformDomainIdentity identity = new PlatformDomainIdentity(process.DomainId, scenario.Subject);
        KernelResult<PlatformDmaCopyClosureObservation> observation = scenario.Kernel.PlatformAuthority.ObserveDmaCopyClosure(
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
        CrossOwnerCopyScenario scenario = CreateCrossOwnerCopyScenario(1527, 1790, 1528, 1800);
        scenario.Input.Span.Fill(0x3D);
        scenario.Provider.CopyEffect = () => Assert.Throws<InvalidOperationException>(() => scenario.Input.Span.ToArray());
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.SourceSubject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.DestinationSubject, scenario.Destination).Value!;

        KernelResult<V6DataMotionExecution<byte>> moved = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
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
        CopyScenario scenario = CreateCopyScenario(1529, 1810);
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        Assert.True(scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Source, sourcePrepare).IsSuccess);
        Assert.True(scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Destination, destinationPrepare).IsSuccess);

        KernelResult<V6DataMotionExecution<byte>> moved = scenario.Kernel.ExecuteV6ProviderDmaDataMotion(
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
        CopyScenario scenario = CreateCopyScenario(1522, 1740);
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        scenario.Provider.MalformedCopySubmit = malformed;
        scenario.Provider.ResetDuringCopySubmit = reset;
        if (!malformed && !reset)
            scenario.Provider.CopySubmitStatus = PlatformAuthorityStatus.Faulted;

        KernelResult<V6PlatformDmaCopyExecution> result = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
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
        Scenario scenario = CreateScenario(
            1501,
            1510,
            PlatformDmaDirection.DeviceWritesMemory);

        KernelResult<PlatformDmaPrepareEvidence> prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);

        KernelResult<PlatformDmaSubmission> submit = scenario.Kernel.SubmitPlatformDma(
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

        PlatformFeatureDescriptor feature = scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.DmaMapping);
        Assert.Equal(PlatformDmaPageFaultContract.ContractVersion, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, feature.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, feature.Availability);

        Type[] surface = new[]
        {
            typeof(PlatformDmaOperationId),
            typeof(PlatformDmaOperationGeneration),
            typeof(PlatformDmaSubmission),
        };
        string[] forbidden = new[]
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
        foreach (Type? type in surface)
        foreach (MemberInfo member in type.GetMembers(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
                string signature = member.ToString() ?? member.Name;
            foreach (string? term in forbidden)
                Assert.DoesNotContain(term, signature, StringComparison.OrdinalIgnoreCase);
        }

        string[] methods = typeof(RuntimeKernel)
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
        Scenario scenario = CreateScenario(
            1502,
            1520,
            PlatformDmaDirection.DeviceWritesMemory);

        KernelResult<PlatformDmaSubmission> noPrepare = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            default);
        Assert.Equal(KernelError.PlatformDenied, noPrepare.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        KernelResult<PlatformDmaPrepareEvidence> first = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(first.IsSuccess, first.Message);
        KernelResult<PlatformDmaPrepareEvidence> second = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(second.IsSuccess, second.Message);
        Assert.NotEqual(first.Value!.Cycle, second.Value!.Cycle);

        KernelResult<PlatformDmaSubmission> replay = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            first.Value);
        Assert.Equal(KernelError.PlatformDenied, replay.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        KernelResult<PlatformDmaAcquireEvidence> acquire = scenario.Kernel.AcquirePlatformDmaForCpu(
            scenario.Subject,
            scenario.Grant);
        Assert.True(acquire.IsSuccess, acquire.Message);

        KernelResult<PlatformDmaSubmission> consumed = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            second.Value);
        Assert.Equal(KernelError.PlatformDenied, consumed.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        KernelResult<PlatformDmaPrepareEvidence> fresh = scenario.Kernel.PreparePlatformDmaForDevice(
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
        Scenario scenario = CreateScenario(1507, 1570, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.AdvanceIncarnation();

        KernelResult<PlatformDmaSubmission> result = scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepare);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);
    }

    [Fact]
    public void ProviderResetDuringSubmitFaultPinsAmbiguousAcceptance()
    {
        Scenario scenario = CreateScenario(1508, 1580, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.ResetDuringSubmit = true;

        KernelResult<PlatformDmaSubmission> result = scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepare);

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
        Scenario scenario = CreateScenario(1550, 2010, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitEffect = () =>
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);

        KernelError error = bound
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
        CopyScenario scenario = CreateCopyScenario(1551, 2020);
        PlatformDmaPrepareEvidence sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination).Value!;
        scenario.Provider.CopyEffect = () =>
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);

        KernelResult<V6PlatformDmaCopyExecution> result = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject,
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
        Scenario scenario = CreateScenario(
            1503,
            1530,
            PlatformDmaDirection.DeviceReadsMemory);
        KernelResult<PlatformDmaPrepareEvidence> prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);

        PlatformDmaGrant staleGrant = scenario.Grant with
        {
            Generation = new PlatformDmaGrantGeneration(scenario.Grant.Generation.Value + 1),
        };
        KernelResult<PlatformDmaSubmission> stale = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            staleGrant,
            prepare.Value!);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);

        PlatformDmaPrepareEvidence forgedEvidence = prepare.Value! with
        {
            Cycle = new PlatformDmaVisibilityCycle(prepare.Value.Cycle.Value + 999),
        };
        KernelResult<PlatformDmaSubmission> forged = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            forgedEvidence);
        Assert.Equal(KernelError.PlatformDenied, forged.Error);
        Assert.Equal(0, scenario.Provider.SubmitCalls);
    }

    [Fact]
    public void SubmittedCycleBlocksAcquireReprepareSecondSubmitAndClosure()
    {
        Scenario scenario = CreateScenario(
            1504,
            1540,
            PlatformDmaDirection.DeviceWritesMemory);
        KernelResult<PlatformDmaPrepareEvidence> prepare = scenario.Kernel.PreparePlatformDmaForDevice(
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
        Scenario scenario = CreateScenario(
            1505,
            1550,
            PlatformDmaDirection.DeviceReadsMemory);
        KernelResult<PlatformDmaPrepareEvidence> prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        Assert.True(scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!).IsSuccess);

        KernelResult revokeCapability = scenario.Kernel.RevokeCapability(scenario.DeviceCapability);
        Assert.Equal(KernelError.PlatformBindingDraining, revokeCapability.Error);

        KernelResult<PlatformDmaSubmission> submitAgain = scenario.Kernel.SubmitPlatformDma(
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

        KernelResult<PlatformRegionMappingLifecycle> lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject,
            scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Active, lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
    }

    [Fact]
    public void ProcessTeardownDrainsWithoutReclaimWhileSubmissionIsPending()
    {
        Scenario scenario = CreateScenario(
            1506,
            1560,
            PlatformDmaDirection.DeviceWritesMemory);
        KernelResult<PlatformDmaPrepareEvidence> prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            scenario.Grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        Assert.True(scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            prepare.Value!).IsSuccess);

        KernelResult terminate = scenario.Kernel.TerminateProcess(scenario.Subject);
        Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);

        KernelResult<SingProcess> process = scenario.Kernel.Processes.Resolve(scenario.Subject);
        Assert.True(process.IsSuccess, process.Message);
        Assert.Equal(ProcessState.Exiting, process.Value!.State);

        KernelResult<ProcessTeardownSnapshot> teardown = scenario.Kernel.QueryProcessTeardown(scenario.Subject);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformDraining, teardown.Value!.Phase);
        Assert.True(teardown.Value.ChannelsClosed);
        Assert.True(teardown.Value.LocalAuthorizationRevoked);
        Assert.False(teardown.Value.PlatformDomainClosed);
        Assert.False(teardown.Value.LocalReclaimCompleted);
        Assert.True(teardown.Value.PendingPlatformMappings > 0);

        KernelResult<PlatformRegionMappingLifecycle> lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
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
        Scenario malformed = CreateScenario(
            1507,
            1570,
            PlatformDmaDirection.DeviceWritesMemory);
        KernelResult<PlatformDmaPrepareEvidence> malformedPrepare = malformed.Kernel.PreparePlatformDmaForDevice(
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

        Scenario faulted = CreateScenario(
            1508,
            1580,
            PlatformDmaDirection.DeviceReadsMemory);
        KernelResult<PlatformDmaPrepareEvidence> faultedPrepare = faulted.Kernel.PreparePlatformDmaForDevice(
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

        Scenario denied = CreateScenario(
            1509,
            1590,
            PlatformDmaDirection.DeviceReadsMemory);
        KernelResult<PlatformDmaPrepareEvidence> deniedPrepare = denied.Kernel.PreparePlatformDmaForDevice(
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
        Scenario scenario = CreateScenario(1511, 1610, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);

        KernelResult<PlatformDmaPageFaultResolutionEvidence> result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
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
        Scenario scenario = CreateScenario(1512, 1620, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);

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
        Scenario scenario = CreateScenario(1513, 1630, PlatformDmaDirection.Bidirectional);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        PlatformDmaSubmission forged = submission with { Generation = new(submission.Generation.Value + 1) };

        KernelResult<PlatformDmaPageFaultResolutionEvidence> result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, forged,
            new(40, 8), PlatformMemoryAccess.Read | PlatformMemoryAccess.Write, 1);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, scenario.Provider.PageFaultCalls);
    }

    [Fact]
    public void MalformedDmaPageFaultEvidenceFaultPinsExactMapping()
    {
        Scenario scenario = CreateScenario(1514, 1640, PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        scenario.Provider.MalformedPageFault = true;

        KernelResult<PlatformDmaPageFaultResolutionEvidence> result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Read, 1);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, scenario.Provider.PageFaultCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void ProviderResetDuringDmaPageFaultFaultPinsAmbiguousResolution()
    {
        Scenario scenario = CreateScenario(1515, 1650, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        scenario.Provider.ResetDuringPageFault = true;

        KernelResult<PlatformDmaPageFaultResolutionEvidence> result = scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject, submission,
            new(40, 8), PlatformMemoryAccess.Write, 1);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, scenario.Provider.PageFaultCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void ProviderResetAfterSubmitBeforeObservationFaultPinsPendingWrite()
    {
        Scenario scenario = CreateScenario(1532, 1830, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
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
        KernelResult<PlatformRegionMappingLifecycle> lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.False(lifecycle.Value!.LocalReservationReleased);
    }

    [Fact]
    public async Task CompletionWaitsForInFlightPageFaultBeforeProviderObservation()
    {
        Scenario scenario = CreateScenario(1522, 1730, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        scenario.Provider.PageFaultEntered = entered;
        scenario.Provider.PageFaultRelease = release;
        Task<KernelResult<PlatformDmaPageFaultResolutionEvidence>> fault = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(
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
        Scenario scenario = CreateScenario(1523, 1740, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        scenario.Provider.CompletionEntered = entered;
        scenario.Provider.CompletionRelease = release;
        Task<KernelResult<PlatformDmaCompletionEvidence>> completion = Task.Run(() => scenario.Kernel.ObservePlatformDmaCompletion(
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
        Scenario scenario = CreateScenario(1524, 1750, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        scenario.Provider.PageFaultEntered = entered;
        scenario.Provider.PageFaultRelease = release;
        Task<KernelResult<PlatformDmaPageFaultResolutionEvidence>> fault = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(
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
        Scenario scenario = CreateScenario(1525, 1760, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        scenario.Provider.PageFaultEntered = entered;
        scenario.Provider.PageFaultRelease = release;
        Task<KernelResult<PlatformDmaPageFaultResolutionEvidence>> fault = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(
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
        KernelResult<PlatformRegionMappingLifecycle> mapping = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.False(mapping.Value!.LocalReservationReleased);
    }

    [Fact]
    public async Task ProcessTeardownDuringPageFaultKeepsSubmittedEffectPinned()
    {
        Scenario scenario = CreateScenario(1526, 1770, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        scenario.Provider.PageFaultEntered = entered;
        scenario.Provider.PageFaultRelease = release;
        Task<KernelResult<PlatformDmaPageFaultResolutionEvidence>> fault = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submission, new(40, 8), PlatformMemoryAccess.Write, 1));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.TerminateProcess(scenario.Subject).Error);
            KernelResult<ProcessTeardownSnapshot> teardown = scenario.Kernel.QueryProcessTeardown(scenario.Subject);
            Assert.True(teardown.IsSuccess, teardown.Message);
            Assert.Equal(ProcessTeardownPhase.PlatformDraining, teardown.Value!.Phase);
            Assert.False(teardown.Value.LocalReclaimCompleted);
            Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        }
        finally
        {
            release.Set();
        }

        Assert.False((await fault.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        KernelResult<PlatformRegionMappingLifecycle> mapping = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.False(mapping.Value!.LocalReservationReleased);
        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.True(completion.IsSuccess, completion.Message);
        Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject, submission, completion.Value!).IsSuccess);
        KernelResult<ProcessTeardownSnapshot> reclaimed = scenario.Kernel.ObserveProcessTeardown(scenario.Subject);
        Assert.True(reclaimed.IsSuccess, reclaimed.Message);
        Assert.True(reclaimed.Value!.LocalReclaimCompleted);
    }

    [Fact]
    public async Task RegionOwnershipAndMutationCannotCrossPendingPageFault()
    {
        CopyScenario scenario = CreateCopyScenario(1530, 1820);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(scenario.Kernel, 1531, 1821);
        KernelResult<PlatformDmaPrepareEvidence> prepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Destination);
        Assert.True(prepare.IsSuccess, prepare.Message);
        KernelResult<PlatformDmaSubmission> submit = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject, scenario.Destination, prepare.Value!);
        Assert.True(submit.IsSuccess, submit.Message);
        RegionOwner owner = new RegionOwner(new(1820), scenario.Subject.Generation);
        KernelResult<RegionDescriptor> before = scenario.Kernel.Regions.Validate(scenario.Output.Handle, owner);
        Assert.True(before.IsSuccess, before.Message);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        scenario.Provider.PageFaultEntered = entered;
        scenario.Provider.PageFaultRelease = release;
        Task<KernelResult<PlatformDmaPageFaultResolutionEvidence>> fault = Task.Run(() => scenario.Kernel.ResolvePlatformDmaPageFault(
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
            KernelResult<RegionDescriptor> during = scenario.Kernel.Regions.Validate(scenario.Output.Handle, owner);
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

        foreach (PropertyInfo? property in surface.SelectMany(static type => type.GetProperties()))
        foreach (string term in forbidden)
            Assert.DoesNotContain(term, property.Name, StringComparison.OrdinalIgnoreCase);

        PlatformProviderDmaPageFaultEvidence providerEvidence = new PlatformProviderDmaPageFaultEvidence();
        PlatformDmaPageFaultResolutionEvidence runtimeEvidence = new PlatformDmaPageFaultResolutionEvidence();
        Assert.False(providerEvidence.AuthorizesMapping);
        Assert.False(providerEvidence.AuthorizesMemoryAccess);
        Assert.False(runtimeEvidence.AuthorizesMapping);
        Assert.False(runtimeEvidence.AuthorizesMemoryAccess);
    }

    [Fact]
    public void V6BoundSubmitCarriesExactNonAuthoritativeGenerationsToProviderBoundary()
    {
        Scenario scenario = CreateScenario(1516, 1660, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;

        KernelResult<V6PlatformDmaExecution> result = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);

        Assert.True(result.IsSuccess, result.Message);
        DmaExecutionBindingV1 providerBinding = Assert.IsType<DmaExecutionBindingV1>(scenario.Provider.LastBinding);
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
        Scenario scenario = CreateScenario(1533, 1840, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();

        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
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
        Scenario scenario = CreateScenario(1552, 2030, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        if (fault == "backend-reset")
            scenario.Provider.SubmitEffect = () =>
                Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
        else if (fault == "provider-reset") scenario.Provider.ResetDuringSubmit = true;
        else if (fault == "faulted") scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Faulted;
        else scenario.Provider.MalformedSubmit = true;

        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
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
        Scenario scenario = CreateScenario(1553, 2040, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Denied;
        DmaTraceSink sink = new DmaTraceSink();

        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
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
        Scenario scenario = CreateScenario(1556, 2070, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitEffect = scenario.Provider.LoseIncarnation;
        DmaTraceSink sink = new DmaTraceSink();

        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
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
        Scenario scenario = CreateScenario(1554, 2050, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Faulted;
        bool reentered = false;
        DmaTraceSink sink = new DmaTraceSink
        {
            OnRecord = () =>
            {
                reentered = true;
                Assert.Equal(KernelError.PlatformFaulted,
                    scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
            },
        };

        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);

        Assert.Equal(KernelError.PlatformFaulted, submitted.Error);
        Assert.True(reentered);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Fact]
    public void V6AmbiguousSubmitTraceSinkFailureCannotReleaseGrant()
    {
        Scenario scenario = CreateScenario(1555, 2060, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Faulted;
        DmaTraceSink sink = new DmaTraceSink { ThrowOnRecord = true };

        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);

        Assert.Equal(KernelError.PlatformFaulted, submitted.Error);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundSubmitTraceSinkFailureCannotChangeCommittedEffect()
    {
        Scenario scenario = CreateScenario(1534, 1850, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink { ThrowOnRecord = true };

        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(1, scenario.Provider.BoundSubmitCalls);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void V6BoundDmaCompletionTraceFollowsCommittedOwnerOrder()
    {
        Scenario scenario = CreateScenario(1535, 1860, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);

        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
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
        Scenario scenario = CreateScenario(1536, 1870, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
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
        Scenario scenario = CreateScenario(1547, 1980, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        scenario.Provider.MalformedPageFault = true;
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject,
                submitted.Value!.Submission, new(40, 8), PlatformMemoryAccess.Write, 1).Error);
        scenario.Provider.AdvanceIncarnation();

        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value.Submission);
        scenario.Provider.AdvanceIncarnation();
        KernelResult<PlatformDmaCompletionEvidence> laterCompletion = scenario.Kernel.ObservePlatformDmaCompletion(
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
        string providerDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
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
        Scenario scenario = CreateScenario(1538, 1890, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        sink.ThrowOnRecord = true;

        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
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
        Scenario scenario = CreateScenario(1537, 1880, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink
        {
            OnRecord = () => Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error),
        };

        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
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
        Scenario scenario = CreateScenario(1539, 1900, direction);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value!.Submission);
        Assert.True(completion.IsSuccess, completion.Message);

        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> visible = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
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
    public void V6BoundDmaGrantRevokeDoesNotInventTracePublicationOrRelease()
    {
        Scenario scenario = CreateScenario(1560, 2110, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        V6PlatformDmaExecution submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink).Value!;
        PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Submission).Value!;
        Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject, submitted.Submission, completed).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);

        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible],
            sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        SemanticTraceEventV1 last = sink.Events[^1];
        SemanticTraceEventV1 fabricated = last with
        {
            Sequence = last.Sequence + 1,
            Kind = SemanticTraceEventKindV1.Released,
        };
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate(sink.Events.Append(fabricated)).Status);
    }

    [Fact]
    public void V6BoundDmaResetDuringAcquireTraceQuarantinesWithoutVisibility()
    {
        Scenario scenario = CreateScenario(1540, 1910, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value!.Submission);
        Assert.True(completion.IsSuccess, completion.Message);
        scenario.Provider.ResetDuringAcquire = true;

        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> visible = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
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
        Scenario scenario = CreateScenario(1541, 1920, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        bool reentered = false;
        sink.OnRecord = () =>
        {
            if (reentered) return;
            reentered = true;
            KernelResult<PlatformDmaCompletionEvidence> exact = scenario.Kernel.GetOrObservePlatformDmaCompletion(
                scenario.Subject, submitted.Value!.Submission);
            Assert.True(exact.IsSuccess, exact.Message);
            KernelResult<PlatformDmaPostCompletionVisibilityEvidence> visible = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
                scenario.Subject, submitted.Value.Submission, exact.Value!);
            Assert.True(visible.IsSuccess, visible.Message);
        };

        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
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
        Scenario scenario = CreateScenario(1542, 1930, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        scenario.Provider.MalformedPageFault = true;

        KernelResult<PlatformDmaPageFaultResolutionEvidence> resolution = scenario.Kernel.ResolvePlatformDmaPageFault(
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
        Scenario scenario = CreateScenario(1543, 1940, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        scenario.Provider.ResetDuringPageFault = true;

        KernelResult<PlatformDmaPageFaultResolutionEvidence> resolution = scenario.Kernel.ResolvePlatformDmaPageFault(
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
        Scenario scenario = CreateScenario(1552, 2030, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        ulong observedBackendEpoch = 0;
        scenario.Provider.PageFaultEffect = () =>
        {
            KernelResult<PlatformBackendResetSnapshot> reset = scenario.Kernel.ObservePlatformBackendReset();
            Assert.True(reset.IsSuccess, reset.Message);
            observedBackendEpoch = reset.Value!.CurrentEpoch.Value;
            scenario.Provider.AdvanceIncarnation();
        };

        KernelResult<PlatformDmaPageFaultResolutionEvidence> resolution = scenario.Kernel.ResolvePlatformDmaPageFault(
            scenario.Subject, submitted.Value!.Submission, new(40, 8),
            PlatformMemoryAccess.Write, 1);

        Assert.Equal(KernelError.PlatformFaulted, resolution.Error);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined],
            sink.Events.Select(static item => item.Kind));
        string admittedDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            (submitted.Value.Binding with { EffectState = DmaEffectStateV1.Admitted }).SerializeCanonical()));
        string epochDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
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
        Scenario scenario = CreateScenario(1544, 1950, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);

        KernelResult<PlatformBackendResetSnapshot> reset = scenario.Kernel.ObservePlatformBackendReset();

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
        Scenario scenario = CreateScenario(1545, 1960, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);

        KernelResult<PlatformBackendResetSnapshot> first = scenario.Kernel.ObservePlatformBackendReset();
        KernelResult<PlatformBackendResetSnapshot> second = scenario.Kernel.ObservePlatformBackendReset();

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
        string admittedDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            (submitted.Value.Binding with { EffectState = DmaEffectStateV1.Admitted }).SerializeCanonical()));
        string expectedCurrentEpochDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
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
        Scenario scenario = CreateScenario(1548, 1990, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);

        Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
        scenario.Provider.AdvanceIncarnation();
        KernelResult<PlatformBackendResetSnapshot> nextReset = scenario.Kernel.ObservePlatformBackendReset();
        Assert.True(nextReset.IsSuccess, nextReset.Message);
        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submitted.Value!.Submission);

        Assert.False(completion.IsSuccess);
        Assert.Equal(0, scenario.Provider.CompletionCalls);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.GenerationChanged],
            sink.Events.Select(static item => item.Kind));
        string admittedDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            (submitted.Value.Binding with { EffectState = DmaEffectStateV1.Admitted }).SerializeCanonical()));
        string observedEpochDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"dma-owner/backend-epoch/v1|{admittedDigest}|{nextReset.Value!.CurrentEpoch.Value}"))));
        Assert.Equal(observedEpochDigest, sink.Events[4].GenerationVectorDigest);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Fact]
    public void V6BoundDmaBackendResetPreservesLastObservedProviderGeneration()
    {
        Scenario scenario = CreateScenario(1549, 2000, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> submitted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        Assert.True(submitted.IsSuccess, submitted.Message);
        scenario.Provider.MalformedPageFault = true;
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ResolvePlatformDmaPageFault(scenario.Subject,
                submitted.Value!.Submission, new(40, 8), PlatformMemoryAccess.Write, 1).Error);
        scenario.Provider.AdvanceIncarnation();
        ulong observedGeneration = scenario.Provider.CurrentIncarnation.Value;
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject,
                submitted.Value.Submission).Error);

        KernelResult<PlatformBackendResetSnapshot> reset = scenario.Kernel.ObservePlatformBackendReset();
        Assert.True(reset.IsSuccess, reset.Message);
        scenario.Provider.AdvanceIncarnation();
        KernelResult<PlatformBackendResetSnapshot> laterReset = scenario.Kernel.ObservePlatformBackendReset();
        Assert.True(laterReset.IsSuccess, laterReset.Message);

        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.GenerationChanged,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.GenerationChanged],
            sink.Events.Select(static item => item.Kind));
        string providerDigest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            (submitted.Value.Binding with
            {
                EffectState = DmaEffectStateV1.Admitted,
                ProviderGeneration = observedGeneration,
            }).SerializeCanonical()));
        string expected = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"dma-owner/backend-epoch/v1|{providerDigest}|{laterReset.Value!.CurrentEpoch.Value}"))));
        Assert.Equal(expected, sink.Events[^1].GenerationVectorDigest);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(0, scenario.Provider.CompletionCalls);
    }

    [Fact]
    public void V6BoundProviderDenialFaultPinsPreparedCycle()
    {
        Scenario scenario = CreateScenario(1517, 1670, PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Denied;

        KernelResult<V6PlatformDmaExecution> denied = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);
        scenario.Provider.SubmitStatus = null;
        KernelResult<V6PlatformDmaExecution> retried = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);

        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        Assert.Equal(KernelError.PlatformFaulted, retried.Error);
        Assert.Equal(1, scenario.Provider.BoundSubmitCalls);
    }

    [Fact]
    public void V6BoundExplicitNotAcceptedLeavesPreparedCycleRetryable()
    {
        Scenario scenario = CreateScenario(1518, 1680, PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.NotAccepted;
        KernelResult<V6PlatformDmaExecution> notAccepted = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);
        scenario.Provider.SubmitStatus = null;
        KernelResult<V6PlatformDmaExecution> retried = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);
        Assert.False(notAccepted.IsSuccess);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(2, scenario.Provider.BoundSubmitCalls);
    }

    [Fact]
    public void V6BoundTransportUnavailableRetainsPossibleWriteAndBlocksRevoke()
    {
        Scenario scenario = CreateScenario(1519, 1690, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        scenario.Provider.SubmitStatus = PlatformAuthorityStatus.Unavailable;

        KernelResult<V6PlatformDmaExecution> unavailable = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared, sink);
        scenario.Provider.SubmitStatus = null;
        KernelResult<V6PlatformDmaExecution> retried = scenario.Kernel.SubmitV6PlatformDma(
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
        Scenario scenario = CreateScenario(1518, 1680, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject, scenario.Grant).Value!;
        scenario.Provider.ResetDuringSubmit = true;

        KernelResult<V6PlatformDmaExecution> result = scenario.Kernel.SubmitV6PlatformDma(
            scenario.Subject, scenario.Grant, prepared);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, scenario.Provider.BoundSubmitCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
    }

    [Fact]
    public void ProviderResetBeforeGrantRevokeAfterCompletionPinsMappingWithoutCallback()
    {
        Scenario scenario = CreateScenario(1545, 1960, PlatformDmaDirection.DeviceReadsMemory);
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
        Scenario scenario = CreateScenario(1546, 1970, PlatformDmaDirection.DeviceReadsMemory);
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
    public void AlreadyRevokedStatusWithoutResetCannotProveExactGrantClosure()
    {
        Scenario scenario = CreateScenario(1561, 2120, PlatformDmaDirection.DeviceReadsMemory);
        CompleteDmaForGrantRevoke(scenario);
        scenario.Provider.RevokedDuringRevoke = true;

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
        Scenario scenario = CreateScenario(1547, 1980, PlatformDmaDirection.DeviceReadsMemory);
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
        Scenario scenario = CreateScenario(1548, 1990, PlatformDmaDirection.DeviceReadsMemory);
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
        Scenario scenario = CreateScenario(1549, 2000, PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(
            scenario.Subject, scenario.Grant).IsSuccess);
        scenario.Provider.AdvanceIncarnation();

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject, scenario.Mapping).Error);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        KernelResult<PlatformRegionMappingLifecycle> lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
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
        Scenario scenario = CreateScenario(1550, 2010, PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(
            scenario.Subject, scenario.Grant).IsSuccess);
        scenario.Provider.ResetDuringMappingBegin = duringBegin;
        scenario.Provider.ResetDuringMappingObserve = !duringBegin;

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject, scenario.Mapping).Error);
        KernelResult<PlatformRegionMappingLifecycle> lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Faulted,
            lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
    }

    [Fact]
    public void ProviderThrowDuringMappingObservationKeepsReservationPinned()
    {
        Scenario scenario = CreateScenario(1551, 2020, PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(scenario.Kernel.RevokePlatformDma(
            scenario.Subject, scenario.Grant).IsSuccess);
        scenario.Provider.ThrowDuringMappingObserve = true;

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject, scenario.Mapping).Error);
        KernelResult<PlatformRegionMappingLifecycle> lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
            scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Faulted,
            lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
    }

    [Fact]
    public void ProviderResetDuringExactMappingAdmissionReturnsQuarantinedHandle()
    {
        Scenario scenario = CreateScenario(1552, 2030, PlatformDmaDirection.DeviceReadsMemory,
            resetDuringMappingAdmission: true);

        KernelResult<PlatformRegionMappingLifecycle> lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
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
        Scenario scenario = CreateScenario(1558, 2090, PlatformDmaDirection.DeviceReadsMemory);
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
        SubmissionProvider provider = new SubmissionProvider
        {
            MalformedLegacyMapAdmission = true,
            FailMappingCleanup = true,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1561, 2120);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 1562, 2130);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(subject, 64).Value!;
        CapabilityId capability = Mint(kernel, subject, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read);

        KernelResult<PlatformRegionMapping> mapped = kernel.MapPlatformOwnedRegion(subject, binding, capability,
            buffer.Handle, PlatformMemoryAccess.Read);

        Assert.Equal(KernelError.PlatformFaulted, mapped.Error);
        Assert.Equal(1, provider.MappingRevokeCalls);
        Assert.True(kernel.PlatformAuthority.HasActiveMapping(buffer.Handle));
        Assert.False(kernel.TransferRegion(subject, target, buffer).IsSuccess);
    }

    [Fact]
    public void MalformedLegacyMappingCleanupReceiptAfterProviderResetCannotReleaseReservation()
    {
        SubmissionProvider provider = new SubmissionProvider
        {
            MalformedLegacyMapAdmission = true,
            ResetDuringMappingCleanup = true,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1563, 2140);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 1564, 2150);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(subject, 64).Value!;
        CapabilityId capability = Mint(kernel, subject, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read);

        KernelResult<PlatformRegionMapping> mapped = kernel.MapPlatformOwnedRegion(subject, binding, capability,
            buffer.Handle, PlatformMemoryAccess.Read);

        Assert.Equal(KernelError.PlatformFaulted, mapped.Error);
        Assert.Equal(1, provider.MappingRevokeCalls);
        Assert.True(kernel.PlatformAuthority.HasActiveMapping(buffer.Handle));
        Assert.False(kernel.TransferRegion(subject, target, buffer).IsSuccess);
    }

    [Fact]
    public void AmbiguousLegacyMappingFailureWithoutLeaseRetainsRegionReservation()
    {
        SubmissionProvider provider = new SubmissionProvider
        {
            LegacyMapFailureStatus = PlatformAuthorityStatus.Faulted,
        };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 1565, 2160);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 1566, 2170);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(subject, 64).Value!;
        CapabilityId capability = Mint(kernel, subject, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read);

        KernelResult<PlatformRegionMapping> mapped = kernel.MapPlatformOwnedRegion(subject, binding, capability,
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
        Scenario scenario = CreateScenario(1553, 2040, PlatformDmaDirection.DeviceReadsMemory);
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
        Scenario scenario = CreateScenario(1554, 2050, PlatformDmaDirection.DeviceReadsMemory);
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
        Scenario scenario = CreateScenario(1555, 2060, PlatformDmaDirection.DeviceReadsMemory);
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
        Scenario scenario = CreateScenario(1556, 2070, PlatformDmaDirection.DeviceReadsMemory);
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
        Scenario scenario = CreateScenario(1557, 2080, PlatformDmaDirection.DeviceReadsMemory,
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
        Scenario scenario = CreateScenario(1559, 2100, PlatformDmaDirection.DeviceReadsMemory,
            malformedMappingAdmission: true);

        KernelResult<PlatformRegionMappingLifecycle> lifecycle = scenario.Kernel.QueryPlatformRegionMappingLifecycle(
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
        PlatformDmaSubmission submission = PrepareAndSubmit(scenario);
        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject, submission);
        Assert.True(completion.IsSuccess, completion.Message);
        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> visibility = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject, submission, completion.Value!);
        Assert.True(visibility.IsSuccess, visibility.Message);
    }

    private static PlatformDmaSubmission PrepareAndSubmit(Scenario scenario)
    {
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(
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
        SubmissionProvider provider = new SubmissionProvider();
        provider.ResetDuringMapAdmission = resetDuringMappingAdmission;
        provider.ResetDuringDeviceAdmission = resetDuringDeviceAdmission;
        provider.MalformedMapAdmission = malformedMappingAdmission;
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, processId, domainId);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;

        PlatformDeviceRights deviceRights = PlatformDeviceRights.Configure;
        PlatformMemoryAccess mappingAccess = PlatformMemoryAccess.None;
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

        CapabilityId deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma-submit0",
            ToCapabilityRights(deviceRights));
        PlatformDeviceLease device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            deviceRights).Value!;

        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(subject, 512).Value!;
        CapabilityRights memoryRights = CapabilityRights.Map;
        if ((mappingAccess & PlatformMemoryAccess.Read) != 0) memoryRights |= CapabilityRights.Read;
        if ((mappingAccess & PlatformMemoryAccess.Write) != 0) memoryRights |= CapabilityRights.Write;
        CapabilityId memoryCapability = Mint(
            kernel,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            memoryRights);
        PlatformOwnedRegionSliceMapping mapping = kernel.MapPlatformOwnedRegionSlice(
            subject,
            binding,
            memoryCapability,
            buffer.Handle,
            64,
            256,
            mappingAccess).Value!;
        PlatformDmaGrant grant = resetDuringMappingAdmission || resetDuringDeviceAdmission ||
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

    private static CopyScenario CreateCopyScenario(ulong processId, ulong domainId, Action<RuntimeKernel,ProcessHandle>? beforeAllocation = null)
    {
        SubmissionProvider provider = new SubmissionProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, processId, domainId);
        beforeAllocation?.Invoke(kernel,subject);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        const PlatformDeviceRights deviceRights = PlatformDeviceRights.Read |
            PlatformDeviceRights.Write | PlatformDeviceRights.Configure;
        CapabilityId deviceCapability = Mint(kernel, subject, ResourceKind.Device, "device/dma-copy0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
        PlatformDeviceLease device = kernel.BindPlatformDevice(subject, binding, deviceCapability, deviceRights).Value!;

        (OwnedBuffer<byte> Buffer, PlatformOwnedRegionSliceMapping Mapping, PlatformDmaGrant Grant)
            Grant(PlatformMemoryAccess access, PlatformDmaDirection direction)
        {
            OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(subject, 64).Value!;
            CapabilityRights rights = CapabilityRights.Map |
                ((access & PlatformMemoryAccess.Read) != 0 ? CapabilityRights.Read : 0) |
                ((access & PlatformMemoryAccess.Write) != 0 ? CapabilityRights.Write : 0);
            CapabilityId capability = Mint(kernel, subject, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), rights);
            PlatformOwnedRegionSliceMapping mapping = kernel.MapPlatformOwnedRegionSlice(subject, binding, capability,
                buffer.Handle, 0, 64, access).Value!;
            PlatformDmaGrant grant = kernel.BindPlatformDma(subject, device, mapping, 0, 64, direction).Value!;
            return (buffer, mapping, grant);
        }

        (OwnedBuffer<byte> Buffer, PlatformOwnedRegionSliceMapping Mapping, PlatformDmaGrant Grant) source = Grant(PlatformMemoryAccess.Read, PlatformDmaDirection.DeviceReadsMemory);
        (OwnedBuffer<byte> Buffer, PlatformOwnedRegionSliceMapping Mapping, PlatformDmaGrant Grant) destination = Grant(PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);
        RegionDescriptor descriptor = kernel.Regions.Validate(source.Buffer.Handle,
            new RegionOwner(new(domainId), subject.Generation)).Value!;
        DataMotionPlanV1 plan = new DataMotionPlanV1(1, "dma-source", "dma-destination", 3, 5,
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
        SubmissionProvider provider = new SubmissionProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle sourceSubject) = TestFixtures.Create(kernel, sourceProcessId, sourceDomainId);
        (SingProcess _, ProcessHandle destinationSubject) = TestFixtures.Create(
            kernel, destinationProcessId, destinationDomainId);
        PlatformDomainBinding sourceBinding = kernel.BindPlatformDomain(sourceSubject).Value!;
        PlatformDomainBinding destinationBinding = kernel.BindPlatformDomain(destinationSubject).Value!;
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
            CapabilityId deviceCapability = Mint(kernel, subject, ResourceKind.Device, deviceName,
                CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
            PlatformDeviceLease device = kernel.BindPlatformDevice(
                subject, binding, deviceCapability, deviceRights).Value!;
            OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(subject, 64).Value!;
            CapabilityRights rights = CapabilityRights.Map |
                ((access & PlatformMemoryAccess.Read) != 0 ? CapabilityRights.Read : 0) |
                ((access & PlatformMemoryAccess.Write) != 0 ? CapabilityRights.Write : 0);
            CapabilityId capability = Mint(kernel, subject, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), rights);
            PlatformOwnedRegionSliceMapping mapping = kernel.MapPlatformOwnedRegionSlice(
                subject, binding, capability, buffer.Handle, 0, 64, access).Value!;
            PlatformDmaGrant grant = kernel.BindPlatformDma(
                subject, device, mapping, 0, 64, direction).Value!;
            return (buffer, mapping, grant);
        }

        (OwnedBuffer<byte> Buffer, PlatformOwnedRegionSliceMapping Mapping, PlatformDmaGrant Grant) source = Leg(sourceSubject, sourceBinding, "device/dma-copy-source",
            PlatformMemoryAccess.Read, PlatformDmaDirection.DeviceReadsMemory);
        (OwnedBuffer<byte> Buffer, PlatformOwnedRegionSliceMapping Mapping, PlatformDmaGrant Grant) destination = Leg(destinationSubject, destinationBinding,
            "device/dma-copy-destination", PlatformMemoryAccess.Write,
            PlatformDmaDirection.DeviceWritesMemory);
        RegionDescriptor descriptor = kernel.Regions.Validate(source.Buffer.Handle,
            new RegionOwner(new(sourceDomainId), sourceSubject.Generation)).Value!;
        DataMotionPlanV1 plan = new DataMotionPlanV1(1, "dma-source", "dma-destination", 3, 5,
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
        KernelResult<SingProcess> process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        KernelResult<CapabilityDescriptorV1> minted = kernel.MintCapability(
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
        CapabilityRights result = CapabilityRights.None;
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void V6DmaPreparationGenerationGetterCannotLoseInterlockOrContinuity(int fault)
    {
        Scenario scenario = CreateScenario(1596, 2460, PlatformDmaDirection.DeviceWritesMemory);
        KernelResult? nested = null;
        bool called = false;
        Action getter = () =>
        {
            called = true;
            if (fault is 1 or 4) nested = scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant);
            if (fault == 2) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault is 3 or 5) throw new InvalidOperationException("Injected preparation generation read loss.");
        };
        if (fault >= 4) scenario.Provider.PrepareEffect = () => scenario.Provider.OnIncarnationRead = getter;
        else scenario.Provider.OnIncarnationRead = getter;
        KernelResult<PlatformDmaPrepareEvidence> result = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.True(called);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        if (fault is 2 or 3)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Equal(fault == 5 ? 1 : 0, scenario.Provider.PrepareCalls);
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        }
        else if (fault == 5)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Equal(1, scenario.Provider.PrepareCalls);
            Assert.False(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
            Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
            Assert.False(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Region,
                new RegionOwner(scenario.Mapping.Mapping.DomainBinding.Subject.DomainId, scenario.Subject.Generation)));
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(result.Value!.IsSatisfied);
            if (fault is 1 or 4) Assert.Equal(KernelError.PlatformBindingDraining, nested!.Value.Error);
            Assert.Equal(1, scenario.Provider.PrepareCalls);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        }
    }

    [Fact]
    public void V6DmaPreparationGenerationGetterRevokeCannotAuthorizeProviderEffect()
    {
        Scenario scenario = CreateScenario(1597, 2470, PlatformDmaDirection.DeviceWritesMemory);
        bool called = false;
        scenario.Provider.OnIncarnationRead = () =>
        {
            called = true;
            _ = scenario.Kernel.RevokeCapability(scenario.DeviceCapability);
        };
        KernelResult<PlatformDmaPrepareEvidence> result = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.True(called);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, scenario.Provider.PrepareCalls);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void V6DmaAcquireGenerationGetterPreservesExactCycleAndAuthorization(int fault)
    {
        Scenario scenario = CreateScenario(1598, 2480, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        KernelError? nestedError = null;
        bool called = false;
        Action getter = () =>
        {
            called = true;
            if (fault is 1 or 4) nestedError = scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error;
            if (fault == 2) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault is 3 or 5) throw new InvalidOperationException("Injected acquire generation loss.");
            if (fault == 6) _ = scenario.Kernel.RevokeCapability(scenario.DeviceCapability);
            if (fault == 7) nestedError = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Error;
        };
        if (fault is 4 or 5) scenario.Provider.AcquireEffect = () => scenario.Provider.OnIncarnationRead = getter;
        else scenario.Provider.OnIncarnationRead = getter;
        KernelResult<PlatformDmaAcquireEvidence> result = scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant);
        Assert.True(called);
        if (fault is 2 or 3 or 6)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Equal(fault == 5 ? 1 : 0, scenario.Provider.AcquireCalls);
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        }
        else if (fault == 5)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.False(scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.False(scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared).IsSuccess);
            Assert.Equal(1, scenario.Provider.AcquireCalls);
            Assert.Equal(0, scenario.Provider.SubmitCalls);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
            Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
            Assert.False(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Region,
                new RegionOwner(scenario.Mapping.Mapping.DomainBinding.Subject.DomainId, scenario.Subject.Generation)));
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(result.Value!.IsSatisfied);
            Assert.Equal(prepared.Cycle, result.Value.Cycle);
            if (fault is 1 or 4 or 7) Assert.Equal(KernelError.PlatformBindingDraining, nestedError);
            Assert.Equal(1, scenario.Provider.AcquireCalls);
            Assert.Equal(1, scenario.Provider.PrepareCalls);
            Assert.Equal(KernelError.PlatformDenied, scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant).Error);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        }
    }

    public static IEnumerable<object[]> SubmitGetterFaults()
    {
        foreach (bool bound in new[] { false, true })
            for (int fault = 0; fault < 8; fault++) yield return [bound, fault];
    }

    [Theory]
    [MemberData(nameof(SubmitGetterFaults))]
    public void V6DmaSubmitGenerationGetterPreservesEffectAndAuthorization(bool bound, int fault)
    {
        Scenario scenario = CreateScenario(1599, 2490, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<PlatformDmaSubmission> Submit()
        {
            if (!bound) return scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared);
            KernelResult<V6PlatformDmaExecution> execution = scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, sink);
            return execution.IsSuccess ? KernelResult<PlatformDmaSubmission>.Ok(execution.Value!.Submission)
                : KernelResult<PlatformDmaSubmission>.Fail(execution.Error, execution.Message!);
        }
        KernelError? nestedError = null;
        bool called = false;
        Action getter = () =>
        {
            called = true;
            if (fault is 1 or 4) nestedError = scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error;
            if (fault == 2) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault is 3 or 5) throw new InvalidOperationException("Injected submit generation read loss.");
            if (fault == 6) _ = scenario.Kernel.RevokeCapability(scenario.DeviceCapability);
            if (fault == 7) nestedError = Submit().Error;
        };
        if (fault is 4 or 5) scenario.Provider.SubmitEffect = () => scenario.Provider.OnIncarnationRead = getter;
        else scenario.Provider.OnIncarnationRead = getter;
        KernelResult<PlatformDmaSubmission> result = Submit();
        Assert.True(called);
        if (fault is 2 or 3 or 5 or 6)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Equal(fault == 5 ? 1 : 0, scenario.Provider.SubmitCalls);
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
            if (bound && fault == 5)
                Assert.Equal(new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                    SemanticTraceEventKindV1.Quarantined }, sink.Events.Select(e => e.Kind));
            if (bound && fault != 5) Assert.Empty(sink.Events);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            if (fault is 1 or 4 or 7) Assert.Equal(KernelError.PlatformBindingDraining, nestedError);
            Assert.Equal(1, scenario.Provider.SubmitCalls);
            Assert.Equal(prepared.Cycle, result.Value!.PreparedCycle);
            PlatformDmaCompletionEvidence completed = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, result.Value).Value!;
            Assert.True(scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(scenario.Subject, result.Value, completed).IsSuccess);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void V6DmaSubmitRegionRevalidationRejectsGetterQuarantine(bool bound, bool quarantine)
    {
        Scenario scenario = CreateScenario(1600, 2500, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        RegionOwner owner = new RegionOwner(scenario.Grant.DeviceLease.DomainBinding.Subject.DomainId, scenario.Subject.Generation);
        MutationEpoch before = scenario.Kernel.Regions.Validate(scenario.Mapping.Region, owner).Value!.MutationEpoch;
        bool called = false;
        scenario.Provider.OnIncarnationRead = () =>
        {
            called = true;
            if (!quarantine) return;
            ProviderHealthEvidenceV1 evidence = new ProviderHealthEvidenceV1(1, new("dma-region-model", "bank-0", 1, 1),
                1, ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8));
            Assert.True(scenario.Kernel.Regions.QuarantineSubrange(scenario.Mapping.Region, owner, evidence).IsSuccess);
        };
        DmaTraceSink sink = new DmaTraceSink();
        KernelError error;
        bool success;
        if (bound)
        {
            KernelResult<V6PlatformDmaExecution> result = scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, sink);
            error = result.Error; success = result.IsSuccess;
        }
        else
        {
            KernelResult<PlatformDmaSubmission> result = scenario.Kernel.SubmitPlatformDma(scenario.Subject, scenario.Grant, prepared);
            error = result.Error; success = result.IsSuccess;
        }
        Assert.True(called);
        Assert.Equal(!quarantine, success);
        Assert.Equal(quarantine ? 0 : 1, scenario.Provider.SubmitCalls);
        if (quarantine)
        {
            Assert.Equal(KernelError.Quarantined, error);
            Assert.NotEqual(before, scenario.Kernel.Regions.Validate(scenario.Mapping.Region, owner).Value!.MutationEpoch);
            Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Mapping.Region, owner));
            Assert.Single(scenario.Kernel.Regions.SnapshotDamage());
            Assert.Empty(sink.Events);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V6DmaSubmitRegionRevalidationRejectsOwnerEpochAdvanceWithoutDamage(bool late)
    {
        Scenario scenario = CreateScenario(1601, 2510, PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaPrepareEvidence prepared = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant).Value!;
        RegionOwner owner = new RegionOwner(scenario.Grant.DeviceLease.DomainBinding.Subject.DomainId, scenario.Subject.Generation);
        MutationEpoch before = scenario.Kernel.Regions.Validate(scenario.Mapping.Region, owner).Value!.MutationEpoch;
        Action advance = () =>
        {
            ExternalOperationHandle operation = scenario.Kernel.ExternalOperations.Prepare(owner,
                [new(scenario.Mapping.Region, RegionUseMode.ExclusiveWrite, new(0, 8))],
                ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
            Assert.True(scenario.Kernel.ExternalOperations.AdmitForExactPlatformMappings(operation, new(1, 1)).IsSuccess);
            Assert.True(scenario.Kernel.ExternalOperations.Cancel(operation, providerCancellationSupported: false).IsSuccess);
            // Never submitted: the existing owner releases without a provider-closure claim.
            Assert.True(scenario.Kernel.ExternalOperations.Release(operation, new(false, false)).IsSuccess);
        };
        if (late) scenario.Provider.SubmitEffect = advance;
        else scenario.Provider.OnIncarnationRead = advance;
        DmaTraceSink sink = new DmaTraceSink();
        KernelResult<V6PlatformDmaExecution> result = scenario.Kernel.SubmitV6PlatformDma(scenario.Subject, scenario.Grant, prepared, sink);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(late ? 1 : 0, scenario.Provider.SubmitCalls);
        Assert.NotEqual(before, scenario.Kernel.Regions.Validate(scenario.Mapping.Region, owner).Value!.MutationEpoch);
        Assert.True(scenario.Kernel.Regions.ValidatePlatformMappingRegionUsability(scenario.Mapping.Region, owner).IsSuccess);
        Assert.Empty(scenario.Kernel.Regions.SnapshotDamage());
        if (late)
        {
            Assert.True(scenario.Kernel.PlatformAuthority.HasActiveDmaSubmission(scenario.Grant.GrantId));
            Assert.True(scenario.Kernel.PlatformAuthority.HasFaultPinnedDmaSubmission(scenario.Grant.GrantId));
            Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).IsSuccess);
            Assert.Equal(new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.Quarantined }, sink.Events.Select(e => e.Kind));
            Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        }
        else Assert.Empty(sink.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void V6DmaCopyGenerationGetterPreservesBothOwnersAndRegionTuples(int fault)
    {
        CrossOwnerCopyScenario scenario = CreateCrossOwnerCopyScenario(1602, 2520, 1603, 2530);
        PlatformDmaPrepareEvidence source = scenario.Kernel.PreparePlatformDmaForDevice(scenario.SourceSubject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destination = scenario.Kernel.PreparePlatformDmaForDevice(scenario.DestinationSubject, scenario.Destination).Value!;
        KernelResult<V6PlatformDmaCopyExecution> Submit() => scenario.Kernel.SubmitV6PlatformDmaCopy(
            scenario.SourceSubject, scenario.Source, source, scenario.DestinationSubject, scenario.Destination, destination);
        KernelError? nestedError = null;
        bool called = false;
        Action getter = () =>
        {
            called = true;
            if (fault is 1 or 6) nestedError = scenario.Kernel.RevokePlatformDma(scenario.SourceSubject, scenario.Source).Error;
            if (fault is 2 or 7) nestedError = scenario.Kernel.RevokePlatformDma(scenario.DestinationSubject, scenario.Destination).Error;
            if (fault == 3) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault is 4 or 5) throw new InvalidOperationException("Injected copy generation loss.");
            if (fault is >= 8 and <= 11)
            {
                PlatformDmaGrant grant = fault is 9 or 11 ? scenario.Destination : scenario.Source;
                ProcessHandle subject = fault is 9 or 11 ? scenario.DestinationSubject : scenario.SourceSubject;
                RegionOwner owner = new RegionOwner(grant.DeviceLease.DomainBinding.Subject.DomainId, subject.Generation);
                if (fault is 8 or 9)
                    Assert.True(scenario.Kernel.Regions.QuarantineSubrange(grant.Mapping.Region, owner,
                        new(1, new("copy-region-model", "bank-0", 1, 1), 1,
                            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8))).IsSuccess);
                else
                {
                    ExternalOperationHandle operation = scenario.Kernel.ExternalOperations.Prepare(owner,
                        [new(grant.Mapping.Region, RegionUseMode.ExclusiveWrite, new(0, 8))],
                        ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
                    Assert.True(scenario.Kernel.ExternalOperations.AdmitForExactPlatformMappings(operation, new(1, 1)).IsSuccess);
                    Assert.True(scenario.Kernel.ExternalOperations.Cancel(operation, false).IsSuccess);
                    Assert.True(scenario.Kernel.ExternalOperations.Release(operation, new(false, false)).IsSuccess);
                }
            }
            if (fault == 12) nestedError = Submit().Error;
        };
        if (fault is 5 or 6 or 7) scenario.Provider.CopyEffect = () => scenario.Provider.OnIncarnationRead = getter;
        else scenario.Provider.OnIncarnationRead = getter;
        KernelResult<V6PlatformDmaCopyExecution> result = Submit();
        Assert.True(called);
        if (fault is 3 or 4 or 5 or 8 or 9 or 10 or 11)
        {
            Assert.Equal(fault is 8 or 9 ? KernelError.Quarantined : fault is 10 or 11 ? KernelError.StaleGeneration : KernelError.PlatformFaulted, result.Error);
            Assert.Equal(fault == 5 ? 1 : 0, scenario.Provider.CopySubmitCalls);
            if (fault is >= 8 and <= 11)
            {
                Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.SourceMapping.Region,
                    new(scenario.Source.DeviceLease.DomainBinding.Subject.DomainId, scenario.SourceSubject.Generation)));
                Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.DestinationMapping.Region,
                    new(scenario.Destination.DeviceLease.DomainBinding.Subject.DomainId, scenario.DestinationSubject.Generation)));
                Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
            }
            else
            {
                Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.SourceSubject, scenario.SourceMapping).IsSuccess);
                Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.DestinationSubject, scenario.DestinationMapping).IsSuccess);
            }
            if (fault == 5)
            {
                Assert.Equal(KernelError.PlatformFaulted, scenario.Kernel.RevokePlatformDma(scenario.SourceSubject, scenario.Source).Error);
                Assert.Equal(KernelError.PlatformFaulted, scenario.Kernel.RevokePlatformDma(scenario.DestinationSubject, scenario.Destination).Error);
            }
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            if (fault != 0) Assert.Equal(KernelError.PlatformBindingDraining, nestedError);
            Assert.Equal(1, scenario.Provider.CopySubmitCalls);
            Assert.True(scenario.Kernel.CompleteV6PlatformDmaCopy(scenario.SourceSubject, scenario.DestinationSubject, result.Value!).IsSuccess);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.SourceSubject, scenario.Source).IsSuccess);
            Assert.True(scenario.Kernel.RevokePlatformDma(scenario.DestinationSubject, scenario.Destination).IsSuccess);
        }
    }

    [Fact]
    public void V6DmaCopyGenerationGetterIdentityExhaustionIsRefusedBeforeEffect()
    {
        CopyScenario scenario = CreateCopyScenario(1604, 2540);
        PlatformDmaPrepareEvidence source = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Source).Value!;
        PlatformDmaPrepareEvidence destination = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Destination).Value!;
        FieldInfo field = typeof(PlatformAuthorityBridge).GetField("_nextDmaOperationId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
        scenario.Provider.OnIncarnationRead = () => field.SetValue(scenario.Kernel.PlatformAuthority, ulong.MaxValue);
        KernelResult<V6PlatformDmaCopyExecution> result = scenario.Kernel.SubmitV6PlatformDmaCopy(scenario.Subject, scenario.Source, source,
            scenario.Subject, scenario.Destination, destination);
        Assert.Equal(KernelError.CapacityExhausted, result.Error);
        Assert.Equal(0, scenario.Provider.CopySubmitCalls);
        Assert.Equal(ulong.MaxValue, field.GetValue(scenario.Kernel.PlatformAuthority));
        Assert.True(scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Source).IsSuccess);
        Assert.True(scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Destination).IsSuccess);
    }

    private sealed class SubmissionProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformDmaGrantProvider,
        IPlatformDmaVisibilityProvider,
        IPlatformDmaBoundSubmissionProvider,
        IPlatformDmaCopyBackingProvider,
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
        public PlatformDmaCopyBacking? CopyBacking { get; private set; }
        public Action? SubmitEffect { get; set; }
        public Action? PrepareEffect { get; set; }
        public bool ResetDuringPageFault { get; set; }
        public Action? PageFaultEffect { get; set; }
        public Action? CompletionEffect { get; set; }
        public Action? GrantBindEffect { get; set; }
        public int GrantBindCalls { get; private set; }
        public Action? DeviceBindEffect { get; set; }
        public int DeviceBindCalls { get; private set; }
        public bool ResetDuringAcquire { get; set; }
        public Action? AcquireEffect { get; set; }
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
        private PlatformProviderIncarnation _currentIncarnation = new(1);
        public bool ThrowIncarnationRead { get; set; }
        public Action? OnIncarnationRead { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get
            {
                Action? callback = OnIncarnationRead;
                OnIncarnationRead = null;
                callback?.Invoke();
                return ThrowIncarnationRead ? throw new InvalidOperationException("Injected incarnation read failure.") : _currentIncarnation;
            }
            private set => _currentIncarnation = value;
        }

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
            PlatformProviderDomainLease lease = new PlatformProviderDomainLease(
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
            DeviceBindCalls++;
            if (!_domains.TryGetValue(domainLease.LeaseId, out PlatformProviderDomainLease domain) ||
                domain != domainLease)
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(
                    PlatformAuthorityStatus.WrongDomain, "Wrong domain.");
            PlatformProviderDeviceLease lease = new PlatformProviderDeviceLease(
                new PlatformProviderDeviceLeaseId(_nextDevice++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                device,
                rights);
            _devices.Add(lease.LeaseId, lease);
            DeviceBindEffect?.Invoke();
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
            PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> mapped = MapOwnedRegionSlice(
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
            if (!_domains.TryGetValue(domainLease.LeaseId, out PlatformProviderDomainLease domain) ||
                domain != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Wrong domain.");
            }

            PlatformProviderRegionMappingLease lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                slice.Region,
                slice.Access);
            PlatformProviderOwnedRegionMapping mapped = new PlatformProviderOwnedRegionMapping(lease, slice);
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
            if (!_mappings.TryGetValue(mapping.MappingId, out PlatformProviderOwnedRegionMapping exact) ||
                exact.Lease != mapping ||
                _grants.Values.Any(grant => grant.MappingLease == mapping))
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "The exact mapping is unavailable or still has a DMA grant.");
            PlatformOperationIdentity operation = new PlatformOperationIdentity(
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
            if (!_revocations.Remove(operation.OperationId, out (PlatformOperationIdentity Operation, PlatformProviderRegionMappingId Mapping) revocation) ||
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
            GrantBindCalls++;
            PlatformAuthorityResult validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    validation.Status,
                    validation.Message!);
            }

            if (!_devices.ContainsKey(request.DeviceLease.LeaseId) ||
                !_mappings.TryGetValue(request.MappingLease.MappingId, out PlatformProviderOwnedRegionMapping mapped) ||
                mapped.Slice != request.MappingSlice)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown exact device or mapping authority.");
            }

            PlatformProviderDmaGrant grant = new PlatformProviderDmaGrant(
                new PlatformProviderDmaGrantId(_nextGrant++),
                new PlatformProviderLeaseGeneration(1),
                request.DeviceLease,
                request.MappingLease,
                request.Range,
                request.Direction);
            _grants.Add(grant.GrantId, grant);
            GrantBindEffect?.Invoke();
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
            if (!_grants.TryGetValue(grant.GrantId, out PlatformProviderDmaGrant exact) || exact != grant)
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

            PlatformProviderDmaVisibilityCycle cycle = new PlatformProviderDmaVisibilityCycle(_nextCycle++);
            _cycles[grant.GrantId] = cycle;
            _acquired.Remove(grant.GrantId);
            PrepareEffect?.Invoke();
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

            if (!_grants.TryGetValue(grant.GrantId, out PlatformProviderDmaGrant exact) || exact != grant ||
                !_cycles.TryGetValue(grant.GrantId, out PlatformProviderDmaVisibilityCycle cycle) ||
                _acquired.Contains(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "No exact unacquired prepared cycle.");
            }

            _acquired.Add(grant.GrantId);
            AcquireEffect?.Invoke();
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
            PlatformAuthorityResult requestValidation = PlatformDmaSubmissionContract.ValidateRequest(request);
            if (!requestValidation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    requestValidation.Status,
                    requestValidation.Message!);
            }

            PlatformProviderDmaGrant grant = request.Grant;
            if (!_grants.TryGetValue(grant.GrantId, out PlatformProviderDmaGrant exact) || exact != grant ||
                !_cycles.TryGetValue(grant.GrantId, out PlatformProviderDmaVisibilityCycle cycle) ||
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

            PlatformProviderDmaSubmission submission = CreateSubmission(request);
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
            PlatformProviderDmaCopySubmitRequest request, PlatformDmaCopyBacking backing)
        {
            CopyBacking = backing;
            return SubmitDmaCopy(request);
        }

        public PlatformAuthorityResult<PlatformProviderDmaCopySubmission> SubmitDmaCopy(
            PlatformProviderDmaCopySubmitRequest request)
        {
            CopySubmitCalls++;
            PlatformAuthorityResult validation = PlatformDmaCopySubmissionContract.ValidateRequest(request);
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
            PlatformProviderDmaSubmission source = CreateSubmission(request.Source);
            PlatformProviderDmaSubmission destination = CreateSubmission(request.Destination);
            _submissions.Add(source.GrantId, source);
            _submissions.Add(destination.GrantId, destination);
            CopyBacking?.Source.Span.CopyTo(CopyBacking.Destination.Span);
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
            if (!_submissions.TryGetValue(submission.GrantId, out PlatformProviderDmaSubmission exact) || exact != submission ||
                !_grants.TryGetValue(submission.GrantId, out PlatformProviderDmaGrant grant))
                return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Fail(
                    PlatformAuthorityStatus.Stale, "The exact DMA submission is unavailable.");
            CompletionEntered?.Set();
            _ = CompletionRelease?.Wait(TimeSpan.FromSeconds(10));
            PlatformProviderDmaCompletionState state = KeepWriteCompletionPending &&
                        submission.Direction == PlatformDmaDirection.DeviceWritesMemory
                ? PlatformProviderDmaCompletionState.Pending
                : PlatformProviderDmaCompletionState.Completed;
            if (state == PlatformProviderDmaCompletionState.Completed)
                _submissions.Remove(submission.GrantId);
            CompletionEffect?.Invoke();
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
            PlatformAuthorityResult validation = PlatformDmaPageFaultContract.ValidateRequest(request);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderDmaPageFaultEvidence>.Fail(
                    validation.Status, validation.Message!);
            if (!_submissions.TryGetValue(request.Grant.GrantId, out PlatformProviderDmaSubmission submission) ||
                submission != request.Submission ||
                !_grants.TryGetValue(request.Grant.GrantId, out PlatformProviderDmaGrant grant) || grant != request.Grant)
                return PlatformAuthorityResult<PlatformProviderDmaPageFaultEvidence>.Fail(
                    PlatformAuthorityStatus.Stale, "The exact submitted DMA grant is unavailable.");
            PageFaultEntered?.Set();
            _ = PageFaultRelease?.Wait(TimeSpan.FromSeconds(10));
            PlatformProviderDmaPageFaultEvidence evidence = new PlatformProviderDmaPageFaultEvidence(
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
