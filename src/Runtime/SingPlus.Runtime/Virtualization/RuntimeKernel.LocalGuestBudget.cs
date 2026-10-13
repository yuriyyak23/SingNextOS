using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private void BindGuestParentCharge(ProcessHandle owner,PlatformRegionMapping mapping,BudgetReservationHandle? reservation,object? originalBudgetOwner,ulong quota)
    {
        lock(_platformMemoryUseGate)
        {
            TrackPlatformMapping(owner,mapping);
            lock(Budgets.MappingReservationPublicationGate)
            {
                var bound=PlatformAuthority.BindOriginalMappingBudgetOwner(mapping,reservation is not null,originalBudgetOwner,quota);
                if(bound.IsSuccess && (reservation is null || Budgets.BindOriginalMappingChargeOwnerLocked(originalBudgetOwner,bound.Value!).IsSuccess))
                    if(reservation is { } charge)_mappingBudgetReservations.TryAdd(mapping.MappingId,(owner,charge));
            }
        }
    }

    private KernelResult ConsumeProviderGuestParent(VirtualDomainAuthority.Record record,GuestRegionMapping mapping,ProcessHandle owner,RegionOwner regionOwner,List<BudgetTraceObservation> observations)
        => _virtualDomains.CommitLocalGuestLifetime(record,mapping,lifetime=>
        {
            if(!lifetime.ParentAdmissionInvoked || !lifetime.ParentAdmissionKnown)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original parent admission is unknown; absence is not closure.");
            if(lifetime.ParentMapping is { } parent)
            {
                var current=Processes.Resolve(owner);
                if(!current.IsSuccess)return KernelResult.Fail(current.Error,current.Message!);
                // Provider callbacks must run before the VDA local consumption gate.
                var state=PlatformAuthority.QueryRegionMappingLifecycle(parent,PlatformIdentity(current.Value!));
                if(!state.IsSuccess || !state.Value!.LocalReservationReleased)
                    return KernelResult.Fail(KernelError.PlatformBindingDraining,"Original parent mapping local closure not consumed.");
            }
            else
            {
                if(!lifetime.RegionReleased)
                {
                    var released=Regions.ReleasePlatformMappingReservation(mapping.Region,regionOwner);
                    if(!released.IsSuccess)return released;
                    lifetime.RegionReleased=true;
                }
                if(!lifetime.ChargeConsumed)
                {
                    var released=ReleaseAttachedBudget(owner,lifetime.Reservation,observations);
                    if(!released.IsSuccess)return released;
                    lifetime.ChargeConsumed=true;
                }
            }
            return SessionInvocations.ConsumeVirtualArtifactMappingClosure(owner,record,mapping.Mapping);
        });
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
