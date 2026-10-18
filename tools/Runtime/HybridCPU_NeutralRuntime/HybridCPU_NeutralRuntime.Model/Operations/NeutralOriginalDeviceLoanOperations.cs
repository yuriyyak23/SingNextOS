namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalDeviceLoanProvider
{
    private bool OriginalLoanHistory(NeutralDeviceState source) =>
        (ulong)source.OriginalDeviceLoans.Count == source.OriginalDeviceLoansIssued &&
        source.OriginalDeviceLoans.All(fact => ReferenceEquals(fact.OwnerSelf, fact) && ReferenceEquals(fact.Source, source));

    private bool OriginalLoanSources(NeutralOriginalDeviceLoanFact fact, bool active) =>
        ReferenceEquals(fact.OwnerSelf, fact) && _dependencies.Devices.TryGetValue(fact.Source.Handle, out var source) && ReferenceEquals(source, fact.Source) &&
        OriginalLoanHistory(source) && source.OriginalDeviceLoans.Any(old => ReferenceEquals(old, fact)) &&
        fact.Request.DeviceAdmission == fact.Admission.Request && fact.Request.Device == source.Lease &&
        fact.Request.DeviceAdmission.RuntimeIncarnation == MappingClosureIncarnation &&
        OriginalDeviceSources(source, fact.Admission, MappingClosureIncarnation, active) && (!active || source.IsActive);

    public NeutralOriginalDeviceLoan? AcquireOriginalDeviceLoan(NeutralOriginalDeviceLoanRequest request)
    {
        if (request is null || request.InvocationId == Guid.Empty || request.Lifetime is null || request.Rights == NeutralDeviceRights.None ||
            (request.Rights & ~request.Device.Rights) != 0) return null;
        lock (_lifecycleGate)
        {
            if (!_dependencies.Devices.TryGetValue(request.Device.Handle, out var source) || !OriginalLoanHistory(source)) return null;
            var prior = source.OriginalDeviceLoans.Where(fact => fact.Request.InvocationId == request.InvocationId).ToArray();
            if (prior.Length != 0) return prior.Length == 1 && ReferenceEquals(prior[0].Request, request) && OriginalLoanSources(prior[0], false) ? prior[0].Loan : null;
            if (!_leases.Validate(request.Device).IsValid || source.OriginalAdmission is not { } admission ||
                admission.Request != request.DeviceAdmission || !OriginalDeviceSources(source, admission, MappingClosureIncarnation) || source.OriginalClose is not null ||
                source.OriginalDeviceLoansIssued == ulong.MaxValue) return null;
            source.OriginalDeviceLoans.EnsureCapacity(source.OriginalDeviceLoans.Count + 1);
            var fact = new NeutralOriginalDeviceLoanFact(request, source, admission);
            fact.EstablishSelf();
            source.OriginalDeviceLoans.Add(fact);
            source.OriginalDeviceLoansIssued++;
            return fact.Loan; // Native device/root Close now sees this original borrower.
        }
    }

    private NeutralOriginalDeviceLoanFact? FindOriginalLoan(NeutralOriginalDeviceLoan loan) =>
        loan?.Request is { } request && _dependencies.Devices.TryGetValue(request.Device.Handle, out var source) && OriginalLoanHistory(source)
            ? source.OriginalDeviceLoans.SingleOrDefault(fact => ReferenceEquals(fact.Loan, loan) && ReferenceEquals(fact.Request, request)) : null;

    public bool ValidateOriginalDeviceLoan(NeutralOriginalDeviceLoan loan)
    {
        lock (_lifecycleGate) return FindOriginalLoan(loan) is { } fact && !fact.Consumed && OriginalLoanSources(fact, true);
    }

    public bool RetireOriginalDeviceLoan(NeutralOriginalDeviceLoan loan)
    {
        NeutralOriginalDeviceLoanFact fact;
        lock (_lifecycleGate)
        {
            if (FindOriginalLoan(loan) is not { } original) return false;
            fact = original;
            if (fact.IsConsumed) return true;
            if (fact.QueryInFlight || !OriginalLoanSources(fact, false)) return false;
            fact.QueryInFlight = true;
        }
        try
        {
            var raw = fact.Closure ?? fact.Request.Lifetime.ResolveOriginalDeviceLoanClosure(loan); // Pure external query outside owner gate.
            lock (_lifecycleGate)
            {
                fact.Closure = raw;
                if (!OriginalLoanSources(fact, false) || raw is not { NoFurtherUse: true } || !ReferenceEquals(raw.Loan, loan)) return false;
                fact.Consumed = true;
                return true;
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException) { return false; }
        finally { lock (_lifecycleGate) fact.QueryInFlight = false; }
    }
}
