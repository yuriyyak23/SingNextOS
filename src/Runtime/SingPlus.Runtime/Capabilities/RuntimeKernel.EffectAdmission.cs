using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly object _effectAdmissionIdentityGate = new();
    private ulong _nextEffectAdmissionAttemptId = 1;

    internal KernelResult<EffectAdmissionLease> AdmitSessionCapabilityEffect(
        ProcessHandle caller, ProcessHandle service, EndpointSessionHandle session,
        CapabilityId capability, ResourceKind resourceKind, string resourceId,
        ulong resourceGeneration, CapabilityOperation operation,
        ulong quotaAmount = 0, bool oneShot = false,
        Action? afterSessionPin = null)
    {
        var process = Processes.Resolve(caller);
        if (!process.IsSuccess) return KernelResult<EffectAdmissionLease>.Fail(process.Error, process.Message!);
        var effect = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!effect.IsSuccess) return KernelResult<EffectAdmissionLease>.Fail(effect.Error, effect.Message!);

        var sessionPin = EndpointSessions.AcquirePin(session, caller, service);
        if (!sessionPin.IsSuccess)
            return KernelResult<EffectAdmissionLease>.Fail(sessionPin.Error, sessionPin.Message!);
        OperationAuthorityLease? capabilityLease = null;
        try
        {
            afterSessionPin?.Invoke();
            var acquired = CapabilityAuthority.AcquireOperationAuthority(capability,
                process.Value!.DomainId, caller.Generation, resourceKind, resourceId, resourceGeneration,
                operation, session, quotaAmount, oneShot);
            if (!acquired.IsSuccess)
                return KernelResult<EffectAdmissionLease>.Fail(acquired.Error, acquired.Message!);
            capabilityLease = acquired.Value!;

            // Reservation/revalidation commit point. The session pin prevents closure after
            // this check; the exact capability lease is already admitted and follows its
            // closed static revocation policy if revocation wins later.
            var finalSession = EndpointSessions.RevalidatePin(sessionPin.Value!);
            if (!finalSession.IsSuccess)
                return KernelResult<EffectAdmissionLease>.Fail(finalSession.Error, finalSession.Message!);

            EffectAdmissionAttemptId attempt;
            lock (_effectAdmissionIdentityGate)
            {
                if (_nextEffectAdmissionAttemptId == 0)
                    return KernelResult<EffectAdmissionLease>.Fail(KernelError.CapacityExhausted,
                        "Effect-admission attempt identity space is exhausted.");
                attempt = new(_nextEffectAdmissionAttemptId++);
            }

            var admitted = new EffectAdmissionLease(attempt, capabilityLease, sessionPin.Value!);
            capabilityLease = null;
            sessionPin = default;
            return KernelResult<EffectAdmissionLease>.Ok(admitted);
        }
        finally
        {
            // Reverse-order compensation for every partial preparation.
            capabilityLease?.Dispose();
            sessionPin.Value?.Dispose();
        }
    }

    internal KernelResult<EffectAdmissionLease> AdmitSessionCapabilityEffect(
        ProcessHandle caller, ProcessHandle service, EndpointSessionHandle session,
        CapabilityHandleV2 capability, ResourceKind resourceKind, string resourceId,
        ulong resourceGeneration, CapabilityOperation operation,
        ulong quotaAmount = 0, bool oneShot = false,
        Action? afterSessionPin = null)
    {
        var id = CapabilityAuthority.ResolveCapabilityId(capability);
        return id.IsSuccess
            ? AdmitSessionCapabilityEffect(caller, service, session, id.Value, resourceKind,
                resourceId, resourceGeneration, operation, quotaAmount, oneShot, afterSessionPin)
            : KernelResult<EffectAdmissionLease>.Fail(id.Error, id.Message!);
    }
}
