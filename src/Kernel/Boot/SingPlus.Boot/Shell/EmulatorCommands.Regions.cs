using System.Globalization;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Boot.Shell;

public static partial class EmulatorCommands
{
    private static bool SendBorrow(EmulatorSession session, string[] args)
    {
        if (session.Runtime is null) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        if (args.Length != 3)
        {
            EmulatorConsole.Error("[emulator] usage: borrow <ownerPid> <channelId> <regionId>\r\n");
            return false;
        }
        if (!TryReadPid(args, 0, out var pid) ||
            !TryReadChannel(session, args, 1, out var channel) ||
            !TryReadBuffer(session, args, 2, out var buffer)) return false;
        var owner = session.ResolvePid(pid);
        if (!owner.IsSuccess) return Fail(owner.Error, owner.Message);
        if (owner.Value != channel.Left && owner.Value != channel.Right)
            return Fail(KernelError.WrongEndpointOwner, "Process does not own a channel endpoint.");
        // Runtime validates the source owner, protocol and loan conflicts before queueing.
        var sent = session.Runtime.Send(owner.Value, channel.PeerOf(owner.Value),
            channel.EndpointFor(owner.Value), messageId: 1, payload: buffer.Buffer);
        if (!sent.IsSuccess) return Fail(sent.Error, sent.Message);
        EmulatorConsole.Ok($"[kernel] region {buffer.Handle.RegionId.Value} borrow queued, sequence {sent.Value!.Sequence}; use recv to obtain the lease\r\n");
        return true;
    }

    private static bool ShowBorrows(EmulatorSession session, string[] args)
    {
        if (args.Length != 0)
        {
            EmulatorConsole.Error("[emulator] usage: borrows\r\n");
            return false;
        }
        EmulatorConsole.Table(["region", "region gen", "lease gen", "owner pid", "borrower pid", "valid"],
            session.Borrows.OrderBy(static pair => pair.Key).Select(static pair =>
            {
                var record = pair.Value;
                return (IReadOnlyList<string>)new[]
                {
                    pair.Key.ToString(), record.Lease.Handle.Region.Generation.Value.ToString(),
                    record.Lease.Handle.Generation.Value.ToString(), record.Owner.ProcessId.Value.ToString(),
                    record.Borrower.ProcessId.Value.ToString(), record.Lease.IsValid.ToString(),
                };
            }).ToArray());
        return true;
    }

    private static bool CompleteBorrow(EmulatorSession session, string[] args, bool revoke)
    {
        if (session.Runtime is null) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        if (args.Length != 2)
        {
            EmulatorConsole.Error($"[emulator] usage: {(revoke ? "borrow-revoke <ownerPid>" : "borrow-return <borrowerPid>")} <regionId>\r\n");
            return false;
        }
        if (!TryReadPid(args, 0, out var pid) ||
            !TryReadBorrow(session, args, 1, out var record)) return false;
        var principal = session.ResolvePid(pid);
        if (!principal.IsSuccess) return Fail(principal.Error, principal.Message);
        var result = revoke
            ? session.Runtime.RevokeBorrow(principal.Value, record.Lease.Handle)
            : session.Runtime.ReturnBorrow(principal.Value, record.Lease.Handle);
        if (!result.IsSuccess) return Fail(result.Error, result.Message);
        EmulatorConsole.Ok($"[kernel] region {record.Lease.Handle.Region.RegionId.Value} borrow {(revoke ? "revoked" : "returned")}; lease valid={record.Lease.IsValid}\r\n");
        return true;
    }

    private static bool TryReadBorrow(EmulatorSession session, string[] args, int index, out BorrowRecord record)
    {
        record = null!;
        if (args.Length <= index || !ulong.TryParse(args[index], NumberStyles.None,
                CultureInfo.InvariantCulture, out var regionId))
            return Fail(KernelError.InvalidRegionState, "A numeric region id is required.");
        if (session.Borrows.TryGetValue(regionId, out record!)) return true;
        return Fail(KernelError.InvalidRegionState, $"No delivered borrow tracked for region {regionId}.");
    }

    private static bool DumpBuffer(EmulatorSession session, string[] args)
    {
        if (args.Length is < 2 or > 4 || !TryReadPid(args, 0, out var pid) ||
            !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var regionId))
        {
            EmulatorConsole.Error("[emulator] usage: dump <pid> <regionId> [offset] [count]\r\n");
            return false;
        }
        var offset = 0;
        var count = 64;
        if ((args.Length > 2 && !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset)) ||
            (args.Length > 3 && !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out count)) ||
            count is < 1 or > 256)
        {
            EmulatorConsole.Error("[emulator] offset must be nonnegative; count must be 1..256\r\n");
            return false;
        }
        var principal = session.ResolvePid(pid);
        if (!principal.IsSuccess) return Fail(principal.Error, principal.Message);
        try
        {
            ReadOnlySpan<byte> bytes;
            if (session.Buffers.TryGetValue(regionId, out var owned) && owned.Owner == principal.Value)
                bytes = owned.Buffer.Span;
            else if (session.Borrows.TryGetValue(regionId, out var borrowed) && borrowed.Borrower == principal.Value)
                bytes = borrowed.Lease.Span;
            else
                return Fail(KernelError.WrongRegionOwner, "Process has no tracked owner or borrow token for this region.");
            if (offset > bytes.Length)
                return Fail(KernelError.InvalidRegionState, "Offset is outside the buffer.");
            var length = Math.Min(count, bytes.Length - offset);
            for (var index = 0; index < length; index += 16)
            {
                var row = bytes.Slice(offset + index, Math.Min(16, length - index));
                EmulatorConsole.Info($"{offset + index:X8}: {Convert.ToHexString(row)}\r\n");
            }
            EmulatorConsole.Detail($"[emulator] {length} bytes read from region {regionId} as pid {pid}\r\n");
            return true;
        }
        catch (InvalidOperationException exception)
        {
            return Fail(KernelError.InvalidRegionState, exception.Message);
        }
    }
}
