using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    /// <summary>Real local admission and cancellation; no provider submission or completion evidence.</summary>
    public static KernelBootStageResult RunExternal(EmulatorSession session)
    {
        if (session.Runtime is not { } runtime)
            return KernelBootStageResult.Failure("Runtime is unavailable.");
        KernelResult<ProcessHandle> subject = session.SpawnProcess("singplus.emulator.external.admission", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!subject.IsSuccess) return Failure("spawn", subject.Error, subject.Message);
        ProcessHandle owner = subject.Value;
        KernelResult<BufferRecord> allocated = session.AllocateTrackedBuffer(owner, 64);
        if (!allocated.IsSuccess) return Failure("allocate", allocated.Error, allocated.Message);
        OwnedBuffer<byte> buffer = allocated.Value!.Buffer;
        buffer.Span.Fill(0x5A);
        KernelResult<OperationPreparation> prepared = runtime.PrepareExternalOperation(owner,
            [new OperationRegionUseRequest(buffer.Handle, RegionUseMode.ExclusiveWrite, new RegionUseRange(0, 64))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged);
        if (!prepared.IsSuccess) return Failure("prepare", prepared.Error, prepared.Message);
        ExternalOperationHandle operation = prepared.Value!.Operation;
        if (!BufferAccessible(buffer)) return KernelBootStageResult.Failure("Preparation acquired a Region use prematurely.");
        // Default dependencies carry no provider evidence. This operation is never submitted.
        KernelResult<OperationAdmissionSnapshot> admitted = runtime.AdmitExternalOperation(owner, operation, default);
        if (!admitted.IsSuccess) return Failure("admit", admitted.Error, admitted.Message);
        if (admitted.Value!.RegionUses.Count != 1 || admitted.Value.RegionUses[0].State != RegionUseState.Active)
            return KernelBootStageResult.Failure("Admission did not acquire the exclusive Region use.");
        EmulatorConsole.StageDetail($"external operation {operation.OperationId.Value}: Prepared -> Admitted; exclusive Region use held; no provider submission");
        if (!CheckExternalCharge(1)) return KernelBootStageResult.Failure("Admission external-operation budget charge mismatch.");
        KernelResult<ExternalOperationSnapshot> premature = runtime.ReleaseExternalOperation(owner, operation, default);
        if (premature.IsSuccess || premature.Error != KernelError.InvalidTransition)
            return KernelBootStageResult.Failure($"Active operation release returned unexpected result: {premature.Error}.");
        KernelResult freeWhileHeld = runtime.ReleaseRegion(owner, buffer);
        if (freeWhileHeld.IsSuccess || freeWhileHeld.Error != KernelError.RegionUseConflict)
            return KernelBootStageResult.Failure("Region reclaimed while held by the external operation.");
        EmulatorConsole.StageDetail($"premature operation release refused ({premature.Error}); Region reclaim refused ({freeWhileHeld.Error})");
        KernelResult<ExternalOperationSnapshot> cancelled = runtime.CancelExternalOperation(owner, operation, providerCancellationSupported: false);
        if (!cancelled.IsSuccess) return Failure("cancel", cancelled.Error, cancelled.Message);
        if (cancelled.Value!.Disposition != ExternalOperationDisposition.Cancelled ||
            !runtime.Regions.ValidateUse(admitted.Value.RegionUses[0].Handle, admitted.Value.Principal).IsSuccess)
            return KernelBootStageResult.Failure("Cancellation lost the Region use before explicit release.");
        if (!CheckExternalCharge(1)) return KernelBootStageResult.Failure("Cancellation returned budget before release.");
        KernelResult<ExternalOperationSnapshot> released = runtime.ReleaseExternalOperation(owner, operation, default);
        if (!released.IsSuccess) return Failure("release", released.Error, released.Message);
        if (released.Value!.State != ExternalOperationState.Released || !BufferAccessible(buffer) ||
            buffer.Span.ToArray().Any(static value => value != 0x5A))
            return KernelBootStageResult.Failure("Release failed to restore access or preserve the unpublished bytes.");
        if (!CheckExternalCharge(0)) return KernelBootStageResult.Failure("Release retained external-operation budget charge.");
        KernelResult<RegionUseDescriptor> oldUse = runtime.Regions.ValidateUse(admitted.Value.RegionUses[0].Handle, admitted.Value.Principal);
        if (oldUse.IsSuccess || oldUse.Error != KernelError.InvalidRegionState)
            return KernelBootStageResult.Failure("Released Region use remained valid.");
        KernelResult<ExternalOperationSnapshot> queried = runtime.QueryExternalOperation(owner, operation);
        if (!queried.IsSuccess) return Failure("query", queried.Error, queried.Message);
        foreach (ExternalOperationTransition transition in queried.Value!.Transitions)
            EmulatorConsole.StageDetail($"external #{operation.OperationId.Value} seq={transition.Sequence}: {transition.From} -> {transition.To} ({transition.Event})");
        KernelResult freed = runtime.ReleaseRegion(owner, buffer);
        if (!freed.IsSuccess) return Failure("free", freed.Error, freed.Message);
        session.Buffers.Remove(buffer.Handle.RegionId.Value);
        EmulatorConsole.StageDetail("cancelled before effect; explicit release closed Region use; no output published");
        bool CheckExternalCharge(ulong expected)
        {
            if (!session.IsBudgetConfigured) return true;
            KernelResult<BudgetAccountSnapshot> account = runtime.QueryBudget(session.BudgetAccounts[owner.ProcessId.Value].ProcessBudget);
            if (!account.IsSuccess) return false;
            ulong used = account.Value!.Usage.Single(static value => value.Dimension == ServiceBudgetDimension.ExternalOperations).Used;
            EmulatorConsole.StageDetail($"ExternalOperations budget: used={used}, expected={expected}");
            return used == expected;
        }

        KernelResult reclaimed = session.TerminateAndReclaim(owner);
        return reclaimed.IsSuccess ? KernelBootStageResult.Success() : Failure("reclaim", reclaimed.Error, reclaimed.Message);
    }
}
