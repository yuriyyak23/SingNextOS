using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDsc1ComputeTests
{
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

        var winner = Assert.Single(results.Where(static result => result.IsSuccess));
        var loser = Assert.Single(results.Where(static result => !result.IsSuccess));
        Assert.Equal(PlatformDsc1CopyOutcome.Completed, winner.Value!.Outcome);
        Assert.True(winner.Value.OutputPublished);
        Assert.Equal(KernelError.PlatformBindingNotFound, loser.Error);
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
            Assert.False(revokeTask.IsCompleted);
        }
        finally
        {
            submitRelease.Set();
        }

        var submitted = await submitTask;
        var revoked = await revokeTask!;

        Assert.True(submitted.IsSuccess, submitted.Message);
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
        int length)
    {
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, processId, domainId);
        var process = kernel.Processes.Resolve(subject).Value!;
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
        IPlatformFeatureProvider,
        IPlatformDsc1ComputeProvider
    {
        private readonly HostPlatformAuthorityProvider _inner = new(
            deferDsc1Completion: true);
        private bool _cancellationAcknowledged;
        private bool _cancellationDenied;

        public bool DenySubmit { get; init; }
        public bool ForgeSubmissionRequest { get; init; }
        public bool ForgeCompletionGeneration { get; init; }
        public bool ForgeCancellationGeneration { get; init; }
        public bool ReturnPendingForFirstCancellation { get; init; }
        public bool DenyFirstCancellation { get; init; }
        public int SubmitCalls { get; private set; }
        public int ObserveCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public ManualResetEventSlim? SubmitEntered { get; set; }
        public ManualResetEventSlim? SubmitRelease { get; set; }
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
            if (!submitted.IsSuccess || !ForgeSubmissionRequest)
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

        public PlatformAuthorityResult<PlatformProviderDsc1Completion> CancelDsc1(
            PlatformProviderDsc1Submission submission)
        {
            CancelCalls++;
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
