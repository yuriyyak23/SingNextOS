using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private bool OriginalSecureRegionBindingCurrent(SecureDomainRecord record, SecureRegionBindInvocationFact fact) =>
        ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Source, record) && IsOriginalSecureOwner(record) &&
        record.RegionBindingOwners.TryGetValue(fact.Mapping.MappingId, out var current) && ReferenceEquals(current, fact) &&
        fact.Observation is { IsSuccess: true } raw && record.Regions.TryGetValue(fact.Mapping.MappingId, out var lease) && lease == raw.Value;

    internal KernelResult<object> CaptureOriginalSecureRegionBinding(SecureDomainBinding binding, PlatformRegionMapping mapping)
    {
        lock (_secureDomainLifecycleGate)
            return _secureDomains.TryGetValue(binding.BindingId, out var record) && record.Binding == binding &&
                record.RegionBindingOwners.TryGetValue(mapping.MappingId, out var fact) && fact.Mapping == mapping && OriginalSecureRegionBindingCurrent(record, fact)
                ? KernelResult<object>.Ok(fact) : KernelResult<object>.Fail(KernelError.PlatformFaulted, "Original secure-region bind fact unavailable.");
    }
    internal KernelResult CommitOriginalSecureRegionBinding(object source, Func<KernelResult> commit)
    {
        lock (_secureDomainLifecycleGate)
            return source is SecureRegionBindInvocationFact fact && OriginalSecureRegionBindingCurrent(fact.Source, fact)
                ? commit() : KernelResult.Fail(KernelError.PlatformFaulted, "Original secure-region bind fact changed before publication.");
    }

    private static void QuarantineSecureClosure(SecureDomainRecord record, SecureClosureInvocationFact fact)
    {
        var own = fact.ValidatedClosed && ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Source, record) &&
            (!record.TerminalQuarantined || ReferenceEquals(record.ClosureQuarantineOwner, fact) &&
                fact.OwnQuarantineRevision == record.TerminalRevision);
        record.TerminalQuarantined = true;
        if (own && !record.TerminalRevisionExhausted)
        {
            record.ClosureQuarantineOwner = fact;
            fact.OwnQuarantineRevision = record.TerminalRevision;
        }
    }

    internal bool HasOriginalSecureRevokeInvocation(SecureDomainBinding binding)
    {
        lock (_secureDomainLifecycleGate)
            return _originalSecureOwners.TryGetValue(binding.BindingId, out var record) &&
                IsOriginalSecureOwner(record) && record.Binding == binding && record.RevokeInvoked;
    }

    private bool OriginalSecureClosureSources(SecureDomainRecord record, SecureClosureInvocationFact fact,
        PlatformProviderIncarnation incarnation) =>
        ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Source, record) && IsOriginalSecureOwner(record) &&
        _secureDomains.TryGetValue(record.Binding.BindingId, out var current) && ReferenceEquals(current, record) &&
        fact.ValidatedClosed && !fact.Consumed && !_backendEpochExhausted && fact.Epoch == BackendEpoch &&
        fact.ParentSource is { } parent && ReferenceEquals(parent, record.CreateSource) &&
        incarnation.Value != 0 && incarnation == parent.ProviderIncarnation && parent.BackendEpoch == fact.Epoch &&
        TryGetCanonicalRootRecord(record.Binding.Parent.BindingId, out var root) && ReferenceEquals(root, parent) &&
        root.ProviderLease == record.ProviderLease.Parent && !root.ParentRevokeMayHaveEffect &&
        ValidateDomain(record.Binding.Parent, record.Binding.Parent.Subject).IsSuccess;

    private static bool OwnSecureClosureFaultCurrent(SecureDomainRecord record, SecureClosureInvocationFact fact) =>
        !record.TerminalRevisionExhausted && ReferenceEquals(record.ClosureQuarantineOwner, fact) &&
        fact.OwnQuarantineRevision == record.TerminalRevision;

    private bool CompletedSecureClosureCurrent(SecureDomainRecord record, SecureClosureInvocationFact fact) =>
        IsOriginalSecureOwner(record) && ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Source, record) &&
        fact.ValidatedClosed && fact.Consumed && (fact switch
        {
            SecureDomainRevokeInvocationFact domain => record.ClosureConsumed && ReferenceEquals(record.RevokeInvocation, domain) &&
                domain.AdmittedLease == record.ProviderLease && domain.Observation is { IsSuccess: true } raw &&
                raw.Value.Domain == domain.AdmittedLease && raw.Value.Closed,
            SecureRegionClosureInvocationFact region => ReferenceEquals(region.BindingSource.OriginalOwner, region.BindingSource) &&
                ReferenceEquals(region.BindingSource.Source, record) && region.BindingSource.Observation is { IsSuccess: true } bindRaw && bindRaw.Value == region.AdmittedBinding && record.RegionClosureInvocations.Any(x => ReferenceEquals(x, region)) &&
                region.Observation is { IsSuccess: true } raw && raw.Value.Binding == region.AdmittedBinding && raw.Value.Closed &&
                _mappings.TryGetValue(region.MappingSource.Mapping.MappingId, out var mapping) && ReferenceEquals(mapping, region.MappingSource),
            _ => false
        });

    private KernelResult<bool> ConsumeCompletedSecureClosure(SecureDomainRecord record, SecureClosureInvocationFact fact,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? consumer)
    {
        var region = fact as SecureRegionClosureInvocationFact;
        lock (_secureDomainLifecycleGate)
        {
            if (!CompletedSecureClosureCurrent(record, fact))
                return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Completed original secure closure fact is unavailable.");
            if (record.RevokeInFlight || record.TransitionInFlight || record.RegionMutationsInFlight.Count != 0)
                return KernelResult<bool>.Fail(KernelError.PlatformBindingActive, "Original secure closure completion is in flight.");
            if (region is null) record.RevokeInFlight = true;
            else record.RegionMutationsInFlight.Add(region.MappingSource.Mapping.MappingId);
        }
        try
        {
            var committed = false;
            KernelResult Commit(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!CompletedSecureClosureCurrent(record, fact))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Completed original closure owner changed before local handoff.");
                    var local = commit();
                    committed = local.IsSuccess;
                    return local;
                }
            }
            var result = consumer is null ? Commit(KernelResult.Ok) : consumer(Commit);
            return result.IsSuccess && committed ? KernelResult<bool>.Ok(true) : KernelResult<bool>.Fail(
                result.IsSuccess ? KernelError.PlatformFaulted : result.Error, result.Message ?? "Completed original closure handoff was not consumed.");
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        { return KernelResult<bool>.Fail(KernelError.PlatformFaulted, $"Completed original closure handoff failed: {exception.Message}"); }
        finally
        {
            lock (_secureDomainLifecycleGate)
                if (region is null) record.RevokeInFlight = false;
                else record.RegionMutationsInFlight.Remove(region.MappingSource.Mapping.MappingId);
        }
    }

    internal KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareOriginalSecureDomainClosureCompletion(SecureDomainBinding binding) =>
        PrepareOriginalSecureClosureCompletion(binding, null);
    internal KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareOriginalSecureRegionClosureCompletion(SecureDomainBinding binding, PlatformRegionMapping mapping, object? originalBindingSource = null) =>
        PrepareOriginalSecureClosureCompletion(binding, mapping, originalBindingSource);

    private KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareOriginalSecureClosureCompletion(SecureDomainBinding binding, PlatformRegionMapping? mapping, object? originalBindingSource = null)
    {
        SecureDomainRecord record;
        SecureClosureInvocationFact fact;
        bool completed;
        lock (_secureDomainLifecycleGate)
        {
            if (!_originalSecureOwners.TryGetValue(binding.BindingId, out record!) || !IsOriginalSecureOwner(record) || record.Binding != binding)
                return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.PlatformFaulted, "Original secure closure owner is unavailable.");
            if (mapping is { } region)
            {
                var candidates = record.RegionClosureInvocations.Where(x => x.MappingSource.Mapping == region &&
                    (originalBindingSource is not null ? ReferenceEquals(x.BindingSource, originalBindingSource) :
                    !record.Regions.TryGetValue(region.MappingId, out var current) || x.AdmittedBinding == current)).ToArray();
                if (candidates.Length != 1)
                    return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.PlatformFaulted, "Original secure-region closure correlation is ambiguous.");
                fact = candidates[0];
            }
            else if (record.RevokeInvocation is { } revoke) fact = revoke;
            else return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.PlatformFaulted, "Original secure closure invocation is unavailable.");
            completed = fact.Consumed;
        }
        PlatformProviderIncarnation source = default;
        if (!completed)
        {
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.PlatformFaulted, "Original secure completion source unavailable."); }
        }
        bool Current() => completed ? CompletedSecureClosureCurrent(record, fact) :
            OriginalSecureClosureSources(record, fact, source) && !record.TerminalQuarantined &&
            (fact is SecureDomainRevokeInvocationFact domain ? ReferenceEquals(record.RevokeInvocation, domain) &&
                domain.Observation is { IsSuccess: true } raw && raw.Value.Domain == domain.AdmittedLease && raw.Value.Closed :
                fact is SecureRegionClosureInvocationFact region && OriginalSecureRegionBindingCurrent(record, region.BindingSource) &&
                record.RegionClosureInvocations.Any(x => ReferenceEquals(x, region)) &&
                region.Observation is { IsSuccess: true } regionRaw && regionRaw.Value.Binding == region.AdmittedBinding && regionRaw.Value.Closed &&
                _mappings.TryGetValue(region.MappingSource.Mapping.MappingId, out var originalMapping) && ReferenceEquals(originalMapping, region.MappingSource));
        lock (_secureDomainLifecycleGate)
        {
            if (!Current()) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.PlatformFaulted, "Original closure completion continuity changed.");
            KernelResult Commit(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                    return Current() ? commit() : KernelResult.Fail(KernelError.PlatformFaulted, "Original closure owner changed before exact local completion.");
            }
            return KernelResult<Func<Func<KernelResult>, KernelResult>>.Ok(Commit);
        }
    }

    private KernelResult<bool> ConsumeOriginalSecureDomainClosure(SecureDomainBinding binding,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? consumer)
    {
        SecureDomainRecord? completedRecord;
        lock (_secureDomainLifecycleGate)
            completedRecord = _originalSecureOwners.TryGetValue(binding.BindingId, out var candidate) &&
                IsOriginalSecureOwner(candidate) && candidate.Binding == binding && candidate.ClosureConsumed ? candidate : null;
        if (completedRecord?.RevokeInvocation is { } completedFact)
            return ConsumeCompletedSecureClosure(completedRecord, completedFact, consumer);
        SecureDomainRecord record;
        SecureDomainRevokeInvocationFact fact;
        lock (_secureDomainLifecycleGate)
        {
            var resolved = ResolveSecure(binding);
            if (!resolved.IsSuccess) return KernelResult<bool>.Fail(resolved.Error, resolved.Message!);
            record = resolved.Value!;
            if (!record.RevokeInvoked) return KernelResult<bool>.Ok(false);
            if (record.RevokeInFlight || record.TransitionInFlight || record.RegionMutationsInFlight.Count != 0)
                return KernelResult<bool>.Fail(KernelError.PlatformBindingActive, "Original secure closure must settle before consumption.");
            if (record.RevokeInvocation is not { ValidatedClosed: true } original)
                return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Original secure revoke outcome is unavailable; effect cannot repeat.");
            fact = original;
        }
        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Original secure closure source unavailable."); }
        bool Current() => OriginalSecureClosureSources(record, fact, source) && ReferenceEquals(record.RevokeInvocation, fact) &&
            fact.AdmittedLease == record.ProviderLease && fact.Observation is { IsSuccess: true } raw &&
            raw.Value.Domain == fact.AdmittedLease && raw.Value.Closed && record.Regions.Count == 0 &&
            record.QuarantinedRegions.Count == 0 && OwnSecureClosureFaultCurrent(record, fact);
        lock (_secureDomainLifecycleGate)
        {
            if (!Current()) return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Original known secure closure continuity changed.");
            record.RevokeInFlight = true;
            record.TerminalQuarantined = false;
            fact.OwnQuarantineRevision = record.TerminalRevision;
        }
        try
        {
            var committed = false;
            KernelResult Commit(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!Current() || record.TerminalQuarantined)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Known secure closure owner changed before exact consumption.");
                    var local = commit();
                    if (!local.IsSuccess) return local;
                    fact.Consumed = true; record.ClosureConsumed = true;
                    record.ClosureQuarantineOwner = null;
                    _secureDomains.Remove(binding.BindingId);
                    committed = true;
                    return KernelResult.Ok();
                }
            }
            var result = consumer is null ? Commit(KernelResult.Ok) : consumer(Commit);
            lock (_secureDomainLifecycleGate)
            {
                if (!result.IsSuccess || !committed)
                {
                    QuarantineSecureClosure(record, fact);
                    return KernelResult<bool>.Fail(result.IsSuccess ? KernelError.PlatformFaulted : result.Error,
                        result.Message ?? "Original closure was not consumed locally.");
                }
            }
            return KernelResult<bool>.Ok(true);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            lock (_secureDomainLifecycleGate) QuarantineSecureClosure(record, fact);
            return KernelResult<bool>.Fail(KernelError.PlatformFaulted, $"Original known secure closure local continuation failed: {exception.Message}");
        }
        finally { lock (_secureDomainLifecycleGate) record.RevokeInFlight = false; }
    }

    private KernelResult<bool> ConsumeOriginalSecureRegionClosure(SecureDomainBinding binding, PlatformRegionMapping mapping,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? consumer, object? originalBindingSource = null)
    {
        SecureDomainRecord? completedRecord = null;
        SecureRegionClosureInvocationFact? completedFact = null;
        lock (_secureDomainLifecycleGate)
        {
            if (_originalSecureOwners.TryGetValue(binding.BindingId, out var candidate) && IsOriginalSecureOwner(candidate) &&
                candidate.Binding == binding && (originalBindingSource is not null || !candidate.Regions.ContainsKey(mapping.MappingId)))
            {
                var originals = candidate.RegionClosureInvocations.Where(x => x.Consumed && x.MappingSource.Mapping == mapping &&
                    (originalBindingSource is null || ReferenceEquals(x.BindingSource, originalBindingSource))).ToArray();
                if (originals.Length == 1) { completedRecord = candidate; completedFact = originals[0]; }
            }
        }
        if (completedRecord is not null) return ConsumeCompletedSecureClosure(completedRecord, completedFact!, consumer);
        SecureDomainRecord record;
        SecureRegionClosureInvocationFact fact;
        PlatformProviderSecureRegionBinding admitted;
        lock (_secureDomainLifecycleGate)
        {
            var resolved = ResolveSecure(binding);
            if (!resolved.IsSuccess) return KernelResult<bool>.Fail(resolved.Error, resolved.Message!);
            record = resolved.Value!;
            if (originalBindingSource is not null && (!record.RegionBindingOwners.TryGetValue(mapping.MappingId, out var bindingOwner) ||
                !ReferenceEquals(bindingOwner, originalBindingSource) || !OriginalSecureRegionBindingCurrent(record, bindingOwner)))
                return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Original secure-region lifetime changed; foreign binding cannot be closed.");
            if (!record.Regions.TryGetValue(mapping.MappingId, out admitted) || (!record.RegionBindingOwners.TryGetValue(mapping.MappingId, out var currentBindingOwner) || !record.RegionCloseInvoked.Contains(currentBindingOwner)))
                return KernelResult<bool>.Ok(false);
            if (record.TransitionInFlight || record.RevokeInFlight || record.RegionMutationsInFlight.Count != 0)
                return KernelResult<bool>.Fail(KernelError.PlatformBindingActive, "Original secure-region closure must settle before consumption.");
            var originals = record.RegionClosureInvocations.Where(x => !x.Consumed && x.AdmittedBinding == admitted && ReferenceEquals(x.BindingSource, currentBindingOwner)).ToArray();
            if (originals.Length != 1 || !originals[0].ValidatedClosed)
                return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Original secure-region closure is unknown; effect cannot repeat.");
            fact = originals[0];
        }
        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Original secure-region closure source unavailable."); }
        bool Current() => OriginalSecureClosureSources(record, fact, source) &&
            record.RegionClosureInvocations.Any(x => ReferenceEquals(x, fact)) && OriginalSecureRegionBindingCurrent(record, fact.BindingSource) &&
            _mappings.TryGetValue(mapping.MappingId, out var mapped) && ReferenceEquals(mapped, fact.MappingSource) &&
            mapped.Mapping == mapping && mapped.BackendEpoch == fact.Epoch && mapped.ProviderIncarnation == source &&
            record.RegionMappingSources.TryGetValue(mapping.MappingId, out var originalMapping) && ReferenceEquals(originalMapping, mapped) &&
            record.Regions.TryGetValue(mapping.MappingId, out var exact) && exact == admitted &&
            fact.Observation is { IsSuccess: true } raw && raw.Value.Binding == admitted && raw.Value.Closed &&
            !record.QuarantinedRegions.Any(id => id != mapping.MappingId) && OwnSecureClosureFaultCurrent(record, fact);
        lock (_secureDomainLifecycleGate)
        {
            if (!Current()) return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Original known secure-region closure continuity changed.");
            record.RegionMutationsInFlight.Add(mapping.MappingId);
            record.TerminalQuarantined = false;
            fact.OwnQuarantineRevision = record.TerminalRevision;
            record.QuarantinedRegions.Remove(mapping.MappingId);
        }
        try
        {
            var committed = false;
            KernelResult Commit(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!Current() || record.TerminalQuarantined)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original secure-region closure changed before exact consumption.");
                    var local = commit();
                    if (!local.IsSuccess) return local;
                    record.Regions.Remove(mapping.MappingId);
                    record.RegionBindingOwners.Remove(mapping.MappingId);
                    record.RegionMappingSources.Remove(mapping.MappingId);
                    record.QuarantinedRegions.Remove(mapping.MappingId);
                    fact.Consumed = true;
                    record.ClosureQuarantineOwner = null;
                    committed = true;
                    return KernelResult.Ok();
                }
            }
            var result = consumer is null ? Commit(KernelResult.Ok) : consumer(Commit);
            lock (_secureDomainLifecycleGate)
            {
                if (!result.IsSuccess || !committed)
                {
                    QuarantineSecureClosure(record, fact); record.QuarantinedRegions.Add(mapping.MappingId);
                    return KernelResult<bool>.Fail(result.IsSuccess ? KernelError.PlatformFaulted : result.Error,
                        result.Message ?? "Original region closure was not consumed locally.");
                }
            }
            return KernelResult<bool>.Ok(true);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            lock (_secureDomainLifecycleGate)
            { QuarantineSecureClosure(record, fact); record.QuarantinedRegions.Add(mapping.MappingId); }
            return KernelResult<bool>.Fail(KernelError.PlatformFaulted, $"Original known secure-region local continuation failed: {exception.Message}");
        }
        finally { lock (_secureDomainLifecycleGate) record.RegionMutationsInFlight.Remove(mapping.MappingId); }
    }
}
