using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunCompute(EmulatorSession session, bool cancel = false, bool fault = false)
    {
        if (!session.Options.HostModelPlatform || session.Runtime is not { } runtime)
            return KernelBootStageResult.Failure("Compute model demo requires --platform host-model.");
        if ((cancel || fault) != session.Options.DeferComputeCompletion)
            return KernelBootStageResult.Failure((cancel || fault)
                ? "compute-cancel/compute-fault requires --defer-compute."
                : "compute requires immediate completion; use compute-cancel with --defer-compute.");
        var subject = session.SpawnProcess("singplus.emulator.compute.model", ExecutionRole.User, MemoryProfile.ManagedGc, start: false);
        if (!subject.IsSuccess) return Failure("admit compute subject", subject.Error, subject.Message);
        var owner = subject.Value;
        var bound = runtime.BindPlatformDomain(owner);
        if (!bound.IsSuccess) return Failure("bind", bound.Error, bound.Message);
        session.PlatformBindings[owner.ProcessId.Value] = bound.Value;
        var source = session.AllocateTrackedBuffer(owner, 64);
        if (!source.IsSuccess) return Failure("source allocate", source.Error, source.Message);
        var destination = session.AllocateTrackedBuffer(owner, 64);
        if (!destination.IsSuccess) return Failure("destination allocate", destination.Error, destination.Message);
        var input = source.Value!.Buffer;
        var output = destination.Value!.Buffer;
        for (var index = 0; index < input.Length; index++) input.Span[index] = (byte)(index * 3 + 1);
        output.Span.Fill(0xCC);
        var expected = input.Span.Slice(7, 19).ToArray();
        var unpublishedAlias = output.Span;
        var process = runtime.Processes.Resolve(owner);
        if (!process.IsSuccess) return Failure("resolve", process.Error, process.Message);
        var sourceMap = MapModelBuffer(runtime, owner, process.Value!.DomainId, bound.Value, input, PlatformMemoryAccess.Read);
        if (!sourceMap.IsSuccess) return Failure("source mapping", sourceMap.Error, sourceMap.Message);
        var destinationMap = MapModelBuffer(runtime, owner, process.Value.DomainId, bound.Value, output, PlatformMemoryAccess.Write);
        if (!destinationMap.IsSuccess) return Failure("destination mapping", destinationMap.Error, destinationMap.Message);
        var compute = runtime.MintCapability(process.Value.DomainId, owner, ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute);
        if (!compute.IsSuccess) return Failure("compute capability", compute.Error, compute.Message);
        var submitted = runtime.SubmitPlatformDsc1Copy(owner, bound.Value, new Dsc1ComputeCapability(compute.Value!.CapabilityId),
            input, new PlatformDsc1RegionRange(sourceMap.Value, 7, 19), output, new PlatformDsc1RegionRange(destinationMap.Value, 23, 19));
        if (!submitted.IsSuccess) return Failure("submit", submitted.Error, submitted.Message);
        if (submitted.Value.Feature.Availability != PlatformFeatureAvailability.ModelOnly)
            return KernelBootStageResult.Failure("Expected ModelOnly DSC1 evidence.");
        if (unpublishedAlias.ToArray().Any(static value => value != 0xCC) || BufferAccessible(input) || BufferAccessible(output))
            return KernelBootStageResult.Failure("Output published early or buffer access was not reserved during submission.");
        EmulatorConsole.StageDetail($"DSC1 submission {submitted.Value.SubmissionId.Value}: ModelOnly; output unpublished; source/destination access blocked");
        if (fault)
        {
            var provider = session.HostModelProvider;
            if (provider?.LastDsc1Submission is not { } providerSubmission)
                return KernelBootStageResult.Failure("Host model did not expose the exact DSC1 submission for fault injection.");
            var injected = provider.AdvanceOperation(providerSubmission.Operation, PlatformCompletionState.Faulted);
            if (!injected.IsSuccess) return KernelBootStageResult.Failure($"Fault injection refused: {injected.Status}: {injected.Message}");
            var observedFault = runtime.ObservePlatformDsc1Copy(owner, submitted.Value);
            if (observedFault.IsSuccess || observedFault.Error != KernelError.PlatformFaulted ||
                BufferAccessible(input) || BufferAccessible(output) || unpublishedAlias.ToArray().Any(static value => value != 0xCC))
                return KernelBootStageResult.Failure("Faulted operation published output or released buffer reservations.");
            var unsafeFree = runtime.ReleaseRegion(owner, output);
            if (unsafeFree.IsSuccess || unsafeFree.Error != KernelError.PlatformBindingActive) return KernelBootStageResult.Failure("Faulted output was reclaimed without closure.");
            var unsafeDomain = runtime.RevokePlatformDomain(owner, bound.Value);
            if (unsafeDomain.IsSuccess || unsafeDomain.Error != KernelError.PlatformBindingActive) return KernelBootStageResult.Failure("Faulted operation domain was reclaimed without closure.");
            EmulatorConsole.StageDetail($"injected exact Host model operation fault: observe={observedFault.Error}; output unpublished; source/destination inaccessible; Region release={unsafeFree.Error}; domain closure={unsafeDomain.Error}");
            return KernelBootStageResult.Failure("Expected injected DSC1 model fault: reservations remain pinned. Public DSC1 recovery is unavailable; shutdown will report incomplete teardown. No durable recovery record is claimed.");
        }
        if (cancel)
        {
            var pending = runtime.ObservePlatformDsc1Copy(owner, submitted.Value);
            if (pending.IsSuccess || pending.Error != KernelError.PlatformBindingDraining || BufferAccessible(input) || BufferAccessible(output))
                return KernelBootStageResult.Failure("Deferred completion did not preserve the pending operation and buffer reservations.");
            var prematureDomain = runtime.RevokePlatformDomain(owner, bound.Value);
            if (prematureDomain.IsSuccess || prematureDomain.Error != KernelError.PlatformBindingActive)
                return KernelBootStageResult.Failure("Domain closure accepted an active DSC1 operation.");
            EmulatorConsole.StageDetail("completion pending (PlatformBindingDraining); premature domain reclaim refused (PlatformBindingActive)");
        }
        var completed = cancel
            ? runtime.CancelPlatformDsc1Copy(owner, submitted.Value)
            : runtime.ObservePlatformDsc1Copy(owner, submitted.Value);
        if (!completed.IsSuccess) return Failure("observe completion", completed.Error, completed.Message);
        if (!cancel && (completed.Value.Outcome != PlatformDsc1CopyOutcome.Completed || !completed.Value.OutputPublished || completed.Value.ByteLength != 19 ||
            !output.Span.Slice(23, 19).SequenceEqual(expected) ||
            output.Span[..23].ToArray().Any(static value => value != 0xCC) || output.Span[42..].ToArray().Any(static value => value != 0xCC)))
            return KernelBootStageResult.Failure("Exact bounded copy/publication verification failed.");
        if (cancel && (completed.Value.Outcome != PlatformDsc1CopyOutcome.Cancelled || completed.Value.OutputPublished ||
                       !BufferAccessible(input) || output.Span.ToArray().Any(static value => value != 0xCC)))
            return KernelBootStageResult.Failure("Cancellation failed to close the operation without output publication.");
        EmulatorConsole.StageDetail(cancel
            ? "cancellation closed provider operation; no output published; local buffer access restored"
            : "closed completion observed; 19 bytes published at offset 23; surrounding bytes unchanged");
        var repeated = runtime.ObservePlatformDsc1Copy(owner, submitted.Value);
        if (repeated.IsSuccess || repeated.Error != KernelError.PlatformBindingNotFound)
            return KernelBootStageResult.Failure("Closed continuation was accepted again.");
        var sourceClosed = runtime.RevokePlatformRegionMapping(owner, sourceMap.Value);
        if (!sourceClosed.IsSuccess) return Failure("source mapping revoke", sourceClosed.Error, sourceClosed.Message);
        var destinationClosed = runtime.RevokePlatformRegionMapping(owner, destinationMap.Value);
        if (!destinationClosed.IsSuccess) return Failure("destination mapping revoke", destinationClosed.Error, destinationClosed.Message);
        var sourceFreed = runtime.ReleaseRegion(owner, input);
        if (!sourceFreed.IsSuccess) return Failure("source release", sourceFreed.Error, sourceFreed.Message);
        var destinationFreed = runtime.ReleaseRegion(owner, output);
        if (!destinationFreed.IsSuccess) return Failure("destination release", destinationFreed.Error, destinationFreed.Message);
        session.Buffers.Remove(input.Handle.RegionId.Value);
        session.Buffers.Remove(output.Handle.RegionId.Value);
        var reclaimed = session.TerminateAndReclaim(owner);
        if (!reclaimed.IsSuccess) return Failure("teardown", reclaimed.Error, reclaimed.Message);
        EmulatorConsole.StageDetail("stale continuation refused; mappings revoked, buffers released, subject reclaimed");
        return KernelBootStageResult.Success();
    }

    private static bool BufferAccessible(OwnedBuffer<byte> buffer)
    {
        try { return buffer.Span.Length >= 0; }
        catch (InvalidOperationException) { return false; }
    }

    private static KernelResult<PlatformRegionMapping> MapModelBuffer(RuntimeKernel runtime, ProcessHandle owner, DomainId domain,
        PlatformDomainBinding binding, OwnedBuffer<byte> buffer, PlatformMemoryAccess access)
    {
        var rights = CapabilityRights.Map | (access == PlatformMemoryAccess.Read ? CapabilityRights.Read : CapabilityRights.Write);
        var minted = runtime.MintCapability(domain, owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), rights);
        return minted.IsSuccess
            ? runtime.MapPlatformOwnedRegion(owner, binding, minted.Value!.CapabilityId, buffer.Handle, access)
            : KernelResult<PlatformRegionMapping>.Fail(minted.Error, minted.Message!);
    }
}
