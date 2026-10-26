using SingPlus.Contracts;
using SingPlus.Sip;

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
        KernelResult<SingProcess> process = Processes.Resolve(caller);
        if (!process.IsSuccess) return KernelResult<EffectAdmissionLease>.Fail(process.Error, process.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!effect.IsSuccess) return KernelResult<EffectAdmissionLease>.Fail(effect.Error, effect.Message!);
        KernelResult provider = ValidateEffectAdmissionProcess(service);
        if (!provider.IsSuccess) return KernelResult<EffectAdmissionLease>.Fail(provider.Error, provider.Message!);

        EffectAdmissionAttemptId attempt;
        lock (_effectAdmissionIdentityGate)
        {
            if (_nextEffectAdmissionAttemptId == 0)
                return KernelResult<EffectAdmissionLease>.Fail(KernelError.CapacityExhausted,
                    "Effect-admission attempt identity space is exhausted.");
            attempt = new(_nextEffectAdmissionAttemptId++);
        }

        KernelResult<EndpointSessionPin> sessionPin = EndpointSessions.AcquirePin(session, caller, service);
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
            KernelResult preparedSession = EndpointSessions.RevalidatePin(sessionPin.Value!);
            if (!preparedSession.IsSuccess)
                return KernelResult<EffectAdmissionLease>.Fail(preparedSession.Error, preparedSession.Message!);
            KernelResult<OperationAuthorityLease> acquired = CapabilityAuthority.AcquireOperationAuthority(capability,
                process.Value!.DomainId, caller.Generation, resourceKind, resourceId, resourceGeneration,
                operation, session, quotaAmount, oneShot, revalidateConsumer: () =>
                {
                    KernelResult<SingProcess> current = Processes.Resolve(caller);
                    if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                    KernelResult accepts = EnsureProcessAcceptsNewEffects(current.Value!);
                    if (!accepts.IsSuccess) return accepts;
                    KernelResult providerCurrent = ValidateEffectAdmissionProcess(service);
                    if (!providerCurrent.IsSuccess) return providerCurrent;
                    KernelResult finalSession = EndpointSessions.RevalidatePin(sessionPin.Value!);
                    if (!finalSession.IsSuccess) return KernelResult.Fail(finalSession.Error, finalSession.Message!);
                    EffectAdmissionQualificationHook?.At(EffectAdmissionQualificationPoint.AfterFinalSessionRevalidation);
                    return KernelResult.Ok();
                }, commitConsumer: commit =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        // Callbacks above were preparation. Freeze both exact
                        // process owners and the real session around source
                        // quota/one-shot commit; no permission comes from a pin.
                        KernelResult callerCurrent = ValidateEffectAdmissionProcess(caller);
                        if (!callerCurrent.IsSuccess)
                            return KernelResult<OperationAuthorityLease>.Fail(callerCurrent.Error, callerCurrent.Message!);
                        KernelResult providerCurrent = ValidateEffectAdmissionProcess(service);
                        if (!providerCurrent.IsSuccess)
                            return KernelResult<OperationAuthorityLease>.Fail(providerCurrent.Error, providerCurrent.Message!);
                        return EndpointSessions.CommitPinnedEffectAdmission(sessionPin.Value!, commit);
                    }
                });
            if (!acquired.IsSuccess)
                return KernelResult<EffectAdmissionLease>.Fail(acquired.Error, acquired.Message!);
            capabilityLease = acquired.Value!;
            EffectAdmissionQualificationHook?.At(EffectAdmissionQualificationPoint.AfterCapabilityCommit);

            EffectAdmissionLease admitted = new EffectAdmissionLease(this, attempt, capabilityLease, sessionPin.Value!);
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

    private KernelResult ValidateEffectAdmissionProcess(ProcessHandle handle)
    {
        KernelResult<SingProcess> current = Processes.Resolve(handle);
        return current.IsSuccess ? EnsureProcessAcceptsNewEffects(current.Value!)
            : KernelResult.Fail(current.Error, current.Message!);
    }

    internal KernelResult<EffectAdmissionLease> AdmitSessionCapabilityEffect(
        ProcessHandle caller, ProcessHandle service, EndpointSessionHandle session,
        CapabilityHandleV2 capability, ResourceKind resourceKind, string resourceId,
        ulong resourceGeneration, CapabilityOperation operation,
        ulong quotaAmount = 0, bool oneShot = false,
        Action? afterSessionPin = null)
    {
        KernelResult<CapabilityId> id = CapabilityAuthority.ResolveCapabilityId(capability);
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
