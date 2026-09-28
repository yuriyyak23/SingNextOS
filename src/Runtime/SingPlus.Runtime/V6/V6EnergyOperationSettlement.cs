using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal readonly record struct V6EnergyCounterToken(
    string ProviderIdentity,
    string OperationCorrelation,
    ulong ProviderGeneration,
    ulong CounterGeneration,
    ulong CounterStart);

internal interface IV6EnergyCounterProvider
{
    string ProviderIdentity { get; }
    ulong ProviderGeneration { get; }
    ulong CounterGeneration { get; }
    KernelResult<V6EnergyCounterToken> Begin(string operationCorrelation);
    KernelResult Cancel(V6EnergyCounterToken token);
    KernelResult<ProviderEnergyEvidenceV1> Complete(
        V6EnergyCounterToken token,
        ulong measuredMicrojoules,
        ThermalStateV1 thermalState,
        DvfsStateV1 dvfsState);
}

internal interface IV6EnergyCapProvider : IV6EnergyCounterProvider
{
    KernelResult<V6EnergyCounterToken> BeginEnforced(
        string operationCorrelation, ulong maximumEnergyMicrojoules);
}

/// <summary>Deterministic measurement-only provider used by the managed P12 contour.</summary>
internal sealed class V6ManagedEnergyCounterProvider : IV6EnergyCounterProvider
{
    internal const string Identity = "singnext.managed-model-energy-counter";
    private readonly object _sync = new();
    private readonly Dictionary<string, V6EnergyCounterToken> _active = new(StringComparer.Ordinal);
    private ulong _providerGeneration = 1;
    private ulong _counterGeneration = 1;
    private ulong _counter;
    private ulong _evidenceSequence = 1;

    public string ProviderIdentity => Identity;
    public ulong ProviderGeneration { get { lock (_sync) return _providerGeneration; } }
    public ulong CounterGeneration { get { lock (_sync) return _counterGeneration; } }

    public KernelResult<V6EnergyCounterToken> Begin(string operationCorrelation)
    {
        if (!Canonical(operationCorrelation))
            return KernelResult<V6EnergyCounterToken>.Fail(KernelError.InvalidMessage, "Energy operation correlation is not canonical.");
        lock (_sync)
        {
            if (_active.ContainsKey(operationCorrelation))
                return KernelResult<V6EnergyCounterToken>.Fail(KernelError.InvalidTransition, "Energy operation correlation is already active.");
            var token = new V6EnergyCounterToken(Identity, operationCorrelation,
                _providerGeneration, _counterGeneration, _counter);
            _active.Add(operationCorrelation, token);
            return KernelResult<V6EnergyCounterToken>.Ok(token);
        }
    }

    public KernelResult Cancel(V6EnergyCounterToken token)
    {
        lock (_sync)
        {
            if (!_active.TryGetValue(token.OperationCorrelation, out var current) || current != token)
                return KernelResult.Fail(KernelError.StaleGeneration, "Energy counter token is stale or already closed.");
            _active.Remove(token.OperationCorrelation);
            return KernelResult.Ok();
        }
    }

    public KernelResult<ProviderEnergyEvidenceV1> Complete(
        V6EnergyCounterToken token,
        ulong measuredMicrojoules,
        ThermalStateV1 thermalState,
        DvfsStateV1 dvfsState)
    {
        lock (_sync)
        {
            if (!_active.TryGetValue(token.OperationCorrelation, out var current) || current != token ||
                token.ProviderGeneration != _providerGeneration || token.CounterGeneration != _counterGeneration)
                return KernelResult<ProviderEnergyEvidenceV1>.Fail(KernelError.StaleGeneration,
                    "Energy provider or counter generation changed before measurement completion.");
            if (_evidenceSequence == 0 || measuredMicrojoules > ulong.MaxValue - _counter)
                return KernelResult<ProviderEnergyEvidenceV1>.Fail(KernelError.CapacityExhausted,
                    "Energy counter or evidence sequence cannot advance without rollover.");
            var end = _counter + measuredMicrojoules;
            ProviderEnergyEvidenceV1 evidence;
            try
            {
                evidence = new ProviderEnergyEvidenceV1(1, Identity, token.OperationCorrelation,
                    EnergyEvidenceClaimV1.MeasurementOnly, EnergyEvidenceAssuranceV1.ModelOnly,
                    _providerGeneration, _counterGeneration, _evidenceSequence, token.CounterStart, end,
                    measuredMicrojoules, 0, 0, 1_000_000, thermalState, dvfsState, string.Empty).Validate();
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            { return KernelResult<ProviderEnergyEvidenceV1>.Fail(KernelError.InvalidMessage, exception.Message); }
            _counter = end;
            _evidenceSequence++;
            _active.Remove(token.OperationCorrelation);
            return KernelResult<ProviderEnergyEvidenceV1>.Ok(evidence);
        }
    }

    internal KernelResult ResetCounter()
    {
        lock (_sync)
        {
            if (_counterGeneration == ulong.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Energy counter generation is exhausted.");
            _counterGeneration++;
            _counter = 0;
            _active.Clear();
            return KernelResult.Ok();
        }
    }

    internal KernelResult ResetProvider()
    {
        lock (_sync)
        {
            if (_providerGeneration == ulong.MaxValue || _counterGeneration == ulong.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Energy provider generation is exhausted.");
            _providerGeneration++;
            _counterGeneration++;
            _counter = 0;
            _active.Clear();
            return KernelResult.Ok();
        }
    }

    internal void SetCounterForTest(ulong value) { lock (_sync) _counter = value; }

    private static bool Canonical(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 256 && !value.Any(char.IsControl);
}

/// <summary>
/// Deterministic provider that admits an exact per-operation cap before execution and
/// emits enforced-upper-bound evidence. This is model enforcement, not physical power control.
/// </summary>
internal sealed class V6ManagedEnergyCapProvider : IV6EnergyCapProvider
{
    internal const string Identity = "singnext.managed-model-energy-cap";
    internal const string Mechanism = "managed-operation-energy-envelope-v1";
    private readonly object sync = new();
    private readonly Dictionary<string, (V6EnergyCounterToken Token, ulong Cap)> active =
        new(StringComparer.Ordinal);
    private ulong providerGeneration = 1;
    private ulong counterGeneration = 1;
    private ulong counter;
    private ulong evidenceSequence = 1;

    public string ProviderIdentity => Identity;
    public ulong ProviderGeneration { get { lock (sync) return providerGeneration; } }
    public ulong CounterGeneration { get { lock (sync) return counterGeneration; } }

    public KernelResult<V6EnergyCounterToken> Begin(string operationCorrelation) =>
        KernelResult<V6EnergyCounterToken>.Fail(KernelError.PlatformUnsupported,
            "The managed cap provider requires an explicit enforced upper bound.");

    public KernelResult<V6EnergyCounterToken> BeginEnforced(
        string operationCorrelation, ulong maximumEnergyMicrojoules)
    {
        if (!Canonical(operationCorrelation) || maximumEnergyMicrojoules == 0)
            return KernelResult<V6EnergyCounterToken>.Fail(KernelError.InvalidMessage,
                "An enforced energy operation requires a canonical correlation and non-zero cap.");
        lock (sync)
        {
            if (active.ContainsKey(operationCorrelation))
                return KernelResult<V6EnergyCounterToken>.Fail(KernelError.InvalidTransition,
                    "The enforced energy operation is already active.");
            var token = new V6EnergyCounterToken(Identity, operationCorrelation,
                providerGeneration, counterGeneration, counter);
            active.Add(operationCorrelation, (token, maximumEnergyMicrojoules));
            return KernelResult<V6EnergyCounterToken>.Ok(token);
        }
    }

    public KernelResult Cancel(V6EnergyCounterToken token)
    {
        lock (sync)
        {
            if (!active.TryGetValue(token.OperationCorrelation, out var current) || current.Token != token)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "The enforced energy token is stale or already closed.");
            active.Remove(token.OperationCorrelation);
            return KernelResult.Ok();
        }
    }

    public KernelResult<ProviderEnergyEvidenceV1> Complete(
        V6EnergyCounterToken token, ulong measuredMicrojoules,
        ThermalStateV1 thermalState, DvfsStateV1 dvfsState)
    {
        lock (sync)
        {
            if (!active.TryGetValue(token.OperationCorrelation, out var current) || current.Token != token ||
                token.ProviderGeneration != providerGeneration || token.CounterGeneration != counterGeneration)
                return KernelResult<ProviderEnergyEvidenceV1>.Fail(KernelError.StaleGeneration,
                    "The energy-cap provider or counter generation changed before completion.");
            if (measuredMicrojoules > current.Cap)
            {
                active.Remove(token.OperationCorrelation);
                return KernelResult<ProviderEnergyEvidenceV1>.Fail(KernelError.BudgetExceeded,
                    "The managed provider detected an operation beyond its admitted energy cap.");
            }
            if (evidenceSequence == 0 || measuredMicrojoules > ulong.MaxValue - counter)
                return KernelResult<ProviderEnergyEvidenceV1>.Fail(KernelError.CapacityExhausted,
                    "The energy-cap counter or evidence sequence cannot advance without rollover.");
            var end = counter + measuredMicrojoules;
            var evidence = new ProviderEnergyEvidenceV1(1, Identity, token.OperationCorrelation,
                EnergyEvidenceClaimV1.EnforcedUpperBound, EnergyEvidenceAssuranceV1.ModelOnly,
                providerGeneration, counterGeneration, evidenceSequence, token.CounterStart, end,
                measuredMicrojoules, current.Cap, 0, 1_000_000, thermalState, dvfsState,
                Mechanism).Validate();
            counter = end;
            evidenceSequence++;
            active.Remove(token.OperationCorrelation);
            return KernelResult<ProviderEnergyEvidenceV1>.Ok(evidence);
        }
    }

    internal KernelResult ResetProvider()
    {
        lock (sync)
        {
            if (providerGeneration == ulong.MaxValue || counterGeneration == ulong.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted,
                    "The energy-cap provider generation is exhausted.");
            providerGeneration++;
            counterGeneration++;
            counter = 0;
            active.Clear();
            return KernelResult.Ok();
        }
    }

    private static bool Canonical(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() &&
        value.Length <= 256 && !value.Any(char.IsControl);
}

internal sealed record V6EnergyOperationBinding(
    EnergyExecutionBindingV1 Energy,
    V6EnergyCounterToken CounterToken);

internal sealed record V6EnergySettlementReceipt(
    EnergyExecutionBindingV1 Binding,
    ProviderEnergyEvidenceV1 Evidence,
    BudgetReservationSnapshot Settlement)
{
    internal bool AuthorizesExecution => false;
}

internal static class V6EnergyOperationSettlement
{
    internal static KernelResult<V6EnergyOperationBinding> Begin(
        ResourceBudgetAuthority budgets,
        ProcessHandle owner,
        BudgetReservationHandle reservation,
        string operationCorrelation,
        ulong maximumEnergyMicrojoules,
        IV6EnergyCounterProvider provider)
        => BeginCore(budgets, owner, reservation, operationCorrelation,
            maximumEnergyMicrojoules, provider, provider.Begin);

    internal static KernelResult<V6EnergyOperationBinding> BeginEnforced(
        ResourceBudgetAuthority budgets,
        ProcessHandle owner,
        BudgetReservationHandle reservation,
        string operationCorrelation,
        ulong maximumEnergyMicrojoules,
        IV6EnergyCapProvider provider)
        => BeginCore(budgets, owner, reservation, operationCorrelation,
            maximumEnergyMicrojoules, provider,
            correlation => provider.BeginEnforced(correlation, maximumEnergyMicrojoules));

    private static KernelResult<V6EnergyOperationBinding> BeginCore(
        ResourceBudgetAuthority budgets,
        ProcessHandle owner,
        BudgetReservationHandle reservation,
        string operationCorrelation,
        ulong maximumEnergyMicrojoules,
        IV6EnergyCounterProvider provider,
        Func<string, KernelResult<V6EnergyCounterToken>> begin)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentNullException.ThrowIfNull(provider);
        var snapshot = budgets.Query(reservation);
        if (!snapshot.IsSuccess)
            return KernelResult<V6EnergyOperationBinding>.Fail(snapshot.Error, snapshot.Message!);
        if (snapshot.Value!.Owner != owner || snapshot.Value.State != BudgetReservationState.Bound ||
            snapshot.Value.Lifetime != BudgetReservationLifetime.ExternalEffect ||
            snapshot.Value.Amounts.SingleOrDefault(static amount => amount.Dimension == ServiceBudgetDimension.EnergyMicrojoules).Amount < maximumEnergyMicrojoules ||
            maximumEnergyMicrojoules == 0)
            return KernelResult<V6EnergyOperationBinding>.Fail(KernelError.PlatformDenied,
                "Energy operation requires the exact bound external-effect reservation envelope.");

        var token = begin(operationCorrelation);
        if (!token.IsSuccess)
            return KernelResult<V6EnergyOperationBinding>.Fail(token.Error, token.Message!);
        if (token.Value.ProviderIdentity != provider.ProviderIdentity ||
            token.Value.OperationCorrelation != operationCorrelation ||
            token.Value.ProviderGeneration == 0 || token.Value.CounterGeneration == 0 ||
            token.Value.ProviderGeneration != provider.ProviderGeneration ||
            token.Value.CounterGeneration != provider.CounterGeneration)
        {
            _ = provider.Cancel(token.Value);
            return KernelResult<V6EnergyOperationBinding>.Fail(KernelError.PlatformFaulted,
                "Energy provider returned a malformed or stale counter token.");
        }
        var consuming = budgets.BeginConsumption(owner, reservation);
        if (!consuming.IsSuccess)
        {
            _ = provider.Cancel(token.Value);
            return KernelResult<V6EnergyOperationBinding>.Fail(consuming.Error, consuming.Message!);
        }
        EnergyExecutionBindingV1 binding;
        try
        {
            binding = new EnergyExecutionBindingV1(1, reservation, provider.ProviderIdentity,
                operationCorrelation, maximumEnergyMicrojoules,
                token.Value.ProviderGeneration, token.Value.CounterGeneration).Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            _ = budgets.QuarantineLease(owner, reservation);
            return KernelResult<V6EnergyOperationBinding>.Fail(KernelError.InvalidMessage, exception.Message);
        }
        return KernelResult<V6EnergyOperationBinding>.Ok(new(binding, token.Value));
    }

    internal static KernelResult<V6EnergySettlementReceipt> CompleteAndSettle(
        ResourceBudgetAuthority budgets,
        ProcessHandle owner,
        V6EnergyOperationBinding operation,
        IV6EnergyCounterProvider provider,
        ulong measuredMicrojoules,
        ThermalStateV1 thermalState,
        DvfsStateV1 dvfsState)
        => CompleteAndSettleCore(budgets, owner, operation, provider,
            measuredMicrojoules, thermalState, dvfsState, requireEnforcedUpperBound: false);

    internal static KernelResult<V6EnergySettlementReceipt> CompleteAndSettleEnforced(
        ResourceBudgetAuthority budgets,
        ProcessHandle owner,
        V6EnergyOperationBinding operation,
        IV6EnergyCapProvider provider,
        ulong measuredMicrojoules,
        ThermalStateV1 thermalState,
        DvfsStateV1 dvfsState)
        => CompleteAndSettleCore(budgets, owner, operation, provider,
            measuredMicrojoules, thermalState, dvfsState, requireEnforcedUpperBound: true);

    private static KernelResult<V6EnergySettlementReceipt> CompleteAndSettleCore(
        ResourceBudgetAuthority budgets,
        ProcessHandle owner,
        V6EnergyOperationBinding operation,
        IV6EnergyCounterProvider provider,
        ulong measuredMicrojoules,
        ThermalStateV1 thermalState,
        DvfsStateV1 dvfsState,
        bool requireEnforcedUpperBound)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentNullException.ThrowIfNull(provider);
        var binding = operation.Energy.Validate();
        var budgetBefore = budgets.Query(binding.Reservation);
        if (!budgetBefore.IsSuccess || budgetBefore.Value!.Owner != owner ||
            budgetBefore.Value.State != BudgetReservationState.Consuming)
            return KernelResult<V6EnergySettlementReceipt>.Fail(KernelError.StaleGeneration,
                "The exact energy budget lease is no longer consuming before provider completion.");
        if (provider.ProviderIdentity != binding.ProviderIdentity ||
            provider.ProviderGeneration != binding.ProviderGeneration ||
            provider.CounterGeneration != binding.CounterGeneration)
            return Quarantine(KernelError.StaleGeneration, "Energy provider identity or generation changed before settlement.");

        KernelResult<ProviderEnergyEvidenceV1> completed;
        try { completed = provider.Complete(operation.CounterToken, measuredMicrojoules, thermalState, dvfsState); }
        catch (Exception exception)
        { return Quarantine(KernelError.PlatformFaulted, $"Energy provider failed during completion: {exception.Message}"); }
        if (!completed.IsSuccess)
            return Quarantine(completed.Error, completed.Message!);
        if (provider.ProviderIdentity != binding.ProviderIdentity ||
            provider.ProviderGeneration != binding.ProviderGeneration ||
            provider.CounterGeneration != binding.CounterGeneration)
            return Quarantine(KernelError.StaleGeneration,
                "Energy provider or counter generation changed during completion.");
        ProviderEnergyEvidenceV1 evidence;
        try { evidence = completed.Value.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return Quarantine(KernelError.PlatformFaulted, exception.Message); }
        if (requireEnforcedUpperBound)
        {
            EnergyEvidenceMatchCodeV1 match;
            try { match = EnergyEvidenceMatcherV1.Match(binding, evidence); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            { return Quarantine(KernelError.PlatformFaulted, exception.Message); }
            if (match != EnergyEvidenceMatchCodeV1.Exact)
                return Quarantine(match == EnergyEvidenceMatchCodeV1.ExceedsReservedEnvelope
                        ? KernelError.BudgetExceeded : KernelError.PlatformDenied,
                    $"Enforced energy evidence did not match the exact admitted cap: {match}.");
        }
        if (evidence.ProviderIdentity != binding.ProviderIdentity || evidence.OperationCorrelation != binding.OperationCorrelation ||
            evidence.ProviderGeneration != binding.ProviderGeneration || evidence.CounterGeneration != binding.CounterGeneration ||
            evidence.MeasuredMicrojoules > binding.MaximumEnergyMicrojoules)
            return Quarantine(KernelError.BudgetExceeded, "Energy measurement does not match the exact reserved operation envelope.");
        IReadOnlyList<BudgetAmount> actual = evidence.MeasuredMicrojoules == 0
            ? [] : [new BudgetAmount(ServiceBudgetDimension.EnergyMicrojoules, evidence.MeasuredMicrojoules)];
        var settled = budgets.SettleLease(owner, binding.Reservation, actual);
        if (!settled.IsSuccess)
            return Quarantine(settled.Error, settled.Message!);
        if (settled.Value!.State != BudgetReservationState.Released ||
            settled.Value.ChargedAmounts?.SequenceEqual(actual) != true)
            return KernelResult<V6EnergySettlementReceipt>.Fail(KernelError.StaleGeneration,
                "The budget owner's terminal charge does not match the completed energy evidence.");
        // Settlement and provider reset have independent owners. A reset observed after
        // the charge prevents a successful receipt, but cannot undo that terminal charge.
        if (provider.ProviderIdentity != binding.ProviderIdentity ||
            provider.ProviderGeneration != binding.ProviderGeneration ||
            provider.CounterGeneration != binding.CounterGeneration)
            return KernelResult<V6EnergySettlementReceipt>.Fail(KernelError.StaleGeneration,
                "Energy provider generation changed across terminal budget settlement; the charge remains terminal.");
        return KernelResult<V6EnergySettlementReceipt>.Ok(new(binding, evidence, settled.Value));

        KernelResult<V6EnergySettlementReceipt> Quarantine(KernelError error, string message)
        {
            _ = budgets.QuarantineLease(owner, operation.Energy.Reservation);
            return KernelResult<V6EnergySettlementReceipt>.Fail(error, message);
        }
    }
}
