using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>
/// Locally published scheduler-policy state for one exact platform-domain binding.
/// The feature descriptor states whether the provider only modeled the request or
/// supplied stronger admission/execution evidence. This snapshot is not authority.
/// </summary>
public readonly record struct PlatformExecutionPolicyRegistration(
    PlatformDomainBinding DomainBinding,
    PlatformExecutionPolicy Policy,
    PlatformFeatureDescriptor Feature);

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<PlatformExecutionPolicyRegistration> ConfigureExecutionPolicy(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        PlatformExecutionPolicy policy,
        Func<Func<KernelResult>, KernelResult> ownerCommit)
    {
        KernelResult bindingValidation;
        lock (_secureDomainLifecycleGate) bindingValidation = ValidateDomain(binding, expectedSubject);
        if (!bindingValidation.IsSuccess)
        {
            return KernelResult<PlatformExecutionPolicyRegistration>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);
        }

        var policyValidation = PlatformExecutionPolicyContract.ValidatePolicy(policy);
        if (!policyValidation.IsSuccess)
        {
            return FromProviderFailure<PlatformExecutionPolicyRegistration>(
                policyValidation.Status,
                policyValidation.Message);
        }

        var feature = _featureManifest.Resolve(PlatformFeatureFamily.ExecutionPolicy);
        if (feature.ContractVersion < PlatformExecutionPolicyContract.ContractVersion ||
            !PlatformExecutionPolicyContract.IsSupportedAvailability(feature.Availability))
        {
            return KernelResult<PlatformExecutionPolicyRegistration>.Fail(
                KernelError.PlatformUnsupported,
                $"The platform provider does not expose execution-policy contract v{PlatformExecutionPolicyContract.ContractVersion} as ModelOnly, RuntimeAdmission, or Executable.");
        }

        if (_provider is not IPlatformExecutionPolicyProvider policyProvider)
        {
            return KernelResult<PlatformExecutionPolicyRegistration>.Fail(
                KernelError.PlatformUnsupported,
                "The bound platform provider does not implement the execution-policy contract.");
        }

        DomainRecord record = null!;
        PlatformBackendEpoch epoch = default;
        PlatformExecutionPolicyRegistration? existing = null;
        PlatformProviderIncarnation source = default;
        var admitted = ownerCommit(() =>
        {
            lock (_secureDomainLifecycleGate)
            {
                var current = ValidateDomain(binding, expectedSubject);
                if (!current.IsSuccess) return current;
                record = _domains[binding.BindingId];
                epoch = BackendEpoch;
                if (epoch != record.BackendEpoch || _backendEpochExhausted)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Policy admission source changed.");
                if (record.ExecutionTransitionInFlight)
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Execution attachment transition is already pending.");
                if (record.ExecutionPolicy is { } known)
                {
                    existing = known;
                    if (known.Policy != policy)
                        return KernelResult.Fail(KernelError.PlatformBindingActive, "A different immutable execution policy is already registered.");
                }
                record.ExecutionTransitionInFlight = true;
                return KernelResult.Ok();
            }
        });
        if (!admitted.IsSuccess) return Failure(admitted.Error, admitted.Message!);
        var invoked = false;
        try
        {
            source = CurrentProviderIncarnation();
            var sourceAdmission = ownerCommit(CheckCurrent);
            if (!sourceAdmission.IsSuccess) return Failure(sourceAdmission.Error, sourceAdmission.Message!);
            if (existing is { } repeat) return KernelResult<PlatformExecutionPolicyRegistration>.Ok(repeat);
            source = CurrentProviderIncarnation(); // Callback outside owner locks.
            var beforeEffect = ownerCommit(CheckCurrent);
            if (!beforeEffect.IsSuccess) return Failure(beforeEffect.Error, beforeEffect.Message!);
            invoked = true;
            var result = policyProvider.ConfigureExecutionPolicy(record.ProviderLease, policy);
            if (!result.IsSuccess)
            {
                source = CurrentProviderIncarnation();
                var current = ownerCommit(CheckCurrent);
                if (!current.IsSuccess || RequiresDomainQuarantine(result.Status)) Pin();
                return current.IsSuccess ? FromProviderFailure<PlatformExecutionPolicyRegistration>(result.Status, result.Message) :
                    Failure(current.Error, current.Message!);
            }
            var receipt = PlatformExecutionPolicyContract.ValidateResult(record.ProviderLease, policy, result.Value!);
            if (!receipt.IsSuccess)
            {
                Pin();
                return Failure(KernelError.PlatformFaulted, receipt.Message ?? "Malformed policy acceptance.");
            }
            var registration = new PlatformExecutionPolicyRegistration(binding, policy, feature);
            // Retain exact acceptance before a fallible source observation. A quarantined
            // domain cannot expose it as usable policy or repeat the provider effect.
            lock (_secureDomainLifecycleGate)
            {
                record.ExecutionPolicy = registration;
                if (CurrentOriginalRootOwnerLocked(record) is { } current)
                    current.ExecutionPolicy = registration;
            }
            source = CurrentProviderIncarnation();
            var published = ownerCommit(CheckCurrent);
            if (!published.IsSuccess)
            {
                Pin();
                return Failure(published.Error, published.Message!);
            }
            return KernelResult<PlatformExecutionPolicyRegistration>.Ok(registration);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            if (invoked) Pin();
            return Failure(KernelError.PlatformFaulted, $"Policy source/provider outcome unavailable: {exception.Message}");
        }
        finally
        {
            lock (_secureDomainLifecycleGate)
            {
                record.ExecutionTransitionInFlight = false;
                if (CurrentOriginalRootOwnerLocked(record) is { } current)
                    current.ExecutionTransitionInFlight = false;
            }
        }

        KernelResult CheckCurrent()
        {
            lock (_secureDomainLifecycleGate)
            {
                if (!_domains.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, record) ||
                    current.Binding != binding || BackendEpoch != epoch || _backendEpochExhausted ||
                    source.Value == 0 || source != record.ProviderIncarnation || !record.ExecutionTransitionInFlight)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Exact policy source/domain continuity changed.");
                return ValidateDomain(binding, expectedSubject);
            }
        }
        void Pin()
        {
            lock (_secureDomainLifecycleGate)
            {
                QuarantineDomain(record);
                if (CurrentOriginalRootOwnerLocked(record) is { } current)
                    QuarantineDomain(current);
            }
        }
        static KernelResult<PlatformExecutionPolicyRegistration> Failure(KernelError error, string message) =>
            KernelResult<PlatformExecutionPolicyRegistration>.Fail(error, message);
    }
}
