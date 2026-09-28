using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum EffectAdmissionQualificationPoint
{
    AfterSessionPin = 0,
    AfterFinalSessionRevalidation,
    AfterCapabilityCommit,
}

internal interface IEffectAdmissionQualificationHook
{
    void At(EffectAdmissionQualificationPoint point);
}

public sealed partial class RuntimeKernel
{
    private readonly object _effectAdmissionIdentityGate = new();
    private ulong _nextEffectAdmissionAttemptId = 1;
    internal IEffectAdmissionQualificationHook? EffectAdmissionQualificationHook { private get; set; }

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

        EffectAdmissionAttemptId attempt;
        lock (_effectAdmissionIdentityGate)
        {
            if (_nextEffectAdmissionAttemptId == 0)
                return KernelResult<EffectAdmissionLease>.Fail(KernelError.CapacityExhausted,
                    "Effect-admission attempt identity space is exhausted.");
            attempt = new(_nextEffectAdmissionAttemptId++);
        }

        var sessionPin = EndpointSessions.AcquirePin(session, caller, service);
        if (!sessionPin.IsSuccess)
            return KernelResult<EffectAdmissionLease>.Fail(sessionPin.Error, sessionPin.Message!);
        OperationAuthorityLease? capabilityLease = null;
        try
        {
            afterSessionPin?.Invoke();
            EffectAdmissionQualificationHook?.At(EffectAdmissionQualificationPoint.AfterSessionPin);

            // SessionPin is reversible preparation. Revalidate it before the
            // consumptive capability commit so a losing close race cannot consume
            // quota/one-shot authority for a stage that will never execute.
            var finalSession = EndpointSessions.RevalidatePin(sessionPin.Value!);
            if (!finalSession.IsSuccess)
                return KernelResult<EffectAdmissionLease>.Fail(finalSession.Error, finalSession.Message!);
            EffectAdmissionQualificationHook?.At(EffectAdmissionQualificationPoint.AfterFinalSessionRevalidation);

            var acquired = CapabilityAuthority.AcquireOperationAuthority(capability,
                process.Value!.DomainId, caller.Generation, resourceKind, resourceId, resourceGeneration,
                operation, session, quotaAmount, oneShot);
            if (!acquired.IsSuccess)
                return KernelResult<EffectAdmissionLease>.Fail(acquired.Error, acquired.Message!);
            capabilityLease = acquired.Value!;
            EffectAdmissionQualificationHook?.At(EffectAdmissionQualificationPoint.AfterCapabilityCommit);

            var admitted = new EffectAdmissionLease(this, attempt, capabilityLease, sessionPin.Value!);
            capabilityLease = null;
            sessionPin = default;
            return KernelResult<EffectAdmissionLease>.Ok(admitted);
        }
        finally
        {
            // Reverse-order compensation for every partial preparation.
            capabilityLease?.Dispose();
            if (sessionPin.Value is { } remainingPin)
                FinalizeSessionPin(remainingPin, session);
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

    internal void ReleaseEffectAdmissionResources(
        OperationAuthorityLease? capability,
        EndpointSessionPin? session)
    {
        // Reverse acquisition order. Releasing the operation lease is lifecycle
        // cleanup only; CapabilityAuthority does not refund quota or one-shot state.
        capability?.Dispose();
        if (session is not null)
            FinalizeSessionPin(session, session.Handle);
    }
}
