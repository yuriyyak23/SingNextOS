using SingNext.Boot.Core;

namespace SingPlus.Platform.HybridCpu.Boot;

public readonly record struct PciExtendedCapability(ushort Id, ushort Version, ushort Offset, ushort NextOffset);

public static class BoundedPciCapabilityWalker
{
    public static BootResult<IReadOnlyList<PciExtendedCapability>> Walk(
        IBootPciConfiguration configuration,
        BootPciAddress address,
        ushort firstOffset,
        BootResetSnapshot reset,
        int maximum = BootLimits.MaxCapabilitiesPerFunction)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (maximum is <= 0 or > BootLimits.MaxCapabilitiesPerFunction)
            return BootResult<IReadOnlyList<PciExtendedCapability>>.Fail(BootFailure.LimitExceeded, "PCI capability budget is invalid.");
        var result = new List<PciExtendedCapability>(Math.Min(maximum, 8));
        Span<ushort> seen = stackalloc ushort[BootLimits.MaxCapabilitiesPerFunction];
        var seenCount = 0;
        var current = firstOffset;
        while (current != 0)
        {
            if (result.Count >= maximum || current < 0x100 || (current & 3) != 0)
                return BootResult<IReadOnlyList<PciExtendedCapability>>.Fail(BootFailure.Malformed, "PCI extended capability chain exceeded bounds or is misaligned.");
            for (var i = 0; i < seenCount; i++)
                if (seen[i] == current)
                    return BootResult<IReadOnlyList<PciExtendedCapability>>.Fail(BootFailure.AmbiguousState, "PCI extended capability loop detected.");
            seen[seenCount++] = current;
            var read = configuration.Read32(address, current, reset);
            if (!read.IsSuccess) return BootResult<IReadOnlyList<PciExtendedCapability>>.Fail(read.Failure, read.Detail ?? "PCI capability read failed.");
            var header = read.Value;
            var id = (ushort)(header & 0xffff);
            var version = (ushort)((header >> 16) & 0xf);
            var next = (ushort)((header >> 20) & 0xfff);
            if (id is 0 or 0xffff || version == 0 || (next != 0 && (next < 0x100 || (next & 3) != 0)))
                return BootResult<IReadOnlyList<PciExtendedCapability>>.Fail(BootFailure.Malformed, "PCI extended capability header is malformed.");
            result.Add(new(id, version, current, next));
            current = next;
        }
        return BootResult<IReadOnlyList<PciExtendedCapability>>.Success(result);
    }
}

public readonly record struct CxlType3Dvsec(ushort Revision, ushort LengthBytes, uint CapabilityFlags);

public static class CxlType3DvsecParser
{
    public const ushort CxlVendorId = 0x1e98;
    public const ushort Type3Device = 3;

    public static BootResult<CxlType3Dvsec> Parse(ReadOnlySpan<uint> dwords)
    {
        if (dwords.Length < 3)
            return BootResult<CxlType3Dvsec>.Fail(BootFailure.Malformed, "CXL DVSEC is truncated.");
        var vendor = (ushort)(dwords[0] & 0xffff);
        var revision = (ushort)((dwords[1] >> 16) & 0xf);
        var length = (ushort)((dwords[1] >> 20) & 0xfff);
        var deviceType = (ushort)(dwords[2] & 0xffff);
        if (vendor != CxlVendorId || revision != 1 || length < 12 || (length & 3) != 0 || length > dwords.Length * 4 || deviceType != Type3Device)
            return BootResult<CxlType3Dvsec>.Fail(BootFailure.Unsupported, "DVSEC is not the bounded CXL Type-3 v1 profile.");
        return BootResult<CxlType3Dvsec>.Success(new(revision, length, dwords[2] >> 16));
    }
}

public static class DeadlineMailbox
{
    public static BootResult<int> Execute(
        IBootCxlTransport transport,
        IBootClock clock,
        byte opcode,
        ReadOnlySpan<byte> request,
        Span<byte> response,
        ulong deadlineTicks,
        BootResetSnapshot expectedReset,
        IBootResetControl resetControl,
        int attempts = BootLimits.MaxMailboxAttempts)
    {
        if (attempts is <= 0 or > BootLimits.MaxMailboxAttempts)
            return BootResult<int>.Fail(BootFailure.LimitExceeded, "Mailbox retry budget is invalid.");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (resetControl.Observe() != expectedReset)
                return BootResult<int>.Fail(BootFailure.ResetObserved, "Reset invalidated mailbox work.");
            if (clock.MonotonicTicks >= deadlineTicks)
                return BootResult<int>.Fail(BootFailure.Timeout, "Mailbox deadline expired.");
            var result = transport.Execute(opcode, request, response, deadlineTicks, expectedReset);
            if (result.IsSuccess || result.Failure is BootFailure.LinkLost or BootFailure.ResetObserved or BootFailure.SecurityPolicyDenied)
                return result;
            if (result.Failure != BootFailure.Timeout) return result;
        }
        return BootResult<int>.Fail(BootFailure.Timeout, "Mailbox retry budget exhausted.");
    }
}
