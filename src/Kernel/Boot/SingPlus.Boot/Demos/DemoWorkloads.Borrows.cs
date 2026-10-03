using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunBorrows(EmulatorSession session)
    {
        if (session.Runtime is not { } runtime)
            return KernelBootStageResult.Failure("Runtime is unavailable.");
        var owner = session.SpawnProcess("singplus.emulator.borrow.owner", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!owner.IsSuccess) return Failure("spawn owner", owner.Error, owner.Message);
        var reader = session.SpawnProcess("singplus.emulator.borrow.reader", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!reader.IsSuccess) return Failure("spawn reader", reader.Error, reader.Message);
        var allocation = session.AllocateTrackedBuffer(owner.Value, 32);
        if (!allocation.IsSuccess) return Failure("allocate", allocation.Error, allocation.Message);
        var buffer = allocation.Value!.Buffer;
        buffer.Span.Fill(0x5A);
        var expected = Checksum(buffer.Span);
        var channel = runtime.CreateChannel(owner.Value, reader.Value, EmulatorSession.CreateBorrowProtocol(), 2);
        if (!channel.IsSuccess) return Failure("create borrow channel", channel.Error, channel.Message);
        var (left, right) = channel.Value;
        session.Channels.Add(new ChannelRecord(left.ChannelId.Value, owner.Value, reader.Value, left, right, 2));
        var sent = runtime.Send(owner.Value, reader.Value, left, 1, buffer);
        if (!sent.IsSuccess) return Failure("send borrow", sent.Error, sent.Message);
        var received = runtime.Receive(reader.Value, right);
        if (!received.IsSuccess) return Failure("receive borrow", received.Error, received.Message);
        if (received.Value!.Payload is not BorrowLease<byte> lease || !lease.IsValid || Checksum(lease.Span) != expected)
            return KernelBootStageResult.Failure("Read-only borrow payload/checksum mismatch.");
        session.Borrows[buffer.Handle.RegionId.Value] = new BorrowRecord(owner.Value, reader.Value, lease);
        EmulatorConsole.StageDetail($"region {buffer.Handle.RegionId.Value} borrowed by pid {reader.Value.ProcessId.Value}; checksum {expected}");

        var move = runtime.TransferRegion(owner.Value, reader.Value, buffer);
        if (move.IsSuccess || move.Error != KernelError.InvalidRegionState)
            return KernelBootStageResult.Failure($"MOVE while loaned returned unexpected result: {move.Error}.");
        var release = runtime.ReleaseRegion(owner.Value, buffer);
        if (release.IsSuccess || release.Error != KernelError.InvalidRegionState)
            return KernelBootStageResult.Failure($"Release while loaned returned unexpected result: {release.Error}.");
        var wrongReturn = runtime.ReturnBorrow(owner.Value, lease.Handle);
        if (wrongReturn.IsSuccess || wrongReturn.Error != KernelError.InvalidRegionState)
            return KernelBootStageResult.Failure($"Wrong borrower returned unexpected result: {wrongReturn.Error}.");
        EmulatorConsole.StageDetail("MOVE/release refused while loaned; owner cannot return the borrower's lease");

        var returned = runtime.ReturnBorrow(reader.Value, lease.Handle);
        if (!returned.IsSuccess) return Failure("return borrow", returned.Error, returned.Message);
        if (lease.IsValid || Checksum(buffer.Span) != expected)
            return KernelBootStageResult.Failure("Return failed to invalidate the lease or restore owner access.");
        var sentAgain = runtime.Send(owner.Value, reader.Value, left, 1, buffer);
        if (!sentAgain.IsSuccess) return Failure("send second borrow", sentAgain.Error, sentAgain.Message);
        var receivedAgain = runtime.Receive(reader.Value, right);
        if (!receivedAgain.IsSuccess) return Failure("receive second borrow", receivedAgain.Error, receivedAgain.Message);
        if (receivedAgain.Value!.Payload is not BorrowLease<byte> second)
            return KernelBootStageResult.Failure("Second borrow payload mismatch.");
        session.Borrows[buffer.Handle.RegionId.Value] = new BorrowRecord(owner.Value, reader.Value, second);
        var staleReturn = runtime.ReturnBorrow(reader.Value, lease.Handle);
        if (staleReturn.IsSuccess || staleReturn.Error != KernelError.StaleGeneration || !second.IsValid)
            return KernelBootStageResult.Failure("Stale borrow affected the current lease.");
        var revoked = runtime.RevokeBorrow(owner.Value, second.Handle);
        if (!revoked.IsSuccess) return Failure("revoke", revoked.Error, revoked.Message);
        if (second.IsValid) return KernelBootStageResult.Failure("Revoke left the borrow token valid.");
        EmulatorConsole.StageDetail($"returned lease is stale ({staleReturn.Error}); owner revocation invalidated the new lease");

        var moved = runtime.TransferRegion(owner.Value, reader.Value, buffer);
        if (!moved.IsSuccess) return Failure("move after revoke", moved.Error, moved.Message);
        if (buffer.IsValid || Checksum(moved.Value!.Span) != expected)
            return KernelBootStageResult.Failure("MOVE left the source valid or changed data.");
        session.Buffers[buffer.Handle.RegionId.Value] = new BufferRecord(moved.Value.Handle, reader.Value, moved.Value.Length, moved.Value);
        var freed = runtime.ReleaseRegion(reader.Value, moved.Value);
        if (!freed.IsSuccess) return Failure("release after move", freed.Error, freed.Message);
        session.Buffers.Remove(buffer.Handle.RegionId.Value);
        EmulatorConsole.StageDetail("MOVE succeeded after revocation; source invalidated; region released");
        return ReclaimPair(session, reader.Value, owner.Value);
    }
}
