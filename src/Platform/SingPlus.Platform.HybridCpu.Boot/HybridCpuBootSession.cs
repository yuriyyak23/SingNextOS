using SingNext.Boot.Core;

namespace SingPlus.Platform.HybridCpu.Boot;

public interface IHybridCpuBootHardware
{
    BootResult<ulong> DenyDeviceDma(BootPciAddress address, BootResetSnapshot reset);
    bool IsDmaIsolationCurrent(ulong generation, BootResetSnapshot reset);
    BootResult<uint> ReadPci32(BootPciAddress address, ushort offset, BootResetSnapshot reset);
    BootResult<int> ExecuteCxlMailbox(byte opcode, ReadOnlySpan<byte> request, Span<byte> response, ulong deadlineTicks, BootResetSnapshot reset);
}

public sealed class UnsupportedHybridCpuBootHardware : IHybridCpuBootHardware
{
    public BootResult<ulong> DenyDeviceDma(BootPciAddress address, BootResetSnapshot reset) =>
        BootResult<ulong>.Fail(BootFailure.Unsupported, "HybridCPU boot DMA isolation is unavailable.");

    public bool IsDmaIsolationCurrent(ulong generation, BootResetSnapshot reset) => false;

    public BootResult<uint> ReadPci32(BootPciAddress address, ushort offset, BootResetSnapshot reset) =>
        BootResult<uint>.Fail(BootFailure.Unsupported, "HybridCPU boot PCI transport is unavailable.");

    public BootResult<int> ExecuteCxlMailbox(byte opcode, ReadOnlySpan<byte> request, Span<byte> response, ulong deadlineTicks, BootResetSnapshot reset) =>
        BootResult<int>.Fail(BootFailure.Unsupported, "HybridCPU boot CXL mailbox transport is unavailable.");
}

public sealed class HybridCpuBootSession(IHybridCpuBootHardware hardware, BootResetSnapshot reset) :
    IBootDmaIsolation, IBootPciConfiguration, IBootCxlTransport
{
    private ulong _isolationGeneration;
    private BootPciAddress? _isolatedAddress;

    public BootResult<ulong> EstablishDenyByDefault(BootPciAddress address, BootResetSnapshot observedReset)
    {
        _isolationGeneration = 0;
        _isolatedAddress = null;
        if (observedReset != reset)
            return BootResult<ulong>.Fail(BootFailure.ResetObserved, "Reset epoch changed before DMA isolation.");
        var result = hardware.DenyDeviceDma(address, reset);
        if (result.IsSuccess && result.Value != 0)
        {
            _isolationGeneration = result.Value;
            _isolatedAddress = address;
        }
        return result.IsSuccess && result.Value == 0
            ? BootResult<ulong>.Fail(BootFailure.SecurityPolicyDenied, "DMA isolation returned an invalid generation.")
            : result;
    }

    public bool IsCurrent(ulong isolationGeneration, BootResetSnapshot observedReset) =>
        observedReset == reset && isolationGeneration != 0 && isolationGeneration == _isolationGeneration &&
        hardware.IsDmaIsolationCurrent(isolationGeneration, reset);

    public BootResult<uint> Read32(BootPciAddress address, ushort offset, BootResetSnapshot observedReset)
    {
        var guard = Guard(observedReset, address);
        return guard == BootFailure.None
            ? hardware.ReadPci32(address, offset, reset)
            : BootResult<uint>.Fail(guard, "PCI access denied before current DMA isolation.");
    }

    public BootResult<int> Execute(byte opcode, ReadOnlySpan<byte> request, Span<byte> response, ulong deadlineTicks, BootResetSnapshot observedReset)
    {
        var guard = Guard(observedReset);
        return guard == BootFailure.None
            ? hardware.ExecuteCxlMailbox(opcode, request, response, deadlineTicks, reset)
            : BootResult<int>.Fail(guard, "Mailbox access denied before current DMA isolation.");
    }

    private BootFailure Guard(BootResetSnapshot observedReset, BootPciAddress? address = null)
    {
        if (observedReset != reset) return BootFailure.ResetObserved;
        if (address is not null && _isolatedAddress != address) return BootFailure.SecurityPolicyDenied;
        if (_isolationGeneration == 0 || !hardware.IsDmaIsolationCurrent(_isolationGeneration, reset))
            return BootFailure.SecurityPolicyDenied;
        return BootFailure.None;
    }
}
