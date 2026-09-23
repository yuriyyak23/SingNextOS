using System.Security.Cryptography;
using SingNext.Boot.Capsule;
using SingNext.Boot.Core;
using SingPlus.Platform;
using YAKSys_Hybrid_CPU.Boot.Contracts;
using Model = YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;

namespace SingPlus.Tests.Boot;

public sealed class DirectBootFaultAndDifferentialTests
{
    [Fact]
    public void P15_ExternalHybridCpuRequirementsUseTheAuthoritativeGateTable()
    {
        PlatformExternalRequirement[] requirements =
        [
            PlatformExternalRequirement.ExtHcpu001,
            PlatformExternalRequirement.ExtHcpu002,
            PlatformExternalRequirement.ExtHcpu004,
            PlatformExternalRequirement.ExtHcpu007,
            PlatformExternalRequirement.ExtHcpu008,
            PlatformExternalRequirement.ExtHcpu009,
            PlatformExternalRequirement.ExtHcpu010,
            PlatformExternalRequirement.ExtHcpu011,
        ];

        foreach (var requirement in requirements)
        {
            var gate = Assert.Single(PlatformExternalGateTable.Current, value => value.Requirement == requirement);
            Assert.NotEqual(PlatformExternalGateState.SatisfiedForExplicitProfile, gate.State);
        }
    }

    [Fact]
    public void P15_F14_ResetDuringCopyClearsDestinationAndHandoff()
    {
        var image = new byte[] { 1, 2, 3, 4 };
        var ram = new byte[image.Length];
        var handoff = new byte[256];
        var reset = new SequencedReset(new(1, 1), new(2, 2));
        var result = DirectBootCapsule.PrepareKernelResetAware(1, 1, new(1, 1, 0x8000_0000, 16 * 1024 * 1024, 0, 4096),
            image, SHA384.HashData(image), ram, Info(), handoff, new byte[48], new(1, 1), reset);
        Assert.Equal(BootFailure.ResetObserved, result.Failure);
        Assert.All(ram, static value => Assert.Equal(0, value));
        Assert.All(handoff, static value => Assert.Equal(0, value));
    }

    [Fact]
    public void P15_F08_LinkLossStopsMailboxWithoutRetry()
    {
        var transport = new LinkLostTransport();
        var result = SingPlus.Platform.HybridCpu.Boot.DeadlineMailbox.Execute(transport, new Clock(), 1, [], new byte[1], 2,
            new(1, 1), new StableReset(), 4);
        Assert.Equal(BootFailure.LinkLost, result.Failure);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public void P15_F22_F23_ConfirmedAndLocalRecoveryFailuresEndInBoundedFallbackOrHalt()
    {
        Assert.Equal(BootRecoveryRoute.Replica, BootRecoveryPolicy.Select(default, 0,
            new(true, false, 5), new(true, true, 5), new(true, true, 0), 5, 0));
        Assert.Equal(BootRecoveryRoute.SignedLocalRecovery, BootRecoveryPolicy.Select(default, 0,
            new(true, false, 5), new(true, false, 5), new(true, true, 2), 5, 2));
        Assert.Equal(BootRecoveryRoute.Halt, BootRecoveryPolicy.Select(default, 0,
            new(true, false, 5), new(true, false, 5), new(true, false, 0), 5, 0));
        Assert.Equal(BootRecoveryRoute.Halt, BootRecoveryPolicy.Select(default, 0,
            new(true, false, 5), new(true, false, 5), new(true, true, 1), 5, 2));
    }

    [Fact]
    public void P15_Differential_ProductionSelectorMatchesRetainedModelOracle()
    {
        var volume = Guid.NewGuid();
        var domain = Guid.NewGuid();
        var image = Guid.NewGuid();
        var replica = Guid.NewGuid();
        foreach (var scenario in new[] { "success", "rollback", "split" })
        {
            var productionCandidates = new List<BootCandidate>
            {
                new(volume, replica, image, domain, scenario == "rollback" ? 1UL : 5UL, new byte[] { 1, 2 }, 3, true, true,
                    new("a", 0, 1, 0, 0, 7, "r"))
            };
            var modelCandidates = new List<Model.CxlBootCandidate>
            {
                new(volume, replica, image, scenario == "rollback" ? 1UL : 5UL, new byte[] { 1, 2 }, 3, true, true,
                    new("a", 0, 1, 0, 0, 7, "r"))
            };
            if (scenario == "split")
            {
                productionCandidates.Add(productionCandidates[0] with { ReplicaId = Guid.NewGuid(), ImageId = Guid.NewGuid() });
                modelCandidates.Add(modelCandidates[0] with { ReplicaId = Guid.NewGuid(), ImageId = Guid.NewGuid() });
            }
            var floor = scenario == "rollback" ? 2UL : 0UL;
            var production = BootCandidateSelector.Select(new(volume, domain, 3, null, floor, null, false, true), productionCandidates);
            var model = new Model.SemanticCxlBootSelector().Select(new(volume, 3, null, floor, null, false, true), modelCandidates);
            Assert.Equal(Map(model.Failure), production.Failure);
        }
    }

    private static BootFailure Map(Model.BootSelectionFailure failure) => failure switch
    {
        Model.BootSelectionFailure.None => BootFailure.None,
        Model.BootSelectionFailure.NoCandidate or Model.BootSelectionFailure.RequiredPhysicalSelectorMissing => BootFailure.NotFound,
        Model.BootSelectionFailure.RollbackRejected => BootFailure.RollbackRejected,
        Model.BootSelectionFailure.SplitBrain => BootFailure.AmbiguousState,
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    private static HybridBootInfoV1 Info() => new(0, Guid.NewGuid(), 6, 1, ResetReason.ColdPowerOn, 1, []);

    private sealed class SequencedReset(params BootResetSnapshot[] values) : IBootResetControl
    {
        private int _index;
        public BootResetSnapshot Observe() => values[Math.Min(_index++, values.Length - 1)];
        public BootFailure RequestTerminalReset() => BootFailure.None;
    }
    private sealed class StableReset : IBootResetControl
    {
        public BootResetSnapshot Observe() => new(1, 1);
        public BootFailure RequestTerminalReset() => BootFailure.None;
    }
    private sealed class Clock : IBootClock { public ulong MonotonicTicks => 1; }
    private sealed class LinkLostTransport : IBootCxlTransport
    {
        public int Calls { get; private set; }
        public BootResult<int> Execute(byte opcode, ReadOnlySpan<byte> request, Span<byte> response, ulong deadlineTicks, BootResetSnapshot reset)
        { Calls++; return BootResult<int>.Fail(BootFailure.LinkLost, "injected"); }
    }
}
