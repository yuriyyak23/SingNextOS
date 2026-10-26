namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalDeviceLoanProvider, INeutralOriginalDeviceLoanResolutionProvider, INeutralOriginalDeviceLoanNoEffectProvider
{
    private bool OriginalLoanHistory(NeutralDeviceState source) => source.HasIntactOriginalLoanHistory;

    private bool OriginalLoanSources(NeutralOriginalDeviceLoanFact fact, bool active) =>
        ReferenceEquals(fact.OwnerSelf, fact) && _dependencies.Devices.TryGetValue(fact.Source.Handle, out NeutralDeviceState? source) && ReferenceEquals(source, fact.Source) &&
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
            if (!_dependencies.Devices.TryGetValue(request.Device.Handle, out NeutralDeviceState? source) || !OriginalLoanHistory(source)) return null;
            NeutralOriginalDeviceLoanFact[] prior = source.OriginalDeviceLoans.Where(fact => fact.Request.InvocationId == request.InvocationId).ToArray();
            if (prior.Length != 0) return prior.Length == 1 && ReferenceEquals(prior[0].Request, request) && OriginalLoanSources(prior[0], false) ? prior[0].Loan : null;
            if (source.OriginalAdmission is not { } admission || request.Device != source.Lease ||
                admission.Request != request.DeviceAdmission || !OriginalDeviceSources(source, admission, MappingClosureIncarnation, false) ||
                source.OriginalDeviceLoansIssued == ulong.MaxValue) return null;
            bool admitted = _leases.Validate(request.Device).IsValid && source.IsActive && source.OriginalClose is null &&
                OriginalDeviceSources(source, admission, MappingClosureIncarnation);
            source.OriginalDeviceLoans.EnsureCapacity(source.OriginalDeviceLoans.Count + 1);
            NeutralOriginalDeviceLoanFact fact = new NeutralOriginalDeviceLoanFact(request, source, admission, admitted);
            fact.EstablishSelf();
            source.OriginalDeviceLoans.Add(fact);
            source.OriginalDeviceLoansIssued++;
            return fact.Loan; // Refusal retains a no-effect fact, never a placeholder borrower.
        }
    }

    private NeutralOriginalDeviceLoanFact? FindOriginalLoan(NeutralOriginalDeviceLoan loan) =>
        loan?.Request is { } request && _dependencies.Devices.TryGetValue(request.Device.Handle, out NeutralDeviceState? source) && OriginalLoanHistory(source)
            ? source.OriginalDeviceLoans.SingleOrDefault(fact => ReferenceEquals(fact.Loan, loan) && ReferenceEquals(fact.Request, request)) : null;

    public NeutralOriginalDeviceLoan? ResolveOriginalDeviceLoan(NeutralOriginalDeviceLoanRequest originalRequest)
    {
        if (originalRequest is null) return null;
        lock (_lifecycleGate)
        {
            if (!_dependencies.Devices.TryGetValue(originalRequest.Device.Handle, out NeutralDeviceState? source) || !OriginalLoanHistory(source)) return null;
            NeutralOriginalDeviceLoanFact? fact = source.OriginalDeviceLoans.SingleOrDefault(old => ReferenceEquals(old.Request, originalRequest));
            return fact is not null && OriginalLoanSources(fact, false) ? fact.Loan : null;
        }
    }

    public NeutralOriginalDeviceLoanNoEffect? ResolveOriginalDeviceLoanNoEffect(NeutralOriginalDeviceLoanRequest originalRequest)
    {
        if (originalRequest is null) return null;
        lock (_lifecycleGate)
        {
            if (!_dependencies.Devices.TryGetValue(originalRequest.Device.Handle, out NeutralDeviceState? source) || !OriginalLoanHistory(source)) return null;
            NeutralOriginalDeviceLoanFact? fact = source.OriginalDeviceLoans.SingleOrDefault(old => ReferenceEquals(old.Request, originalRequest));
            return fact is not null && OriginalLoanSources(fact, false) && fact.HasExactNoEffect ? fact.NoEffect : null;
        }
    }

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
            if (fact.Consumed || !ReferenceEquals(fact.Closure, fact.OriginalClosure)) return false;
            if (fact.QueryInFlight || !OriginalLoanSources(fact, false)) return false;
            fact.QueryInFlight = true;
        }
        try
        {
            NeutralOriginalDeviceLoanClosure? raw = fact.Closure ?? fact.Request.Lifetime.ResolveOriginalDeviceLoanClosure(loan); // Pure external query outside owner gate.
            lock (_lifecycleGate)
            {
                if (fact.OriginalClosure is { } prior && !ReferenceEquals(prior, raw)) return false;
                fact.OriginalClosure = raw; // Exact lifetime reply retained before local consumption.
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
