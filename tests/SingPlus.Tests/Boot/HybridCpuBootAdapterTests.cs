using SingNext.Boot.Core;
using SingPlus.Platform.HybridCpu.Boot;

namespace SingPlus.Tests.Boot;

public sealed class HybridCpuBootAdapterTests
{
    private static readonly BootResetSnapshot Reset = new(7, 11);
    private static readonly BootPciAddress Address = new(0, 1, 0, 0);

    [Fact]
    public void P15_05_DeviceAccessIsDeniedUntilDmaIsolationIsCurrent()
    {
        var hardware = new FakeHardware();
        var session = new HybridCpuBootSession(hardware, Reset);
        Assert.Equal(BootFailure.SecurityPolicyDenied, session.Read32(Address, 0x100, Reset).Failure);
        Assert.True(session.EstablishDenyByDefault(Address, Reset).IsSuccess);
        Assert.True(session.Read32(Address, 0x100, Reset).IsSuccess);
        Assert.Equal(BootFailure.ResetObserved, session.Read32(Address, 0x100, new(8, 12)).Failure);
    }

    [Fact]
    public void P15_05_DmaIsolationIsBoundToExactBdfAndFailedReestablishmentRevokesSession()
    {
        var hardware = new FakeHardware();
        var session = new HybridCpuBootSession(hardware, Reset);
        Assert.True(session.EstablishDenyByDefault(Address, Reset).IsSuccess);
        Assert.Equal(BootFailure.SecurityPolicyDenied, session.Read32(new(0, 2, 0, 0), 0x100, Reset).Failure);

        hardware.FailIsolation = true;
        Assert.Equal(BootFailure.Timeout, session.EstablishDenyByDefault(Address, Reset).Failure);
        Assert.Equal(BootFailure.SecurityPolicyDenied, session.Read32(Address, 0x100, Reset).Failure);
    }

    [Fact]
    public void P15_06_CapabilityLoopAndMalformedDvsecFailClosed()
    {
        var hardware = new FakeHardware { PciHeader = Header(0x23, 1, 0x100) };
        var session = new HybridCpuBootSession(hardware, Reset);
        _ = session.EstablishDenyByDefault(Address, Reset);
        Assert.Equal(BootFailure.AmbiguousState, BoundedPciCapabilityWalker.Walk(session, Address, 0x100, Reset).Failure);
        Assert.Equal(BootFailure.Unsupported, CxlType3DvsecParser.Parse([0u, 0u, 0u]).Failure);
        Assert.True(CxlType3DvsecParser.Parse([0x1e98u, (1u << 16) | (12u << 20), 3u]).IsSuccess);
    }

    [Fact]
    public void P15_06_MailboxTimeoutIsBoundedAndResetStopsRetries()
    {
        var transport = new TimeoutTransport();
        var reset = new FixedReset(Reset);
        var result = DeadlineMailbox.Execute(transport, new FixedClock(1), 1, [], new byte[8], 10, Reset, reset, 3);
        Assert.Equal(BootFailure.Timeout, result.Failure);
        Assert.Equal(3, transport.Calls);
        reset.Value = new(8, 12);
        Assert.Equal(BootFailure.ResetObserved, DeadlineMailbox.Execute(transport, new FixedClock(1), 1, [], new byte[8], 10, Reset, reset).Failure);
    }

    [Theory]
    [InlineData(2, 0, BootFailure.Quarantined)]
    [InlineData(-1, 1, BootFailure.ReadbackMismatch)]
    [InlineData(2, 2, BootFailure.Quarantined)]
    public void P15_07_HdmFailuresCompensateOrQuarantine(int failCommit, int mismatch, BootFailure expected)
    {
        var hardware = new FakeHdm { FailCommitHop = failCommit, MismatchHop = mismatch, FailDisableHop = expected == BootFailure.Quarantined ? 0 : -1 };
        var result = new TransactionalHdmMapping(hardware).Map(Request(), Reset);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(hardware.Disabled.OrderDescending().ToArray(), hardware.Disabled.ToArray());
    }

    [Fact]
    public void P15_07_ResetMakesRetirementQuarantineNotRelease()
    {
        var mapping = new TransactionalHdmMapping(new FakeHdm());
        var evidence = mapping.Map(Request(), Reset).Value;
        Assert.Equal(BootFailure.Quarantined, mapping.Retire(evidence, new(8, 12)));
    }

    [Fact]
    public void P15_F13_ResetDuringMappingInvalidatesTransaction()
    {
        var hardware = new FakeHdm { ResetCommitHop = 1 };
        var mapping = new TransactionalHdmMapping(hardware);
        var result = mapping.Map(Request(), Reset);
        Assert.Equal(BootFailure.Quarantined, result.Failure);
        Assert.Equal([2, 1, 0], hardware.Disabled);
        hardware.ResetCommitHop = -1;
        Assert.Equal(BootFailure.Quarantined, mapping.Map(Request(), Reset).Failure);
    }

    [Fact]
    public void P15_07_AmbiguousCompensationPermanentlyQuarantinesMappingOwner()
    {
        var hardware = new FakeHdm { FailCommitHop = 2, FailDisableHop = 0 };
        var mapping = new TransactionalHdmMapping(hardware);
        Assert.Equal(BootFailure.Quarantined, mapping.Map(Request(), Reset).Failure);
        hardware.FailCommitHop = -1;
        hardware.FailDisableHop = -1;
        Assert.Equal(BootFailure.Quarantined, mapping.Map(Request(), Reset).Failure);
    }

    private static uint Header(ushort id, ushort version, ushort next) => (uint)id | ((uint)version << 16) | ((uint)next << 20);
    private static BootMappingRequest Request() => new(0x8000_0000, 16 * 1024 * 1024, 0, 4096, 32 * 1024 * 1024);

    private sealed class FakeHardware : IHybridCpuBootHardware
    {
        public uint PciHeader { get; set; } = Header(0x23, 1, 0);
        public bool FailIsolation { get; set; }
        public BootResult<ulong> DenyDeviceDma(BootPciAddress address, BootResetSnapshot reset) => FailIsolation
            ? BootResult<ulong>.Fail(BootFailure.Timeout, "injected")
            : BootResult<ulong>.Success(9);
        public bool IsDmaIsolationCurrent(ulong generation, BootResetSnapshot reset) => generation == 9;
        public BootResult<uint> ReadPci32(BootPciAddress address, ushort offset, BootResetSnapshot reset) => BootResult<uint>.Success(PciHeader);
        public BootResult<int> ExecuteCxlMailbox(byte opcode, ReadOnlySpan<byte> request, Span<byte> response, ulong deadlineTicks, BootResetSnapshot reset) => BootResult<int>.Success(0);
    }

    private sealed class TimeoutTransport : IBootCxlTransport
    {
        public int Calls { get; private set; }
        public BootResult<int> Execute(byte opcode, ReadOnlySpan<byte> request, Span<byte> response, ulong deadlineTicks, BootResetSnapshot reset)
        { Calls++; return BootResult<int>.Fail(BootFailure.Timeout, "injected"); }
    }

    private sealed class FixedClock(ulong value) : IBootClock { public ulong MonotonicTicks => value; }
    private sealed class FixedReset(BootResetSnapshot value) : IBootResetControl
    {
        public BootResetSnapshot Value { get; set; } = value;
        public BootResetSnapshot Observe() => Value;
        public BootFailure RequestTerminalReset() => BootFailure.None;
    }

    private sealed class FakeHdm : IHdmDecoderHardware
    {
        public int FailCommitHop { get; set; } = -1;
        public int MismatchHop { get; set; } = -1;
        public int FailDisableHop { get; set; } = -1;
        public int ResetCommitHop { get; set; } = -1;
        public List<int> Disabled { get; } = [];
        public int HopCount => 3;
        public BootFailure Validate(int hop, BootMappingRequest request, BootResetSnapshot reset) => BootFailure.None;
        public BootFailure Stage(int hop, BootMappingRequest request, BootResetSnapshot reset) => BootFailure.None;
        public BootFailure Commit(int hop, BootResetSnapshot reset) => hop == ResetCommitHop ? BootFailure.ResetObserved : hop == FailCommitHop ? BootFailure.Timeout : BootFailure.None;
        public BootResult<HdmDecoderState> Readback(int hop, BootResetSnapshot reset)
        {
            var request = Request();
            return BootResult<HdmDecoderState>.Success(new(hop, request.HpaBase + (hop == MismatchHop ? 4096UL : 0), request.HpaBytes,
                request.SourceOffset, request.SourceBytes, true, false, false));
        }
        public BootFailure Disable(int hop, BootResetSnapshot reset)
        { Disabled.Add(hop); return hop == FailDisableHop ? BootFailure.Timeout : BootFailure.None; }
    }
}
