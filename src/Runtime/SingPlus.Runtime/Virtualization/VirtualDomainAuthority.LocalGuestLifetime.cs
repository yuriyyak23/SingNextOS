using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

internal sealed partial class VirtualDomainAuthority
{
    // VDA lifecycle/correlation only. Quantitative truth remains in ResourceBudgetAuthority.
    internal sealed class LocalGuestLifetime
    {
        private readonly object originalSelf;
        internal LocalGuestLifetime(Record domain, GuestRegionMapping mapping)
        { Domain = domain; Mapping = mapping; originalSelf = this; }
        internal Record Domain { get; }
        internal GuestRegionMapping Mapping { get; }
        internal bool Original => ReferenceEquals(originalSelf, this);
        internal BudgetReservationHandle? Reservation { get; set; }
        internal object? ChargeOwner { get; set; }
        internal bool AdmissionKnown { get; set; }
        internal bool AdmissionInvoked { get; set; }
        internal bool RegionInvocationKnown { get; set; }
        internal bool RegionInvoked { get; set; }
        internal bool RegionReserved { get; set; }
        internal bool RegionReleased { get; set; }
        internal bool ChargeConsumed { get; set; }
        internal PlatformRegionMapping? ParentMapping { get; set; }
        internal bool ParentAdmissionInvoked { get; set; }
        internal bool ParentAdmissionKnown { get; set; }
    }

    private bool TryLocalGuestLifetime(Record record, GuestRegionMapping mapping, out LocalGuestLifetime lifetime) =>
        record.LocalGuestLifetimes.TryGetValue(mapping.Mapping.MappingId, out lifetime!) &&
        lifetime.Original && ReferenceEquals(lifetime.Domain, record) && lifetime.Mapping == mapping &&
        HasCurrentOriginalRecord(record) && HasOriginalMapping(record, mapping) &&
        record.Mappings.TryGetValue(mapping.Mapping.MappingId, out var route) && route == mapping;

    internal KernelResult CommitLocalGuestLifetime(Record record, GuestRegionMapping mapping,
        Func<LocalGuestLifetime, KernelResult> consumer)
    {
        lock (_gate)
            return TryLocalGuestLifetime(record, mapping, out var lifetime) ? consumer(lifetime) :
                KernelResult.Fail(KernelError.PlatformFaulted, "Original local guest lifetime/history changed.");
    }
}
