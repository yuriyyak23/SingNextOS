using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private KernelResult ReserveLocalGuest(VirtualDomainAuthority.Record record, GuestRegionMapping mapping, ProcessHandle owner, RegionOwner regionOwner) =>
        _virtualDomains.CommitLocalGuestLifetime(record, mapping, lifetime =>
        {
            var budget = Budgets.ReserveOriginalLocalGuest(owner, lifetime, checked((ulong)mapping.GuestRange.ByteLength));
            if (!budget.IsSuccess) return budget;
            lifetime.RegionInvoked = true;
            var reserved = Regions.ReservePlatformMapping(mapping.Region, regionOwner);
            lifetime.RegionInvocationKnown = true;
            lifetime.RegionReserved = reserved.IsSuccess;
            return reserved;
        });

    private KernelResult PublishLocalGuest(VirtualDomainAuthority.Record record, GuestRegionMapping mapping, ProcessHandle owner) =>
        _virtualDomains.CommitLocalGuestLifetime(record, mapping, lifetime =>
        {
            var budget = Budgets.ValidateOriginalLocalGuestPublication(owner, lifetime);
            return budget.IsSuccess ? _virtualDomains.PublishMapping(record, mapping) : budget;
        });

    private KernelResult ConsumeLocalGuest(VirtualDomainAuthority.Record record, GuestRegionMapping mapping, ProcessHandle owner, RegionOwner regionOwner) =>
        _virtualDomains.CommitLocalGuestLifetime(record, mapping, lifetime =>
        {
            // Withdraw publication before either irreversible local cleanup effect.
            // The original lifetime remains available only to the cleanup consumer.
            record.StagedMappings.Add(mapping.Mapping.MappingId);
            if (lifetime.RegionInvoked && !lifetime.RegionInvocationKnown)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original local guest Region invocation remains unknown.");
            if (lifetime.RegionReserved && !lifetime.RegionReleased)
            {
                var released = Regions.ReleasePlatformMappingReservation(mapping.Region, regionOwner);
                if (!released.IsSuccess) return released;
                lifetime.RegionReleased = true;
            }
            if (!lifetime.ChargeConsumed)
            {
                var consumed = Budgets.ConsumeOriginalLocalGuest(owner, lifetime);
                if (!consumed.IsSuccess) return consumed;
                lifetime.ChargeConsumed = true;
            }
            return KernelResult.Ok();
        });
}
