using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDsc1ComputeTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ProcessDsc1StageRunsCallbacksOutsideBothGatesAndRetainsActualClosure(int fault)
    {
        var provider = new FaultInjectingDsc1Provider
        {
            ReturnPendingForFirstCancellation = fault == 0,
            ForgeCancellationGeneration = fault == 2,
        };
        var scenario = CreateScenario(provider, 2133, 2430, 32);
        Assert.True(SubmitWhole(scenario).IsSuccess);
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel)!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_dsc1Gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        var outside = true;
        var observed = false;
        var pending = false;
        KernelResult nested = default;
        KernelResult<PlatformDsc1CopySubmission> newWork = default;
        var added = new InventoryOnlyCxlParticipant();
        Action callback = () =>
        {
            observed = true;
            outside &= !Monitor.IsEntered(kernelGate) && !Monitor.IsEntered(bridgeGate);
            nested = scenario.Kernel.TerminateProcess(scenario.Subject);
            var progress = scenario.Kernel.ObserveProcessTeardown(scenario.Subject);
            pending = progress.IsSuccess && !progress.Value!.LocalReclaimCompleted;
            newWork = scenario.Kernel.SubmitPlatformDsc1Copy(scenario.Subject, scenario.Binding, scenario.ComputeCapability,
                scenario.Source, new PlatformDsc1RegionRange(scenario.SourceMapping, 0, 32),
                scenario.Destination, new PlatformDsc1RegionRange(scenario.DestinationMapping, 0, 32));
            if (fault == 4) typeof(RuntimeKernel).GetMethod("RegisterCxlTeardownParticipant", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(scenario.Kernel, new object[] { added });
        };
        provider.BeforeCancel = callback;
        provider.BeforeObservation = callback;
        provider.AfterCancel = fault == 3 ? () => throw new IOException("Exact cancellation reply lost after effect") : null;
        var first = scenario.Kernel.TerminateProcess(scenario.Subject);
        Assert.Equal(fault == 1, first.IsSuccess);
        Assert.True(observed);
        Assert.True(outside);
        Assert.True(pending);
        Assert.Equal(KernelError.PlatformBindingDraining, nested.Error);
        Assert.False(newWork.IsSuccess);
        Assert.Equal(1, provider.SubmitCalls);
        Assert.Equal(1, provider.CancelCalls);
        if (fault == 1) Assert.False(scenario.Kernel.Processes.Resolve(scenario.Subject).IsSuccess);
        else
        {
            Assert.True(scenario.Kernel.Processes.Resolve(scenario.Subject).IsSuccess);
            provider.BeforeCancel = null;
            provider.AfterCancel = null;
            var retry = scenario.Kernel.TerminateProcess(scenario.Subject);
            Assert.Equal(fault is 0 or 4, retry.IsSuccess);
            Assert.Equal(1, provider.CancelCalls);
            Assert.Equal(fault == 0 ? 1 : 0, provider.ObserveCalls);
            Assert.Equal(fault is 2 or 3, scenario.Kernel.Processes.Resolve(scenario.Subject).IsSuccess);
            if (fault is 2 or 3) Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.DestinationMapping).IsSuccess);
            if (fault == 4) Assert.Equal(1, added.Calls);
        }
    }
    [Fact]
    public async Task PausedProcessDsc1CancelAllowsConcurrentObservationWithoutReclaim()
    {
        var provider = new FaultInjectingDsc1Provider();
        var scenario = CreateScenario(provider, 2134, 2440, 32);
        Assert.True(SubmitWhole(scenario).IsSuccess);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var released = false;
        provider.BeforeCancel = () => { entered.Set(); released = release.Wait(TimeSpan.FromSeconds(5)); };
        var closure = Task.Run(() => scenario.Kernel.TerminateProcess(scenario.Subject));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining, scenario.Kernel.TerminateProcess(scenario.Subject).Error);
            Assert.False(scenario.Kernel.ObserveProcessTeardown(scenario.Subject).Value!.LocalReclaimCompleted);
            Assert.False(scenario.Kernel.QueryProcessTeardown(scenario.Subject).Value!.LocalReclaimCompleted);
            Assert.False(scenario.Kernel.SubmitPlatformDsc1Copy(scenario.Subject, scenario.Binding, scenario.ComputeCapability,
                scenario.Source, new PlatformDsc1RegionRange(scenario.SourceMapping, 0, 32),
                scenario.Destination, new PlatformDsc1RegionRange(scenario.DestinationMapping, 0, 32)).IsSuccess);
            Assert.Equal(1, provider.CancelCalls);
            Assert.Equal(1, provider.SubmitCalls);
            Assert.True(scenario.Kernel.Processes.Resolve(scenario.Subject).IsSuccess);
        }
        finally { release.Set(); }
        Assert.True((await closure).IsSuccess);
        Assert.True(released);
        Assert.Equal(1, provider.CancelCalls);
        Assert.False(scenario.Kernel.Processes.Resolve(scenario.Subject).IsSuccess);
    }
    // Inventory mutation fixture only: no provider/resource lifetime or containment claim.
    private sealed class InventoryOnlyCxlParticipant : ICxlTeardownParticipant
    {
        public int Calls { get; private set; }
        public KernelResult CloseForProcess(ProcessHandle process, RegionOwner owner)
        {
            Calls++;
            return KernelResult.Ok();
        }
    }
    [Fact]
    public void HostModelPublishesBoundedCopyOnlyAfterExactClosedCompletion()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateScenario(provider, 2101, 2110, 64);
        for (var index = 0; index < scenario.Source.Length; index++)
            scenario.Source.Span[index] = unchecked((byte)(index * 13 + 5));
        scenario.Destination.Span.Fill(0xCC);
        var expected = scenario.Source.Span.Slice(7, 19).ToArray();
        var destinationAlias = scenario.Destination.Span;

        var submitted = scenario.Kernel.SubmitPlatformDsc1Copy(
            scenario.Subject,
            scenario.Binding,
            scenario.ComputeCapability,
            scenario.Source,
            new PlatformDsc1RegionRange(scenario.SourceMapping, 7, 19),
            scenario.Destination,
            new PlatformDsc1RegionRange(scenario.DestinationMapping, 23, 19));

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(1, provider.SubmitDsc1CopyCallCount);
        Assert.Equal(PlatformFeatureAvailability.ModelOnly,
            submitted.Value!.Feature.Availability);
        Assert.False(scenario.Kernel.QueryPlatformFeatures().Supports(
            PlatformFeatureFamily.Dsc1BulkCompute,
            PlatformDsc1ComputeContract.ContractVersion,
            PlatformFeatureAvailability.Executable));
        Assert.All(destinationAlias.ToArray(), value => Assert.Equal((byte)0xCC, value));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Source.Span[0];
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });

        var completed = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted.Value);

        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(PlatformDsc1CopyOutcome.Completed, completed.Value!.Outcome);
        Assert.True(completed.Value.OutputPublished);
        Assert.Equal(19, completed.Value.ByteLength);
        Assert.Equal(expected, scenario.Destination.Span.Slice(23, 19).ToArray());
        Assert.All(scenario.Destination.Span[..23].ToArray(), value => Assert.Equal((byte)0xCC, value));
        Assert.All(scenario.Destination.Span[42..].ToArray(), value => Assert.Equal((byte)0xCC, value));
        Assert.Equal(1, provider.ObserveDsc1CompletionCallCount);
        Assert.Equal(0, provider.ActiveDsc1SubmissionCount);
        Assert.Equal(KernelError.PlatformBindingNotFound,
            scenario.Kernel.ObservePlatformDsc1Copy(
                scenario.Subject,
                submitted.Value).Error);

        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject,
            scenario.SourceMapping).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject,
            scenario.DestinationMapping).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDomain(
            scenario.Subject,
            scenario.Binding).IsSuccess);
    }

    [Fact]
    public void PendingModelCopyPinsCpuAccessAndCancellationLeavesOutputUnchanged()
    {
        var provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var scenario = CreateScenario(provider, 2102, 2120, 32);
        scenario.Source.Span.Fill(0x2A);
        scenario.Destination.Span.Fill(0x91);

        var submitted = SubmitWhole(scenario);
        Assert.True(submitted.IsSuccess, submitted.Message);

        var pending = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted.Value!);
        Assert.False(pending.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, pending.Error);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.RevokePlatformDomain(
                scenario.Subject,
                scenario.Binding).Error);

        var cancelled = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            submitted.Value!);

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.Equal(PlatformDsc1CopyOutcome.Cancelled, cancelled.Value!.Outcome);
        Assert.False(cancelled.Value.OutputPublished);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x91, value));
        Assert.Equal(1, provider.CancelDsc1CallCount);
    }

    [Fact]
    public void PreAcquiredManagedAliasIsExplicitlyOutsideModelOnlyReservationGuarantee()
    {
        var provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var scenario = CreateScenario(provider, 2113, 2230, 32);
        scenario.Source.Span.Fill(0x26);
        scenario.Destination.Span.Fill(0x91);
        var preAcquiredAlias = scenario.Destination.Span;

        var submitted = SubmitWhole(scenario);

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });
        preAcquiredAlias[0] = 0x4A;
        Assert.Equal((byte)0x4A, preAcquiredAlias[0]);

        var cancelled = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            submitted.Value!);

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.False(cancelled.Value!.OutputPublished);
        Assert.Equal((byte)0x4A, scenario.Destination.Span[0]);
    }

    [Fact]
    public void CancellationIsIssuedOnceAndLaterAttemptsOnlyObserveDrain()
    {
        var provider = new FaultInjectingDsc1Provider
        {
            ReturnPendingForFirstCancellation = true,
        };
        var scenario = CreateScenario(provider, 2114, 2240, 32);
        scenario.Destination.Span.Fill(0x72);
        var submitted = SubmitWhole(scenario).Value!;

        var draining = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            submitted);
        var closed = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            submitted);

        Assert.False(draining.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, draining.Error);
        Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(PlatformDsc1CopyOutcome.Cancelled, closed.Value!.Outcome);
        Assert.Equal(1, provider.CancelCalls);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x72, value));
    }

    [Fact]
    public void ProviderCancellationDenialDoesNotReleaseCustodyOrFakeAcknowledgement()
    {
        var provider = new FaultInjectingDsc1Provider
        {
            DenyFirstCancellation = true,
        };
        var scenario = CreateScenario(provider, 2119, 2290, 32);
        scenario.Destination.Span.Fill(0x39);
        var submitted = SubmitWhole(scenario).Value!;

        var denied = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            submitted);
        Assert.False(denied.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });

        var retried = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            submitted);

        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(PlatformDsc1CopyOutcome.Cancelled, retried.Value!.Outcome);
        Assert.Equal(2, provider.CancelCalls);
        Assert.Equal(0, provider.ObserveCalls);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x39, value));
    }

    [Fact]
    public async Task ConcurrentObservationAndCancellationHaveOneLocalFinalizer()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateScenario(provider, 2115, 2250, 32);
        scenario.Source.Span.Fill(0x5C);
        scenario.Destination.Span.Fill(0x10);
        var submitted = SubmitWhole(scenario).Value!;
        using var start = new ManualResetEventSlim();

        var observeTask = Task.Run(() =>
        {
            start.Wait();
            return scenario.Kernel.ObservePlatformDsc1Copy(
                scenario.Subject,
                submitted);
        });
        var cancelTask = Task.Run(() =>
        {
            start.Wait();
            return scenario.Kernel.CancelPlatformDsc1Copy(
                scenario.Subject,
                submitted);
        });
        start.Set();

        var results = await Task.WhenAll(observeTask, cancelTask);

        var winner = Assert.Single(results, static result => result.IsSuccess);
        var loser = Assert.Single(results, static result => !result.IsSuccess);
        Assert.Equal(PlatformDsc1CopyOutcome.Completed, winner.Value!.Outcome);
        Assert.True(winner.Value.OutputPublished);
        Assert.Contains(loser.Error, new[]
        {
            KernelError.PlatformBindingDraining,
            KernelError.PlatformBindingNotFound,
        });
        Assert.Equal(1,
            provider.ObserveDsc1CompletionCallCount + provider.CancelDsc1CallCount);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x5C, value));
    }

    [Fact]
    public async Task SubmissionAndMappingRevocationAreOneRuntimeLifecycleTransaction()
    {
        var provider = new FaultInjectingDsc1Provider();
        var scenario = CreateScenario(provider, 2118, 2280, 32);
        using var submitEntered = new ManualResetEventSlim();
        using var submitRelease = new ManualResetEventSlim();
        using var revokeStarted = new ManualResetEventSlim();
        using var mappingRevokeEntered = new ManualResetEventSlim();
        provider.SubmitEntered = submitEntered;
        provider.SubmitRelease = submitRelease;
        provider.MappingRevokeEntered = mappingRevokeEntered;

        var submitTask = Task.Run(() => SubmitWhole(scenario));
        Task<KernelResult>? revokeTask = null;
        try
        {
            Assert.True(submitEntered.Wait(TimeSpan.FromSeconds(5)));
            revokeTask = Task.Run(() =>
            {
                revokeStarted.Set();
                return scenario.Kernel.RevokePlatformRegionMapping(
                    scenario.Subject,
                    scenario.SourceMapping);
            });
            Assert.True(revokeStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(mappingRevokeEntered.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.True(revokeTask.IsCompleted);
        }
        finally
        {
            submitRelease.Set();
        }

        var submitted = await submitTask;
        var revoked = await revokeTask!;

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.False(revoked.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, revoked.Error);
        revoked = scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject,
            scenario.SourceMapping);
        Assert.True(revoked.IsSuccess, revoked.Message);
        Assert.Equal(1, provider.CancelCalls);
        Assert.True(mappingRevokeEntered.IsSet);
        scenario.Source.Span[0] = 0x7D;
        scenario.Destination.Span[0] = 0x6E;
    }

    [Fact]
    public void ComputeCapabilityIsIndependentAndValidatedBeforeProviderCall()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateScenario(provider, 2103, 2130, 32);
        var process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        var wrongRights = scenario.Kernel.MintCapability(
            process.DomainId,
            scenario.Subject,
            ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy,
            CapabilityRights.Read).Value!.CapabilityId;
        var wrongResource = scenario.Kernel.MintCapability(
            process.DomainId,
            scenario.Subject,
            ResourceKind.Compute,
            "compute:matrix-model:v1",
            CapabilityRights.Execute).Value!.CapabilityId;
        var memoryCapability = scenario.Kernel.MintCapability(
            process.DomainId,
            scenario.Subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(scenario.Source.Handle.RegionId),
            CapabilityRights.Execute).Value!.CapabilityId;
        var revokedCapability = scenario.Kernel.MintCapability(
            process.DomainId,
            scenario.Subject,
            ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy,
            CapabilityRights.Execute).Value!.CapabilityId;
        Assert.True(scenario.Kernel.RevokeCapability(revokedCapability).IsSuccess);

        var candidates = new[]
        {
            new Dsc1ComputeCapability(new CapabilityId(999_999)),
            new Dsc1ComputeCapability(wrongRights),
            new Dsc1ComputeCapability(wrongResource),
            new Dsc1ComputeCapability(memoryCapability),
            new Dsc1ComputeCapability(revokedCapability),
        };

        foreach (var candidate in candidates)
        {
            var rejected = scenario.Kernel.SubmitPlatformDsc1Copy(
                scenario.Subject,
                scenario.Binding,
                candidate,
                scenario.Source,
                new PlatformDsc1RegionRange(scenario.SourceMapping, 0, 16),
                scenario.Destination,
                new PlatformDsc1RegionRange(scenario.DestinationMapping, 0, 16));

            Assert.False(rejected.IsSuccess);
        }

        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
        scenario.Source.Span[0] = 4;
        scenario.Destination.Span[0] = 8;
    }

    [Fact]
    public void WrongRangeMappingAndRegionGenerationAreRejectedBeforeProviderCall()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateScenario(provider, 2104, 2140, 32);

        var invalid = new[]
        {
            new
            {
                Source = new PlatformDsc1RegionRange(scenario.SourceMapping, -1, 4),
                Destination = new PlatformDsc1RegionRange(scenario.DestinationMapping, 0, 4),
            },
            new
            {
                Source = new PlatformDsc1RegionRange(scenario.SourceMapping, 0, 0),
                Destination = new PlatformDsc1RegionRange(scenario.DestinationMapping, 0, 0),
            },
            new
            {
                Source = new PlatformDsc1RegionRange(scenario.SourceMapping, 24, 16),
                Destination = new PlatformDsc1RegionRange(scenario.DestinationMapping, 0, 16),
            },
            new
            {
                Source = new PlatformDsc1RegionRange(scenario.SourceMapping, 0, 8),
                Destination = new PlatformDsc1RegionRange(scenario.DestinationMapping, 0, 9),
            },
            new
            {
                Source = new PlatformDsc1RegionRange(
                    scenario.SourceMapping with { Access = PlatformMemoryAccess.Write },
                    0,
                    8),
                Destination = new PlatformDsc1RegionRange(scenario.DestinationMapping, 0, 8),
            },
        };

        foreach (var request in invalid)
        {
            var rejected = scenario.Kernel.SubmitPlatformDsc1Copy(
                scenario.Subject,
                scenario.Binding,
                scenario.ComputeCapability,
                scenario.Source,
                request.Source,
                scenario.Destination,
                request.Destination);
            Assert.False(rejected.IsSuccess);
        }

        var staleBufferMapping = scenario.SourceMapping with
        {
            Region = scenario.SourceMapping.Region with
            {
                Generation = new RegionGeneration(
                    scenario.SourceMapping.Region.Generation.Value + 1),
            },
        };
        var stale = scenario.Kernel.SubmitPlatformDsc1Copy(
            scenario.Subject,
            scenario.Binding,
            scenario.ComputeCapability,
            scenario.Source,
            new PlatformDsc1RegionRange(staleBufferMapping, 0, 8),
            scenario.Destination,
            new PlatformDsc1RegionRange(scenario.DestinationMapping, 0, 8));

        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
    }

    [Fact]
    public void ForgedStaleOrCrossProcessLocalSubmissionNeverReachesProviderObservation()
    {
        var provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var scenario = CreateScenario(provider, 2105, 2150, 32);
        var (_, sibling) = TestFixtures.Create(scenario.Kernel, 2106, 2160);
        var submitted = SubmitWhole(scenario).Value!;

        var stale = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted with
            {
                Generation = new PlatformDsc1SubmissionGeneration(
                    submitted.Generation.Value + 1),
            });
        var forged = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted with
            {
                Destination = submitted.Destination with
                {
                    Length = submitted.Destination.Length - 1,
                },
            });
        var wrongProcess = scenario.Kernel.ObservePlatformDsc1Copy(sibling, submitted);

        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(KernelError.PlatformDenied, forged.Error);
        Assert.Equal(KernelError.WrongPlatformDomain, wrongProcess.Error);
        Assert.Equal(0, provider.ObserveDsc1CompletionCallCount);
        Assert.True(scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            submitted).IsSuccess);
    }

    [Fact]
    public void ProviderDenialRollsBackReservationsWithoutPublishingOutput()
    {
        var provider = new FaultInjectingDsc1Provider
        {
            DenySubmit = true,
        };
        var scenario = CreateScenario(provider, 2107, 2170, 32);
        scenario.Source.Span.Fill(0x41);
        scenario.Destination.Span.Fill(0xB7);

        var rejected = SubmitWhole(scenario);

        Assert.False(rejected.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, rejected.Error);
        Assert.Equal(1, provider.SubmitCalls);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0xB7, value));
        scenario.Source.Span[0] = 7;
    }

    [Theory]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(4, false)] [InlineData(4, true)]
    [InlineData(5, false)] [InlineData(5, true)]
    [InlineData(6, false)] [InlineData(6, true)]
    [InlineData(7, false)] [InlineData(7, true)]
    [InlineData(8, false)] [InlineData(8, true)]
    [InlineData(9, false)] [InlineData(9, true)]
    [InlineData(10, false)] [InlineData(10, true)]
    [InlineData(11, false)] [InlineData(11, true)]
    [InlineData(12, false)] [InlineData(12, true)]
    public void KnownModelReceiptRefusesLatePublicationAndRemainsCleanable(int fault, bool duringSourceQuery)
    {
        var provider = new FaultInjectingDsc1Provider();
        var scenario = CreateScenario(provider, 2182, 2282, 32);
        scenario.Source.Span.Fill(0x41);
        scenario.Destination.Span.Fill(0xB7);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        global::System.Collections.IDictionary Records(string name) =>
            (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
                .GetField(name, flags)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        var domains = Records("_domains");
        var subjects = Records("_activeSubjects");
        var mappings = Records("_mappings");
        var operations = Records("_dsc1Operations");
        var originalDomain = domains[scenario.Binding.BindingId]!;
        var originalMapping = mappings[scenario.SourceMapping.MappingId]!;
        object? originalOperation = null;
        var injected = false;
        PlatformDsc1SubmissionId Identity(object record) =>
            ((PlatformDsc1CopySubmission)record.GetType().GetProperty("Submission")!.GetValue(record)!).SubmissionId;
        void Inject()
        {
            if (injected) return;
            injected = true;
            object Foreign(object original) => original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            if (fault == 1) domains.Remove(scenario.Binding.BindingId);
            if (fault == 2) domains[scenario.Binding.BindingId] = Foreign(originalDomain);
            if (fault == 3) mappings.Remove(scenario.SourceMapping.MappingId);
            if (fault == 4) mappings[scenario.SourceMapping.MappingId] = Foreign(originalMapping);
            if (fault == 5) _ = scenario.Kernel.RevokeCapability(scenario.ComputeCapability.CapabilityId);
            if (fault == 6) provider.Incarnation = 2;
            if (fault == 7) provider.ThrowIncarnation = true;
            if (fault == 8) subjects.Remove(scenario.Binding.Subject);
            if (fault == 9) operations.Remove(Identity(originalOperation!));
            if (fault == 10) operations[Identity(originalOperation!)] = Foreign(originalOperation!);
            if (fault == 11) _ = scenario.Kernel.RevokeCapability(scenario.SourceCapability);
            if (fault == 12) subjects[scenario.Binding.Subject] = Foreign(originalDomain);
        }
        provider.AfterSubmissionAdmission = () =>
        {
            originalOperation = Assert.Single(operations.Values.Cast<object>());
            provider.BeforeIncarnationRead = () =>
            {
                Assert.NotNull(originalOperation.GetType().GetProperty("AcceptedProviderSubmission")!.GetValue(originalOperation));
                foreach (var (owner, name) in new (object, string)[]
                    { (scenario.Kernel, "_platformMemoryUseGate"), (scenario.Kernel.PlatformAuthority, "_dsc1Gate"),
                      (scenario.Kernel.PlatformAuthority, "_secureDomainLifecycleGate") })
                    Assert.False(Monitor.IsEntered(owner.GetType().GetField(name, flags)!.GetValue(owner)!));
                var worker = Task.Run(() => scenario.Kernel.MintCapability(scenario.Binding.Subject.DomainId,
                    scenario.Subject, ResourceKind.Compute, "compute/source-read-worker", CapabilityRights.Execute));
                Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(worker.Result.IsSuccess);
                if (duringSourceQuery) Inject();
            };
            if (!duringSourceQuery) Inject();
        };

        var result = SubmitWhole(scenario);
        provider.BeforeIncarnationRead = null;
        provider.Incarnation = 1;
        provider.ThrowIncarnation = false;
        Assert.Equal(fault == 9, result.IsSuccess);
        Assert.True(injected);
        Assert.NotNull(originalOperation!.GetType().GetProperty("AcceptedProviderSubmission")!.GetValue(originalOperation));
        Assert.Equal(1, provider.ActiveOriginalSubmissions);
        Assert.Equal(1, provider.SubmitCalls);
        Assert.Throws<InvalidOperationException>(() => { _ = scenario.Source.Span.Length; });

        // Restore only the same original records. Missing operation routing is
        // deliberately left absent: cleanup must consume retained owner truth.
        domains[scenario.Binding.BindingId] = originalDomain;
        subjects[scenario.Binding.Subject] = originalDomain;
        mappings[scenario.SourceMapping.MappingId] = originalMapping;
        if (fault == 10) operations[Identity(originalOperation)] = originalOperation;
        var teardown = scenario.Kernel.TerminateProcess(scenario.Subject);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(0, provider.ActiveOriginalSubmissions);
        Assert.Equal(1, provider.SubmitCalls);
        Assert.Equal(1, provider.CancelCalls);
        Assert.All(scenario.Kernel.Regions.Snapshot().Where(r =>
            r.Handle.RegionId == scenario.Source.Handle.RegionId || r.Handle.RegionId == scenario.Destination.Handle.RegionId),
            r => Assert.Equal(RegionState.Released, r.State));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ClosedModelFactConsumptionRequiresOriginalOwnersAndReceiptAtFinalCommit(int fault)
    {
        var provider = new FaultInjectingDsc1Provider();
        Func<ulong> pinned = () => 0;
        Func<ulong> owned = () => 0;
        var scenario = CreateScenario(provider, 2183, 2283, 32, (kernel, owner) =>
        {
            var admin = TestFixtures.Create(kernel, 98, 980).Handle;
            var administration = kernel.MintCapability(new DomainId(980), admin, ResourceKind.KernelService,
                CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
            var budget = kernel.AdmitProcessBudget(admin, administration, owner, "dsc1-final-consumption",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 64), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 64)])
                .Value!.ProcessBudget;
            pinned = () => kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used;
            owned = () => kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        });
        scenario.Destination.Span.Fill(0xB7);
        var submission = SubmitWhole(scenario).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        global::System.Collections.IDictionary Records(object owner, string field) =>
            (global::System.Collections.IDictionary)owner.GetType().GetField(field, flags)!.GetValue(owner)!;
        var domains = Records(scenario.Kernel.PlatformAuthority, "_domains");
        var mappings = Records(scenario.Kernel.PlatformAuthority, "_mappings");
        var operations = Records(scenario.Kernel.PlatformAuthority, "_dsc1Operations");
        var payloads = Records(scenario.Kernel, "_dsc1PayloadOperations");
        var originalDomain = domains[scenario.Binding.BindingId]!;
        var originalMapping = mappings[scenario.SourceMapping.MappingId]!;
        var originalOperation = operations[submission.SubmissionId]!;
        var originalPayload = payloads[submission.SubmissionId]!;
        var observation = originalOperation.GetType().GetProperty("OriginalCompletionObservation")!;
        object? originalObservation = null;
        provider.AfterCancel = () => provider.BeforeIncarnationRead = () =>
        {
            provider.BeforeIncarnationRead = null;
            originalObservation = observation.GetValue(originalOperation);
            object Foreign(object original) => original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            if (fault == 0) domains[scenario.Binding.BindingId] = Foreign(originalDomain);
            if (fault == 1) domains.Remove(scenario.Binding.BindingId);
            if (fault == 2) mappings[scenario.SourceMapping.MappingId] = Foreign(originalMapping);
            if (fault == 3) observation.SetValue(originalOperation, null);
            if (fault == 4) payloads[submission.SubmissionId] = Foreign(originalPayload);
            if (fault == 5) payloads.Remove(submission.SubmissionId);
        };

        var first = scenario.Kernel.CancelPlatformDsc1Copy(scenario.Subject, submission);
        Assert.False(first.IsSuccess);
        Assert.Equal(0, provider.ActiveOriginalSubmissions);
        Assert.Equal(1, provider.CancelCalls);
        Assert.Throws<InvalidOperationException>(() => { _ = scenario.Destination.Span.Length; });
        Assert.Equal(64UL, owned());
        Assert.Equal(64UL, pinned());
        domains[scenario.Binding.BindingId] = originalDomain;
        mappings[scenario.SourceMapping.MappingId] = originalMapping;
        payloads[submission.SubmissionId] = originalPayload;
        observation.SetValue(originalOperation, originalObservation);
        provider.AfterCancel = null;
        provider.BeforeIncarnationRead = null;
        // Consume already validated closure without another provider Cancel or active capability.
        _ = scenario.Kernel.RevokeCapability(scenario.ComputeCapability.CapabilityId);
        if (scenario.Kernel.PlatformAuthority.HasActiveDsc1Operations(scenario.Binding))
            Assert.True(scenario.Kernel.CancelPlatformDsc1Copy(scenario.Subject, submission).IsSuccess);
        Assert.Equal(1, provider.CancelCalls);
        Assert.Equal(64UL, pinned());
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0xB7, value));
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(0UL, pinned());
        Assert.Equal(0UL, owned());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FalseClosedModelProjectionCannotReplaceOriginalClosureFact(bool knownOriginal)
    {
        var provider = new FaultInjectingDsc1Provider { SubmissionOutcomeFault = knownOriginal ? 0 : 1 };
        var scenario = CreateScenario(provider, 2185, 2285, 32);
        var submitted = SubmitWhole(scenario);
        Assert.Equal(knownOriginal, submitted.IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var operations = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_dsc1Operations", flags)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        var record = Assert.Single(operations.Values.Cast<object>());
        var identity = (PlatformDsc1CopySubmission)record.GetType().GetProperty("Submission")!.GetValue(record)!;
        var state = record.GetType().GetProperty("State")!;
        state.SetValue(record, Enum.Parse(state.PropertyType, "ClosedCancelled"));
        if (knownOriginal)
        {
            Assert.True(scenario.Kernel.CancelPlatformDsc1Copy(scenario.Subject, identity).IsSuccess);
            Assert.Equal(1, provider.CancelCalls);
            Assert.Equal(0, provider.ActiveOriginalSubmissions);
            Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        }
        else
        {
            Assert.False(scenario.Kernel.PlatformAuthority.CancelDsc1ModelCopy(identity, scenario.Binding.Subject).IsSuccess);
            Assert.Equal(0, provider.CancelCalls);
            Assert.Equal(1, provider.ActiveOriginalSubmissions);
            Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            Assert.All(scenario.Kernel.Regions.Snapshot(), r => Assert.Equal(RegionState.Owned, r.State));
        }
    }

    [Fact]
    public void CompletedModelClosureConsumesWithoutActivePermissionAndDoesNotRepinAfterEpochChange()
    {
        var provider = new FaultInjectingDsc1Provider(deferDsc1Completion: false);
        var scenario = CreateScenario(provider, 2186, 2286, 32);
        scenario.Destination.Span.Fill(0xB7);
        var submission = SubmitWhole(scenario).Value!;
        provider.BeforeObservation = () => { _ = scenario.Kernel.RevokeCapability(scenario.ComputeCapability.CapabilityId); };
        var refused = scenario.Kernel.ObservePlatformDsc1Copy(scenario.Subject, submission);
        Assert.False(refused.IsSuccess);
        Assert.Equal(0, provider.ActiveOriginalSubmissions);
        Assert.Throws<InvalidOperationException>(() => { _ = scenario.Destination.Span.Length; });
        provider.BeforeObservation = null;
        var consumed = scenario.Kernel.CancelPlatformDsc1Copy(scenario.Subject, submission);
        Assert.True(consumed.IsSuccess, consumed.Message);
        Assert.False(consumed.Value!.OutputPublished);
        Assert.Equal(0, provider.CancelCalls);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.False(scenario.Kernel.PlatformAuthority.HasActiveDsc1Operations(scenario.Binding));
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0xB7, value));
        Assert.True(scenario.Kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        Assert.False(scenario.Kernel.PlatformAuthority.HasActiveDsc1Operations(scenario.Binding));
        scenario.Source.Span[0] = 7;
        scenario.Destination.Span[0] = 8;
    }

    [Fact]
    public async Task RootLifecycleInterlockSerializesActualModelAdmission()
    {
        var provider = new FaultInjectingDsc1Provider();
        var scenario = CreateScenario(provider, 2184, 2284, 32);
        var rootGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        using var started = new ManualResetEventSlim();
        Task<KernelResult<PlatformDsc1CopySubmission>> task;
        Monitor.Enter(rootGate);
        try
        {
            task = Task.Run(() => { started.Set(); return SubmitWhole(scenario); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(task.Wait(TimeSpan.FromMilliseconds(200)));
            Assert.Equal(0, provider.SubmitCalls);
        }
        finally { Monitor.Exit(rootGate); }
        var accepted = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(accepted.IsSuccess, accepted.Message);
        Assert.True(scenario.Kernel.CancelPlatformDsc1Copy(scenario.Subject, accepted.Value!).IsSuccess);
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    [InlineData(4, 0)] [InlineData(4, 1)] [InlineData(4, 2)]
    public void LostModelSubmissionOutcomeRetainsOriginalLifecycleAndRefusesRepeatOrReclaim(int outcome, int routeLoss)
    {
        var provider = new FaultInjectingDsc1Provider { SubmissionOutcomeFault = outcome };
        var scenario = CreateScenario(provider, 2181, 2281, 32);
        scenario.Source.Span.Fill(0x41);
        scenario.Destination.Span.Fill(0xB7);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var operations = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_dsc1Operations", flags)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        var originalDomain = domains[scenario.Binding.BindingId]!;
        object? foreignDomain = null;
        object? originalRecord = null;
        provider.AfterSubmissionAdmission = () =>
        {
            originalRecord ??= Assert.Single(operations.Values.Cast<object>());
            if (routeLoss == 1) domains.Remove(scenario.Binding.BindingId);
            if (routeLoss == 2)
            {
                foreignDomain = originalDomain.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(originalDomain, null)!;
                domains[scenario.Binding.BindingId] = foreignDomain;
            }
        };
        var compensationCalls = 0;
        var compensationVerified = false;
        provider.BeforeCancel = () =>
        {
            compensationCalls++;
            Assert.Same(originalRecord, Assert.Single(operations.Values.Cast<object>()));
            Assert.Equal("Faulted", originalRecord!.GetType().GetProperty("State")!.GetValue(originalRecord)!.ToString());
            foreach (var (owner, name) in new (object, string)[]
                { (scenario.Kernel, "_platformMemoryUseGate"),
                  (scenario.Kernel.PlatformAuthority, "_dsc1Gate"),
                  (scenario.Kernel.PlatformAuthority, "_secureDomainLifecycleGate") })
            {
                var gate = owner.GetType().GetField(name, flags)!.GetValue(owner)!;
                Assert.False(Monitor.IsEntered(gate));
            }
            Assert.False(SubmitWhole(scenario).IsSuccess);
            compensationVerified = true;
        };

        var rejected = SubmitWhole(scenario);
        var retained = operations.Values.Cast<object>().SingleOrDefault();
        Assert.Equal("Quarantined", originalDomain.GetType().GetProperty("AuthorityState")!.GetValue(originalDomain)!.ToString());
        if (foreignDomain is not null)
            Assert.Equal("Active", foreignDomain.GetType().GetProperty("AuthorityState")!.GetValue(foreignDomain)!.ToString());
        Assert.False(SubmitWhole(scenario).IsSuccess);
        // Controlled restoration tests continuity of the same record, not recovery.
        domains[scenario.Binding.BindingId] = originalDomain;
        // ModelOnly promises no external byte access: byte staging can be rolled back
        // independently of the unresolved original provider lifecycle.
        scenario.Source.Span[0] = 7;
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0xB7, value));
        var repeated = SubmitWhole(scenario);
        var root = scenario.Kernel.RevokePlatformDomain(scenario.Subject, scenario.Binding);
        var mapping = scenario.Kernel.RevokeCapability(scenario.SourceCapability);
        var teardown = scenario.Kernel.TerminateProcess(scenario.Subject);

        Assert.False(rejected.IsSuccess);
        Assert.Same(originalRecord, retained);
        Assert.False((bool)retained!.GetType().GetProperty("ProviderCallInFlight")!.GetValue(retained)!);
        Assert.False((bool)retained.GetType().GetProperty("LocalReservationsReleased")!.GetValue(retained)!);
        Assert.Same(provider, retained.GetType().GetProperty("OriginalSource")!.GetValue(retained));
        Assert.Equal(outcome >= 3 ? 1 : 0, compensationCalls);
        Assert.Equal(outcome >= 3, compensationVerified);
        Assert.Equal(outcome == 4, provider.LastCancellationClosed);
        Assert.False(repeated.IsSuccess);
        Assert.Equal(1, provider.SubmitCalls);
        Assert.Equal(1, provider.ActiveOriginalSubmissions);
        Assert.False(root.IsSuccess);
        Assert.False(mapping.IsSuccess);
        Assert.False(teardown.IsSuccess);
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(scenario.Source.Handle,
            new RegionOwner(scenario.Binding.Subject.DomainId, scenario.Subject.Generation)));
        Assert.All(scenario.Kernel.Regions.Snapshot().Where(r =>
            r.Handle.RegionId == scenario.Source.Handle.RegionId ||
            r.Handle.RegionId == scenario.Destination.Handle.RegionId), r => Assert.Equal(RegionState.Owned, r.State));
    }

    [Fact]
    public void MalformedProviderCompletionPinsMappingsAndPublishesNothing()
    {
        var provider = new FaultInjectingDsc1Provider
        {
            ForgeCompletionGeneration = true,
        };
        var scenario = CreateScenario(provider, 2108, 2180, 32);
        scenario.Source.Span.Fill(0x34);
        scenario.Destination.Span.Fill(0xE2);
        var submitted = SubmitWhole(scenario).Value!;

        var observed = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted);

        Assert.False(observed.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, observed.Error);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject,
                scenario.DestinationMapping).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.RevokePlatformDomain(
                scenario.Subject,
                scenario.Binding).Error);
    }

    [Fact]
    public void MalformedCancellationReceiptPinsReservationsAndPublishesNothing()
    {
        var provider = new FaultInjectingDsc1Provider
        {
            ForgeCancellationGeneration = true,
        };
        var scenario = CreateScenario(provider, 2116, 2260, 32);
        scenario.Destination.Span.Fill(0xA8);
        var submitted = SubmitWhole(scenario).Value!;

        var cancelled = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            submitted);

        Assert.False(cancelled.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, cancelled.Error);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject,
                scenario.DestinationMapping).Error);
    }

    [Fact]
    public void MalformedProviderSubmissionCannotPublishLocalContinuationAndQuarantinesDomain()
    {
        var provider = new FaultInjectingDsc1Provider
        {
            ForgeSubmissionRequest = true,
        };
        var scenario = CreateScenario(provider, 2111, 2210, 32);
        scenario.Destination.Span.Fill(0xD3);

        var submitted = SubmitWhole(scenario);

        Assert.False(submitted.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, submitted.Error);
        Assert.Equal(default(PlatformDsc1CopySubmission), submitted.Value);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0xD3, value));
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject,
                scenario.SourceMapping).Error);
    }

    [Fact]
    public void CapabilityRevocationAndProcessTeardownCancelBeforeReclaim()
    {
        var revokeProvider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var revokeScenario = CreateScenario(revokeProvider, 2109, 2190, 32);
        revokeScenario.Destination.Span.Fill(0x73);
        Assert.True(SubmitWhole(revokeScenario).IsSuccess);

        var revoked = revokeScenario.Kernel.RevokeCapability(
            revokeScenario.ComputeCapability.CapabilityId);

        Assert.True(revoked.IsSuccess, revoked.Message);
        Assert.Equal(1, revokeProvider.CancelDsc1CallCount);
        Assert.All(revokeScenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x73, value));

        var mappingProvider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var mappingScenario = CreateScenario(mappingProvider, 2112, 2220, 32);
        mappingScenario.Destination.Span.Fill(0x68);
        Assert.True(SubmitWhole(mappingScenario).IsSuccess);

        var mappingRevoked = mappingScenario.Kernel.RevokeCapability(
            mappingScenario.SourceCapability);

        Assert.True(mappingRevoked.IsSuccess, mappingRevoked.Message);
        Assert.Equal(1, mappingProvider.CancelDsc1CallCount);
        Assert.All(mappingScenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x68, value));

        var teardownProvider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var teardownScenario = CreateScenario(teardownProvider, 2110, 2200, 32);
        Assert.True(SubmitWhole(teardownScenario).IsSuccess);

        var terminated = teardownScenario.Kernel.TerminateProcess(teardownScenario.Subject);

        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.Equal(1, teardownProvider.CancelDsc1CallCount);
        Assert.False(teardownScenario.Source.IsValid);
        Assert.False(teardownScenario.Destination.IsValid);
        Assert.DoesNotContain(
            teardownScenario.Kernel.Regions.Snapshot(),
            region => region.Owner.DomainId == new DomainId(2200) &&
                      region.State != RegionState.Released);
    }

    [Fact]
    public void ProcessTeardownRetriesCancellationByObservationBeforeReclaim()
    {
        var provider = new FaultInjectingDsc1Provider
        {
            ReturnPendingForFirstCancellation = true,
        };
        var scenario = CreateScenario(provider, 2117, 2270, 32);
        Assert.True(SubmitWhole(scenario).IsSuccess);

        var first = scenario.Kernel.TerminateProcess(scenario.Subject);
        var second = scenario.Kernel.ObserveProcessTeardown(scenario.Subject);

        Assert.False(first.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, first.Error);
        Assert.True(second.IsSuccess, second.Message);
        Assert.True(second.Value!.LocalReclaimCompleted);
        Assert.Equal(1, provider.CancelCalls);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.False(scenario.Source.IsValid);
        Assert.False(scenario.Destination.IsValid);
    }

    [Fact]
    public async Task PendingObservationRollsBackThenTerminalCompletionPublishesOneEvent()
    {
        var provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var scenario = CreateScenario(provider, 2120, 2300, 32);
        scenario.Source.Span.Fill(0x6A);
        scenario.Destination.Span.Fill(0x19);
        var destinationAlias = scenario.Destination.Span;
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submitted = SubmitWhole(scenario).Value!;
        var wait = scenario.Kernel.WaitForKernelEventAsync(
            scenario.Subject,
            endpoint).AsTask();

        var pending = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            endpoint);

        Assert.False(pending.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, pending.Error);
        Assert.False(wait.IsCompleted);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
        Assert.All(destinationAlias.ToArray(), value => Assert.Equal((byte)0x19, value));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });

        Assert.True(provider.LastDsc1Submission.HasValue);
        var providerCompletion = provider.CompleteDsc1Copy(
            provider.LastDsc1Submission.Value);
        Assert.True(providerCompletion.IsSuccess, providerCompletion.Message);

        var completed = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            endpoint);
        var delivered = await wait;

        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(PlatformDsc1CopyOutcome.Completed, completed.Value!.Outcome);
        Assert.True(completed.Value.OutputPublished);
        Assert.True(delivered.IsSuccess, delivered.Message);
        Assert.Equal(endpoint, delivered.Value!.Endpoint);
        Assert.Equal(KernelEventClass.Completion, delivered.Value.EventClass);
        Assert.Equal(
            FormattableString.Invariant(
                $"platform/dsc1-terminal-observed/v1/{submitted.SubmissionId.Value}/{submitted.Generation.Value}"),
            delivered.Value.SourceResourceId);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x6A, value));
        Assert.Equal(2, provider.ObserveDsc1CompletionCallCount);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);

        var replay = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            endpoint);
        Assert.False(replay.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingNotFound, replay.Error);
        Assert.Equal(2, provider.ObserveDsc1CompletionCallCount);
    }

    [Fact]
    public void StaleForgedForeignAndClosedEventInputsFailBeforeProviderObservation()
    {
        var provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var scenario = CreateScenario(provider, 2121, 2310, 32);
        var (_, sibling) = TestFixtures.Create(scenario.Kernel, 2122, 2320);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var siblingEndpoint = scenario.Kernel.CreateKernelEventEndpoint(sibling).Value!;
        var closedEndpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        Assert.True(scenario.Kernel.CloseKernelEventEndpoint(
            scenario.Subject,
            closedEndpoint).IsSuccess);
        var submitted = SubmitWhole(scenario).Value!;

        Assert.Equal(
            KernelError.StaleHandle,
            scenario.Kernel.ObservePlatformDsc1Copy(
                scenario.Subject with { Generation = scenario.Subject.Generation + 1 },
                submitted,
                endpoint).Error);
        Assert.Equal(
            KernelError.WrongEndpointOwner,
            scenario.Kernel.ObservePlatformDsc1Copy(
                scenario.Subject,
                submitted,
                siblingEndpoint).Error);
        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.ObservePlatformDsc1Copy(
                scenario.Subject,
                submitted,
                endpoint with
                {
                    Generation = new KernelEventEndpointGeneration(
                        endpoint.Generation.Value + 1),
                }).Error);
        Assert.Equal(
            KernelError.EndpointNotFound,
            scenario.Kernel.ObservePlatformDsc1Copy(
                scenario.Subject,
                submitted,
                closedEndpoint).Error);
        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.ObservePlatformDsc1Copy(
                scenario.Subject,
                submitted with
                {
                    Generation = new PlatformDsc1SubmissionGeneration(
                        submitted.Generation.Value + 1),
                },
                endpoint).Error);
        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.ObservePlatformDsc1Copy(
                scenario.Subject,
                submitted with
                {
                    Destination = submitted.Destination with
                    {
                        Length = submitted.Destination.Length - 1,
                    },
                },
                endpoint).Error);
        Assert.Equal(
            KernelError.WrongPlatformDomain,
            scenario.Kernel.ObservePlatformDsc1Copy(
                sibling,
                submitted,
                siblingEndpoint).Error);

        Assert.Equal(0, provider.ObserveDsc1CompletionCallCount);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(sibling, siblingEndpoint).Error);
        Assert.True(scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            submitted).IsSuccess);
    }

    [Fact]
    public void BusyEndpointBackpressuresBeforeObservationAndRetryDeliversOnce()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateScenario(provider, 2123, 2330, 32);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        scenario.Source.Span.Fill(0x27);
        scenario.Destination.Span.Fill(0x11);

        var firstSubmission = SubmitWhole(scenario).Value!;
        var firstCompletion = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            firstSubmission,
            endpoint);
        Assert.True(firstCompletion.IsSuccess, firstCompletion.Message);
        Assert.Equal(1, provider.ObserveDsc1CompletionCallCount);

        scenario.Source.Span.Fill(0x82);
        scenario.Destination.Span.Fill(0x33);
        var secondOutputAlias = scenario.Destination.Span;
        var secondSubmission = SubmitWhole(scenario).Value!;

        var blocked = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            secondSubmission,
            endpoint);

        Assert.False(blocked.IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted, blocked.Error);
        Assert.Equal(1, provider.ObserveDsc1CompletionCallCount);
        Assert.All(secondOutputAlias.ToArray(), value => Assert.Equal((byte)0x33, value));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });

        var firstEvent = scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint);
        Assert.True(firstEvent.IsSuccess, firstEvent.Message);
        var retried = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            secondSubmission,
            endpoint);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(2, provider.ObserveDsc1CompletionCallCount);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x82, value));

        var secondEvent = scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint);
        Assert.True(secondEvent.IsSuccess, secondEvent.Message);
        Assert.NotEqual(firstEvent.Value!.Sequence, secondEvent.Value!.Sequence);
        Assert.NotEqual(firstEvent.Value.SourceResourceId, secondEvent.Value.SourceResourceId);

        var replay = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            secondSubmission,
            endpoint);
        Assert.False(replay.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingNotFound, replay.Error);
        Assert.Equal(2, provider.ObserveDsc1CompletionCallCount);
    }

    [Fact]
    public async Task DeniedObservationRollsBackReservationAndPreservesWaiterForRetry()
    {
        var provider = new FaultInjectingDsc1Provider
        {
            DenyFirstObservation = true,
        };
        var scenario = CreateScenario(provider, 2124, 2340, 32);
        scenario.Source.Span.Fill(0x44);
        scenario.Destination.Span.Fill(0x95);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submitted = SubmitWhole(scenario).Value!;
        var wait = scenario.Kernel.WaitForKernelEventAsync(
            scenario.Subject,
            endpoint).AsTask();

        var denied = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            endpoint);

        Assert.False(denied.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.False(wait.IsCompleted);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });

        var providerCompletion = provider.CompleteAcceptedDsc1();
        Assert.True(providerCompletion.IsSuccess, providerCompletion.Message);
        var retry = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            endpoint);
        var delivered = await wait;

        Assert.True(retry.IsSuccess, retry.Message);
        Assert.True(delivered.IsSuccess, delivered.Message);
        Assert.Equal(endpoint, delivered.Value!.Endpoint);
        Assert.Equal(2, provider.ObserveCalls);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x44, value));
    }

    [Fact]
    public void MalformedTerminalObservationPublishesNoEventAndPinsReclaim()
    {
        var provider = new FaultInjectingDsc1Provider
        {
            ForgeCompletionGeneration = true,
        };
        var scenario = CreateScenario(provider, 2125, 2350, 32);
        scenario.Source.Span.Fill(0x61);
        scenario.Destination.Span.Fill(0xA7);
        var destinationAlias = scenario.Destination.Span;
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submitted = SubmitWhole(scenario).Value!;
        var providerCompletion = provider.CompleteAcceptedDsc1();
        Assert.True(providerCompletion.IsSuccess, providerCompletion.Message);

        var malformed = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            endpoint);

        Assert.False(malformed.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, malformed.Error);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
        Assert.All(destinationAlias.ToArray(), value => Assert.Equal((byte)0xA7, value));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.TerminateProcess(scenario.Subject).Error);
        Assert.Contains(
            scenario.Kernel.Regions.Snapshot(),
            region => region.Handle.RegionId == scenario.Destination.Handle.RegionId &&
                      region.State == RegionState.Owned);
    }

    [Fact]
    public async Task WaitCancellationLeavesDsc1AndEndpointAuthorityIntact()
    {
        var provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var scenario = CreateScenario(provider, 2126, 2360, 32);
        scenario.Source.Span.Fill(0x38);
        scenario.Destination.Span.Fill(0xC4);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submitted = SubmitWhole(scenario).Value!;
        using var cancellation = new CancellationTokenSource();
        var wait = scenario.Kernel.WaitForKernelEventAsync(
            scenario.Subject,
            endpoint,
            cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(0, provider.ObserveDsc1CompletionCallCount);
        Assert.Equal(0, provider.CancelDsc1CallCount);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });

        Assert.True(provider.LastDsc1Submission.HasValue);
        var providerCompletion = provider.CompleteDsc1Copy(
            provider.LastDsc1Submission.Value);
        Assert.True(providerCompletion.IsSuccess, providerCompletion.Message);
        var observed = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            endpoint);
        Assert.True(observed.IsSuccess, observed.Message);

        var delivered = await scenario.Kernel.WaitForKernelEventAsync(
            scenario.Subject,
            endpoint);
        Assert.True(delivered.IsSuccess, delivered.Message);
        Assert.Equal(KernelEventClass.Completion, delivered.Value!.EventClass);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x38, value));
    }

    [Fact]
    public void PendingCancellationCanPublishExactCancelledTerminalNotification()
    {
        var provider = new FaultInjectingDsc1Provider
        {
            ReturnPendingForFirstCancellation = true,
        };
        var scenario = CreateScenario(provider, 2129, 2390, 32);
        scenario.Source.Span.Fill(0x56);
        scenario.Destination.Span.Fill(0xB3);
        var destinationAlias = scenario.Destination.Span;
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submitted = SubmitWhole(scenario).Value!;

        var draining = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            submitted);
        Assert.False(draining.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, draining.Error);
        Assert.Equal(1, provider.CancelCalls);
        Assert.Equal(0, provider.ObserveCalls);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);

        var cancelled = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            endpoint);

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.Equal(PlatformDsc1CopyOutcome.Cancelled, cancelled.Value!.Outcome);
        Assert.False(cancelled.Value.OutputPublished);
        Assert.Equal(1, provider.CancelCalls);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.All(destinationAlias.ToArray(), value => Assert.Equal((byte)0xB3, value));
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0xB3, value));
        var delivered = scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint);
        Assert.True(delivered.IsSuccess, delivered.Message);
        Assert.Equal(KernelEventClass.Completion, delivered.Value!.EventClass);
        Assert.Equal(
            FormattableString.Invariant(
                $"platform/dsc1-terminal-observed/v1/{submitted.SubmissionId.Value}/{submitted.Generation.Value}"),
            delivered.Value.SourceResourceId);
    }

    [Fact]
    public void DirectTerminalCancellationRemainsEndpointFreeAndPublishesNoEvent()
    {
        var provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var scenario = CreateScenario(provider, 2132, 2420, 32);
        scenario.Destination.Span.Fill(0x6D);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submitted = SubmitWhole(scenario).Value!;

        var cancelled = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            submitted);

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.Equal(PlatformDsc1CopyOutcome.Cancelled, cancelled.Value!.Outcome);
        Assert.False(cancelled.Value.OutputPublished);
        Assert.Equal(1, provider.CancelDsc1CallCount);
        Assert.Equal(0, provider.ObserveDsc1CompletionCallCount);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x6D, value));
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
    }

    [Fact]
    public void UnreadTerminalNotificationDoesNotBecomeReclaimAuthority()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateScenario(provider, 2130, 2400, 32);
        scenario.Source.Span.Fill(0x9C);
        scenario.Destination.Span.Fill(0x12);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submitted = SubmitWhole(scenario).Value!;

        var completed = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            endpoint);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.True(completed.Value!.OutputPublished);

        var terminated = scenario.Kernel.TerminateProcess(scenario.Subject);

        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.Equal(0, provider.CancelDsc1CallCount);
        Assert.False(scenario.Source.IsValid);
        Assert.False(scenario.Destination.IsValid);
        Assert.DoesNotContain(
            scenario.Kernel.Regions.Snapshot(),
            region => region.Owner.DomainId == new DomainId(2400) &&
                      region.State != RegionState.Released);
    }

    [Fact]
    public void EndpointCloseAndProcessExitDoNotBypassCancellationDrainBeforeReclaim()
    {
        var provider = new FaultInjectingDsc1Provider
        {
            ReturnPendingForFirstCancellation = true,
        };
        var scenario = CreateScenario(provider, 2127, 2370, 32);
        scenario.Destination.Span.Fill(0xD2);
        var destinationAlias = scenario.Destination.Span;
        var closedEndpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var exitEndpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        Assert.True(scenario.Kernel.CloseKernelEventEndpoint(
            scenario.Subject,
            closedEndpoint).IsSuccess);
        var submitted = SubmitWhole(scenario).Value!;

        var closedDelivery = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            closedEndpoint);
        Assert.False(closedDelivery.IsSuccess);
        Assert.Equal(KernelError.EndpointNotFound, closedDelivery.Error);
        Assert.Equal(0, provider.ObserveCalls);
        Assert.Equal(0, provider.CancelCalls);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Destination.Span[0];
        });

        var wait = scenario.Kernel.WaitForKernelEventAsync(
            scenario.Subject,
            exitEndpoint).AsTask();
        var terminate = scenario.Kernel.TerminateProcess(scenario.Subject);

        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);
        Assert.ThrowsAny<OperationCanceledException>(() => wait.GetAwaiter().GetResult());
        Assert.Equal(1, provider.CancelCalls);
        Assert.Equal(0, provider.ObserveCalls);

        var exitingDelivery = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            exitEndpoint);
        Assert.False(exitingDelivery.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, exitingDelivery.Error);
        Assert.Equal(0, provider.ObserveCalls);

        var teardown = scenario.Kernel.ObserveProcessTeardown(scenario.Subject);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.True(teardown.Value!.LocalReclaimCompleted);
        Assert.True(teardown.Value.PlatformDomainClosed);
        Assert.Equal(1, provider.CancelCalls);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.All(destinationAlias.ToArray(), value => Assert.Equal((byte)0xD2, value));
        Assert.False(scenario.Source.IsValid);
        Assert.False(scenario.Destination.IsValid);
        Assert.DoesNotContain(
            scenario.Kernel.Regions.Snapshot(),
            region => region.Owner.DomainId == new DomainId(2370) &&
                      region.State != RegionState.Released);

        var (_, recycled) = TestFixtures.Create(
            scenario.Kernel,
            scenario.Subject.ProcessId.Value,
            12_370,
            generation: 2);
        Assert.Equal(
            KernelError.StaleHandle,
            scenario.Kernel.ObservePlatformDsc1Copy(
                scenario.Subject,
                submitted,
                exitEndpoint).Error);
        Assert.Equal(
            KernelError.WrongEndpointOwner,
            scenario.Kernel.ObservePlatformDsc1Copy(
                recycled,
                submitted,
                exitEndpoint).Error);
        Assert.Equal(1, provider.ObserveCalls);
    }

    [Fact]
    public async Task InFlightObservationKeepsEndpointCloseDrainingUntilExactCommit()
    {
        var provider = new FaultInjectingDsc1Provider(deferDsc1Completion: false);
        var scenario = CreateScenario(provider, 2128, 2380, 32);
        scenario.Source.Span.Fill(0x7B);
        scenario.Destination.Span.Fill(0x24);
        var destinationAlias = scenario.Destination.Span;
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submitted = SubmitWhole(scenario).Value!;
        using var observeEntered = new ManualResetEventSlim();
        using var observeRelease = new ManualResetEventSlim();
        provider.ObserveEntered = observeEntered;
        provider.ObserveRelease = observeRelease;

        var observeTask = Task.Run(() => scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            endpoint));
        try
        {
            Assert.True(observeEntered.Wait(TimeSpan.FromSeconds(5)));
            var close = scenario.Kernel.CloseKernelEventEndpoint(
                scenario.Subject,
                endpoint);
            Assert.False(close.IsSuccess);
            Assert.Equal(KernelError.PlatformBindingDraining, close.Error);
            Assert.Equal(
                KernelError.ResponseNotAvailable,
                scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
            Assert.All(destinationAlias.ToArray(), value => Assert.Equal((byte)0x24, value));
        }
        finally
        {
            observeRelease.Set();
        }

        var observed = await observeTask;
        Assert.True(observed.IsSuccess, observed.Message);
        Assert.All(scenario.Destination.Span.ToArray(), value => Assert.Equal((byte)0x7B, value));
        Assert.True(scenario.Kernel.ConsumeKernelEvent(
            scenario.Subject,
            endpoint).IsSuccess);
        Assert.True(scenario.Kernel.CloseKernelEventEndpoint(
            scenario.Subject,
            endpoint).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentEventObserversCallProviderAndPublishOnlyOnce()
    {
        var provider = new FaultInjectingDsc1Provider(deferDsc1Completion: false);
        var scenario = CreateScenario(provider, 2131, 2410, 32);
        scenario.Source.Span.Fill(0x4E);
        scenario.Destination.Span.Fill(0x16);
        var firstEndpoint = scenario.Kernel.CreateKernelEventEndpoint(
            scenario.Subject).Value!;
        var secondEndpoint = scenario.Kernel.CreateKernelEventEndpoint(
            scenario.Subject).Value!;
        var submitted = SubmitWhole(scenario).Value!;
        using var observeEntered = new ManualResetEventSlim();
        using var observeRelease = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        provider.ObserveEntered = observeEntered;
        provider.ObserveRelease = observeRelease;

        var firstTask = Task.Run(() => scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            submitted,
            firstEndpoint));
        Task<KernelResult<PlatformDsc1CopyReceipt>>? secondTask = null;
        try
        {
            Assert.True(observeEntered.Wait(TimeSpan.FromSeconds(5)));
            secondTask = Task.Run(() =>
            {
                secondStarted.Set();
                return scenario.Kernel.ObservePlatformDsc1Copy(
                    scenario.Subject,
                    submitted,
                    secondEndpoint);
            });
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
            var secondWhileWinnerIsBlocked = await secondTask.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert.False(secondWhileWinnerIsBlocked.IsSuccess);
            Assert.Equal(
                KernelError.PlatformBindingDraining,
                secondWhileWinnerIsBlocked.Error);
            Assert.Equal(1, provider.ObserveCalls);
            Assert.Equal(
                KernelError.ResponseNotAvailable,
                scenario.Kernel.ConsumeKernelEvent(
                    scenario.Subject,
                    firstEndpoint).Error);
            Assert.Equal(
                KernelError.ResponseNotAvailable,
                scenario.Kernel.ConsumeKernelEvent(
                    scenario.Subject,
                    secondEndpoint).Error);
        }
        finally
        {
            observeRelease.Set();
        }

        var first = await firstTask;
        var second = await secondTask!;

        Assert.True(first.IsSuccess, first.Message);
        Assert.False(second.IsSuccess);
        Assert.Contains(second.Error, new[]
        {
            KernelError.PlatformBindingDraining,
            KernelError.PlatformBindingNotFound,
        });
        Assert.Equal(1, provider.ObserveCalls);
        Assert.True(scenario.Kernel.ConsumeKernelEvent(
            scenario.Subject,
            firstEndpoint).IsSuccess);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(
                scenario.Subject,
                secondEndpoint).Error);
    }

    [Fact]
    public void ContractValidatesExactLeaseAccessBoundsAndDisjointRegions()
    {
        var lease = ProviderDomain(31, 7, 301, 41, 5);
        var source = ProviderMapping(lease, 51, 3, 61, 4, 128, PlatformMemoryAccess.Read);
        var destination = ProviderMapping(lease, 52, 4, 62, 5, 128, PlatformMemoryAccess.Write);
        var valid = new PlatformDsc1CopyRequest(
            lease,
            new PlatformProviderDsc1RegionRange(source, 4, 32),
            new PlatformProviderDsc1RegionRange(destination, 12, 32),
            PlatformDsc1CopyProfile.UInt8AllOrNone);

        Assert.True(PlatformDsc1ComputeContract.ValidateRequest(valid).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Denied,
            PlatformDsc1ComputeContract.ValidateRequest(
                valid with { Source = valid.Source with { Length = 33 } }).Status);
        Assert.Equal(PlatformAuthorityStatus.Denied,
            PlatformDsc1ComputeContract.ValidateRequest(
                valid with
                {
                    Source = valid.Source with
                    {
                        Mapping = valid.Source.Mapping with
                        {
                            Access = PlatformMemoryAccess.Write,
                        },
                    },
                }).Status);
        Assert.Equal(PlatformAuthorityStatus.Denied,
            PlatformDsc1ComputeContract.ValidateRequest(
                valid with
                {
                    Source = valid.Source with
                    {
                        Mapping = valid.Source.Mapping with
                        {
                            Access = PlatformMemoryAccess.Read |
                                     (PlatformMemoryAccess)(1 << 7),
                        },
                    },
                }).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            PlatformDsc1ComputeContract.ValidateRequest(
                valid with
                {
                    Destination = valid.Destination with
                    {
                        Mapping = valid.Destination.Mapping with
                        {
                            DomainLease = lease with
                            {
                                Generation = new PlatformProviderLeaseGeneration(8),
                            },
                        },
                    },
                }).Status);
        Assert.Equal(PlatformAuthorityStatus.Denied,
            PlatformDsc1ComputeContract.ValidateRequest(
                valid with
                {
                    Destination = valid.Destination with
                    {
                        Mapping = valid.Destination.Mapping with
                        {
                            Region = valid.Source.Mapping.Region,
                        },
                    },
                }).Status);
        Assert.Equal(PlatformAuthorityStatus.Unsupported,
            PlatformDsc1ComputeContract.ValidateRequest(
                valid with
                {
                    Profile = valid.Profile with
                    {
                        ElementType = (PlatformDsc1ElementType)byte.MaxValue,
                    },
                }).Status);

        var oversizedSource = ProviderMapping(
            lease,
            53,
            5,
            63,
            6,
            PlatformDsc1ComputeContract.MaximumByteLength + 1,
            PlatformMemoryAccess.Read);
        var oversizedDestination = ProviderMapping(
            lease,
            54,
            6,
            64,
            7,
            PlatformDsc1ComputeContract.MaximumByteLength + 1,
            PlatformMemoryAccess.Write);
        Assert.Equal(PlatformAuthorityStatus.Denied,
            PlatformDsc1ComputeContract.ValidateRequest(
                new PlatformDsc1CopyRequest(
                    lease,
                    new PlatformProviderDsc1RegionRange(
                        oversizedSource,
                        0,
                        PlatformDsc1ComputeContract.MaximumByteLength + 1),
                    new PlatformProviderDsc1RegionRange(
                        oversizedDestination,
                        0,
                        PlatformDsc1ComputeContract.MaximumByteLength + 1),
                    PlatformDsc1CopyProfile.UInt8AllOrNone)).Status);
    }

    [Fact]
    public void PublicSemanticSurfaceContainsNoTopologyOpcodeOrProviderAuthority()
    {
        var semanticSurface = new[]
        {
            typeof(Dsc1ComputeCapability),
            typeof(PlatformDsc1RegionRange),
            typeof(PlatformDsc1CopySubmission),
            typeof(PlatformDsc1CopyReceipt),
        };
        var forbidden = new[]
        {
            "Lane",
            "Opcode",
            "MicroOp",
            "Physical",
            "Address",
            "Vmcs",
            "Vmx",
            "Ise",
            "PlatformProvider",
            "Neutral",
            "HybridCpu",
        };

        foreach (var type in semanticSurface)
        foreach (var member in type.GetMembers(
                     BindingFlags.Public |
                     BindingFlags.Instance |
                     BindingFlags.Static))
        {
            var signature = member.ToString() ?? member.Name;
            foreach (var term in forbidden)
                Assert.DoesNotContain(term, signature, StringComparison.OrdinalIgnoreCase);
        }

        var host = new HostPlatformAuthorityProvider();
        var feature = host.QueryFeatures().Resolve(
            PlatformFeatureFamily.Dsc1BulkCompute);
        Assert.Equal(PlatformDsc1ComputeContract.ContractVersion, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.ModelOnly, feature.Availability);

        var eventOverload = typeof(RuntimeKernel)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method =>
                method.Name == nameof(RuntimeKernel.ObservePlatformDsc1Copy) &&
                method.GetParameters().Length == 3);
        Assert.Equal(
            typeof(KernelEventEndpoint),
            eventOverload.GetParameters()[2].ParameterType);
        var eventSignature = eventOverload.ToString() ?? eventOverload.Name;
        foreach (var term in forbidden)
            Assert.DoesNotContain(term, eventSignature, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            typeof(RuntimeKernel).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name == nameof(RuntimeKernel.CancelPlatformDsc1Copy) &&
                      method.GetParameters().Length == 3);
    }

    private static KernelResult<PlatformDsc1CopySubmission> SubmitWhole(Scenario scenario) =>
        scenario.Kernel.SubmitPlatformDsc1Copy(
            scenario.Subject,
            scenario.Binding,
            scenario.ComputeCapability,
            scenario.Source,
            new PlatformDsc1RegionRange(
                scenario.SourceMapping,
                0,
                scenario.Source.Length),
            scenario.Destination,
            new PlatformDsc1RegionRange(
                scenario.DestinationMapping,
                0,
                scenario.Destination.Length));

    private static Scenario CreateScenario(
        IPlatformAuthorityProvider provider,
        ulong processId,
        ulong domainId,
        int length,
        Action<RuntimeKernel, ProcessHandle>? beforeAllocation = null)
    {
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, processId, domainId);
        var process = kernel.Processes.Resolve(subject).Value!;
        beforeAllocation?.Invoke(kernel, subject);
        var source = kernel.AllocateBuffer<byte>(subject, length).Value!;
        var destination = kernel.AllocateBuffer<byte>(subject, length).Value!;
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var sourceCapability = kernel.MintCapability(
            process.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(source.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var destinationCapability = kernel.MintCapability(
            process.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(destination.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Write).Value!.CapabilityId;
        var computeCapability = new Dsc1ComputeCapability(
            kernel.MintCapability(
                process.DomainId,
                subject,
                ResourceKind.Compute,
                CapabilityResourceIds.Dsc1Copy,
                CapabilityRights.Execute).Value!.CapabilityId);
        var sourceMapping = kernel.MapPlatformOwnedRegion(
            subject,
            binding,
            sourceCapability,
            source.Handle,
            PlatformMemoryAccess.Read).Value!;
        var destinationMapping = kernel.MapPlatformOwnedRegion(
            subject,
            binding,
            destinationCapability,
            destination.Handle,
            PlatformMemoryAccess.Write).Value!;

        return new Scenario(
            kernel,
            subject,
            binding,
            computeCapability,
            sourceCapability,
            destinationCapability,
            source,
            destination,
            sourceMapping,
            destinationMapping);
    }

    private static PlatformProviderDomainLease ProviderDomain(
        ulong leaseId,
        ulong leaseGeneration,
        ulong domainId,
        ulong processId,
        ulong processGeneration) =>
        new(
            new PlatformProviderDomainLeaseId(leaseId),
            new PlatformProviderLeaseGeneration(leaseGeneration),
            new PlatformDomainIdentity(
                new DomainId(domainId),
                new ProcessHandle(new ProcessId(processId), processGeneration)));

    private static PlatformProviderRegionMappingLease ProviderMapping(
        PlatformProviderDomainLease lease,
        ulong mappingId,
        ulong mappingGeneration,
        ulong regionId,
        ulong regionGeneration,
        long byteLength,
        PlatformMemoryAccess access) =>
        new(
            new PlatformProviderRegionMappingId(mappingId),
            new PlatformProviderLeaseGeneration(mappingGeneration),
            lease,
            new PlatformRegionIdentity(
                new RegionHandle(
                    new RegionId(regionId),
                    new RegionGeneration(regionGeneration)),
                new RegionOwner(
                    lease.Subject.DomainId,
                    lease.Subject.ProcessGeneration),
                byteLength),
            access);

    private sealed record Scenario(
        RuntimeKernel Kernel,
        ProcessHandle Subject,
        PlatformDomainBinding Binding,
        Dsc1ComputeCapability ComputeCapability,
        CapabilityId SourceCapability,
        CapabilityId DestinationCapability,
        OwnedBuffer<byte> Source,
        OwnedBuffer<byte> Destination,
        PlatformRegionMapping SourceMapping,
        PlatformRegionMapping DestinationMapping);

    private sealed class FaultInjectingDsc1Provider :
        IPlatformAuthorityProvider,
        IPlatformProviderIncarnationSource,
        IPlatformFeatureProvider,
        IPlatformRegionRevocationProvider,
        IPlatformDsc1ComputeProvider
    {
        private readonly HostPlatformAuthorityProvider _inner;
        private bool _cancellationAcknowledged;
        private bool _cancellationDenied;
        private bool _observationDenied;

        public FaultInjectingDsc1Provider(bool deferDsc1Completion = true)
        {
            _inner = new HostPlatformAuthorityProvider(
                deferDsc1Completion: deferDsc1Completion);
        }

        public bool DenySubmit { get; init; }
        public ulong Incarnation { get; set; } = 1;
        public bool ThrowIncarnation { get; set; }
        public Action? BeforeIncarnationRead { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get
            {
                BeforeIncarnationRead?.Invoke();
                if (ThrowIncarnation) throw new InvalidOperationException("Original DSC1 source unavailable.");
                return new(Incarnation);
            }
        }
        public int SubmissionOutcomeFault { get; init; }
        public Action? AfterSubmissionAdmission { get; set; }
        public int ActiveOriginalSubmissions => _inner.ActiveDsc1SubmissionCount;
        public bool LastCancellationClosed { get; private set; }
        public bool DenyFirstObservation { get; init; }
        public bool ForgeSubmissionRequest { get; init; }
        public bool ForgeCompletionGeneration { get; init; }
        public bool ForgeCancellationGeneration { get; init; }
        public bool ReturnPendingForFirstCancellation { get; init; }
        public bool DenyFirstCancellation { get; init; }
        public Action? BeforeCancel { get; set; }
        public Action? BeforeObservation { get; set; }
        public Action? AfterCancel { get; set; }
        public int SubmitCalls { get; private set; }
        public int ObserveCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public ManualResetEventSlim? SubmitEntered { get; set; }
        public ManualResetEventSlim? SubmitRelease { get; set; }
        public ManualResetEventSlim? ObserveEntered { get; set; }
        public ManualResetEventSlim? ObserveRelease { get; set; }
        public ManualResetEventSlim? MappingRevokeEntered { get; set; }

        public PlatformProviderDescriptor Descriptor => _inner.Descriptor;

        public PlatformFeatureManifest QueryFeatures() => _inner.QueryFeatures();

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject) => _inner.BindDomain(subject);

        public PlatformAuthorityResult RevokeDomain(
            PlatformProviderDomainLease lease) => _inner.RevokeDomain(lease);

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            _inner.MapOwnedRegion(domainLease, region, access);

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            RevokeRegionMappingCore(mapping, policy);

        public PlatformAuthorityResult<PlatformRegionRevocationTicket>
            BeginRegionMappingRevocation(
                PlatformProviderRegionMappingLease mapping,
                PlatformRegionRevocationPolicy policy)
        {
            MappingRevokeEntered?.Set();
            return _inner.BeginRegionMappingRevocation(mapping, policy);
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation) =>
            _inner.ObserveCompletion(operation);

        private PlatformAuthorityResult RevokeRegionMappingCore(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            MappingRevokeEntered?.Set();
            return _inner.RevokeRegionMapping(mapping, policy);
        }

        public PlatformAuthorityResult<PlatformProviderDsc1Submission> SubmitDsc1Copy(
            PlatformDsc1CopyRequest request)
        {
            SubmitCalls++;
            SubmitEntered?.Set();
            if (SubmitRelease is { } submitRelease &&
                !submitRelease.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting to release the injected DSC1 submission.");
            }

            if (DenySubmit)
            {
                return PlatformAuthorityResult<PlatformProviderDsc1Submission>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Injected DSC1 model denial.");
            }

            var submitted = _inner.SubmitDsc1Copy(request);
            if (submitted.IsSuccess)
            {
                AfterSubmissionAdmission?.Invoke();
                if (SubmissionOutcomeFault == 1)
                    return PlatformAuthorityResult<PlatformProviderDsc1Submission>.Fail(
                        PlatformAuthorityStatus.Denied, "Original admitted model outcome unavailable.");
                if (SubmissionOutcomeFault == 2)
                    throw new InvalidOperationException("Original admitted model outcome lost.");
                if (SubmissionOutcomeFault == 4)
                    return _inner.SubmitDsc1Copy(request with
                    {
                        Source = request.Source with { Length = request.Source.Length / 2 },
                        Destination = request.Destination with { Length = request.Destination.Length / 2 }
                    });
            }
            if (!submitted.IsSuccess || (!ForgeSubmissionRequest && SubmissionOutcomeFault != 3))
                return submitted;

            var submission = submitted.Value!;
            return PlatformAuthorityResult<PlatformProviderDsc1Submission>.Ok(
                submission with
                {
                    Request = submission.Request with
                    {
                        Destination = submission.Request.Destination with
                        {
                            Length = submission.Request.Destination.Length - 1,
                        },
                    },
                });
        }

        public PlatformAuthorityResult<PlatformProviderDsc1Completion>
            ObserveDsc1Completion(PlatformProviderDsc1Submission submission)
        {
            ObserveCalls++;
            BeforeObservation?.Invoke();
            ObserveEntered?.Set();
            if (ObserveRelease is { } observeRelease &&
                !observeRelease.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting to release the injected DSC1 observation.");
            }

            if (DenyFirstObservation && !_observationDenied)
            {
                _observationDenied = true;
                return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Injected DSC1 observation denial.");
            }

            var observed = _cancellationAcknowledged
                ? _inner.CancelDsc1(submission)
                : _inner.ObserveDsc1Completion(submission);
            if (!observed.IsSuccess || !ForgeCompletionGeneration)
                return observed;

            var completion = observed.Value!;
            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Ok(
                completion with
                {
                    Receipt = completion.Receipt with
                    {
                        Generation = new PlatformOperationGeneration(
                            completion.Receipt.Generation.Value + 1),
                    },
                });
        }

        public PlatformAuthorityResult<PlatformProviderDsc1Completion>
            CompleteAcceptedDsc1()
        {
            if (_inner.LastDsc1Submission is not { } submission)
            {
                return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "No DSC1 submission has been accepted by the wrapped Host provider.");
            }

            return _inner.CompleteDsc1Copy(submission);
        }

        public PlatformAuthorityResult<PlatformProviderDsc1Completion> CancelDsc1(
            PlatformProviderDsc1Submission submission)
        {
            CancelCalls++;
            BeforeCancel?.Invoke();
            if (DenyFirstCancellation && !_cancellationDenied)
            {
                _cancellationDenied = true;
                return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Injected DSC1 cancellation denial.");
            }

            if (ReturnPendingForFirstCancellation && !_cancellationAcknowledged)
            {
                _cancellationAcknowledged = true;
                return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Ok(
                    new PlatformProviderDsc1Completion(
                        submission,
                        new PlatformCompletionReceipt(
                            submission.Operation.OperationId,
                            submission.Operation.Generation,
                            submission.Operation.DomainLease,
                            PlatformCompletionState.Draining),
                        PlatformDsc1CompletionDisposition.Pending));
            }

            var cancelled = _inner.CancelDsc1(submission);
            LastCancellationClosed = cancelled.IsSuccess &&
                cancelled.Value!.Receipt.State == PlatformCompletionState.Closed;
            if (cancelled.IsSuccess) AfterCancel?.Invoke();
            if (!cancelled.IsSuccess || !ForgeCancellationGeneration)
                return cancelled;

            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Ok(
                cancelled.Value! with
                {
                    Receipt = cancelled.Value!.Receipt with
                    {
                        Generation = new PlatformOperationGeneration(
                            cancelled.Value.Receipt.Generation.Value + 1),
                    },
                });
        }
    }
}
