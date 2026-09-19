using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;

internal static class DebugRuntimeExercise
{
    internal static async Task<DebugExecutionResult> RunAsync()
    {
        try
        {
            var kernel = new RuntimeKernel();
            var root = CreateAndStart(kernel, 500, 5000, "debug-runtime-root", ExecutionRole.Kernel);
            var component = AdmitDebugComponent(kernel);
            if (component.State != ComponentLifecycleState.Running)
                return DebugExecutionResult.Failure("Debug component admission did not reach Running.");

            var rootCapability = Require(kernel.MintCapability(
                new DomainId(5000), root, ResourceKind.Process, "debug.runtime.exercise",
                CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Execute));
            Require(kernel.ValidateCapability(root, rootCapability.CapabilityId, CapabilityRights.Execute));

            var minimalSip = CreateAndStart(kernel, 520, 5200, "debug-minimal-sip", ExecutionRole.Sip);
            Require(kernel.ParkProcess(minimalSip));
            Require(kernel.ResumeProcess(minimalSip));

            var buffer = Require(kernel.AllocateBuffer<byte>(root, 3));
            buffer.Span[0] = 0x53;
            buffer.Span[1] = 0x49;
            buffer.Span[2] = 0x50;
            var moved = Require(kernel.TransferRegion(root, minimalSip, buffer));
            if (!moved.Span.SequenceEqual(new byte[] { 0x53, 0x49, 0x50 }))
                return DebugExecutionResult.Failure("Region transfer did not preserve the debug SIP payload.");
            Require(kernel.ReleaseRegion(minimalSip, moved));

            Require(kernel.TerminateProcess(minimalSip));

            Require(kernel.RevokeCapability(rootCapability.CapabilityId));
            Console.WriteLine("[debug-host] runtime subsystems ready: component admission, capability validation, process lifecycle, SIP-role child, region transfer.");
            return DebugExecutionResult.Success();
        }
        catch (InvalidOperationException exception)
        {
            return DebugExecutionResult.Failure(exception.Message);
        }
    }

    private static ComponentLifecycleSnapshot AdmitDebugComponent(RuntimeKernel kernel)
    {
        byte[] image = [0x44, 0x42, 0x47, 0x31];
        var process = new SingProcessManifestV1(
            new ProcessId(510), new DomainId(5100), 1, "debug-runtime-component",
            ExecutionRole.Sip, MemoryProfile.SipRegion);
        var manifest = new ServiceManifestV1(
            new ComponentIdentity("debug-runtime-component"), new ComponentVersion("debug-1"),
            Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(), process);
        return Require(kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image)));
    }

    private static ProcessHandle CreateAndStart(
        RuntimeKernel kernel, ulong processId, ulong domainId, string name, ExecutionRole role)
    {
        var manifest = new SingProcessManifestV1(
            new ProcessId(processId), new DomainId(domainId), 1, name,
            role, role == ExecutionRole.Sip ? MemoryProfile.SipRegion : MemoryProfile.ManagedGc);
        var created = Require(kernel.CreateProcess(manifest));
        var handle = new ProcessHandle(created.ProcessId, created.Generation);
        Require(kernel.AdmitProcess(handle));
        Require(kernel.StartProcess(handle));
        return handle;
    }

    private static T Require<T>(KernelResult<T> result) => result.IsSuccess
        ? result.Value!
        : throw new InvalidOperationException(result.Message ?? result.Error.ToString());

    private static void Require(KernelResult result)
    {
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message ?? result.Error.ToString());
    }
}
