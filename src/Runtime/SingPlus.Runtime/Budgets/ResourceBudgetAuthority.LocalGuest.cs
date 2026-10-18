using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class ResourceBudgetAuthority
{
    internal KernelResult ReserveOriginalLocalGuest(ProcessHandle owner, VirtualDomainAuthority.LocalGuestLifetime lifetime, ulong bytes)
    {
        lock (_gate)
        {
            if (lifetime.AdmissionInvoked) return KernelResult.Fail(KernelError.PlatformFaulted, "Original local guest admission cannot repeat.");
            lifetime.AdmissionInvoked = true;
            if (!_processAccounts.ContainsKey(owner) && !HasOriginalProcessAttachment(owner))
            { lifetime.AdmissionKnown = true; return KernelResult.Ok(); }
            var attached = ResolveProcessAttachment(owner);
            if (!attached.IsSuccess) { lifetime.AdmissionKnown = true; return KernelResult.Fail(attached.Error, attached.Message!); }
            var reserved = ReserveCore(owner, attached.Value!.Handle,
                [new(ServiceBudgetDimension.GuestMemoryBytes, bytes), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, bytes)],
                BudgetReservationLifetime.GuestMemory, AdmissionQosHint.None, runtimeAttached: true, localGuestLifetime: lifetime);
            lifetime.AdmissionKnown = true;
            return reserved.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(reserved.Error, reserved.Message!);
        }
    }

    internal KernelResult ValidateOriginalLocalGuestPublication(ProcessHandle owner, VirtualDomainAuthority.LocalGuestLifetime lifetime)
    {
        lock (_gate)
        {
            if (!lifetime.AdmissionKnown || !lifetime.RegionReserved || lifetime.RegionReleased || lifetime.ChargeConsumed)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Local guest admission is incomplete or consumed.");
            if (lifetime.Reservation is not { } handle) return ValidateMappingReservationPublicationLocked(owner, null);
            var resolved = ResolveReservation(owner, handle);
            if (!resolved.IsSuccess || !ReferenceEquals(resolved.Value, lifetime.ChargeOwner) ||
                !ReferenceEquals(resolved.Value!.OriginalLocalGuestOwner, lifetime) || resolved.Value.Lifetime != BudgetReservationLifetime.GuestMemory ||
                resolved.Value.State != BudgetReservationState.Active || resolved.Value.OriginalAccounts.Any(account => account.Retired) ||
                !resolved.Value.OriginalAccounts[0].AttachedProcesses.Contains(owner) ||
                !_processAccounts.TryGetValue(owner, out var current) || current != resolved.Value.Account)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original live local guest charge changed before publication.");
            return KernelResult.Ok();
        }
    }

    internal KernelResult ConsumeOriginalLocalGuest(ProcessHandle owner, VirtualDomainAuthority.LocalGuestLifetime lifetime)
    {
        lock (_gate)
        {
            if (lifetime.Reservation is not { } handle)
                return lifetime.AdmissionKnown || !lifetime.AdmissionInvoked ? KernelResult.Ok() :
                    KernelResult.Fail(KernelError.PlatformFaulted, "Original local guest budget invocation remains unknown.");
            if (lifetime.ChargeOwner is not ReservationRecord record || record.Owner != owner || record.Handle != handle ||
                !ReferenceEquals(record.OriginalLocalGuestOwner, lifetime) || !OwnsOriginalReservation(record))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original local guest quantitative owner/history changed.");
            if (record.State == BudgetReservationState.Released) return KernelResult.Ok();
            var resolved = ResolveReservation(owner, handle);
            if (!resolved.IsSuccess || !ReferenceEquals(resolved.Value, record))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original local guest charge route changed.");
            var released = ReleaseCore(owner, handle, runtimeConsumer: true, localGuestOwner: lifetime);
            return released.IsSuccess && released.Value!.State == BudgetReservationState.Released ? KernelResult.Ok() :
                KernelResult.Fail(released.IsSuccess ? KernelError.PlatformFaulted : released.Error, released.Message ?? "Local guest charge remains pinned.");
        }
    }
}
