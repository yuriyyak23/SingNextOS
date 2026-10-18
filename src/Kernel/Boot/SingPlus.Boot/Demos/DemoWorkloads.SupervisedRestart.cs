using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunSupervisedRestart(EmulatorSession session)
    {
        if (session.Runtime is not { } runtime || session.SystemProcess is not { } system)
            return KernelBootStageResult.Failure("Runtime/system is unavailable.");
        if (session.Sips.Count != 1 || session.Sips[0].StaticVerificationProof is null || session.Options.SipRoot is not { } root)
            return KernelBootStageResult.Failure("supervised-restart requires exactly one --sip with --sip-root static verification.");
        var source = session.Sips[0];
        byte[] image;
        try
        {
            using var stream = new FileStream(source.ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 64 * 1024 * 1024)
                return KernelBootStageResult.Failure("Replacement image size is invalid.");
            image = new byte[checked((int)stream.Length)];
            stream.ReadExactly(image);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return KernelBootStageResult.Failure($"Replacement image read failed: {exception.Message}"); }
        var digest = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        if (digest != source.ImageDigest) return KernelBootStageResult.Failure("Image changed since boot admission.");
        var verified = SipStaticVerifier.Verify(source.ImagePath, root, digest, out _);
        if (!verified.IsSuccess) return verified;
        var nextImage = image;
        var nextDigest = digest;
        if (session.Options.ReplacementSipPath is { } replacementPath)
        {
            try
            {
                using var stream = new FileStream(replacementPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length is <= 0 or > 64 * 1024 * 1024)
                    return KernelBootStageResult.Failure("Generation-2 image size is invalid.");
                nextImage = new byte[checked((int)stream.Length)];
                stream.ReadExactly(nextImage);
                nextDigest = Convert.ToHexString(SHA256.HashData(nextImage)).ToLowerInvariant();
                if (nextDigest == digest)
                    return KernelBootStageResult.Failure("Explicit generation-2 image must have different bytes from generation 1.");
                var verifiedNext = SipStaticVerifier.Verify(replacementPath, root, nextDigest, out _);
                if (!verifiedNext.IsSuccess) return verifiedNext;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { return KernelBootStageResult.Failure($"Generation-2 image read failed: {exception.Message}"); }
        }
        var principal = runtime.Processes.Resolve(system);
        if (!principal.IsSuccess) return Failure("resolve system", principal.Error, principal.Message);
        var control = runtime.MintCapability(principal.Value!.DomainId, system, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute);
        if (!control.IsSuccess) return Failure("supervisor capability", control.Error, control.Message);
        var created = runtime.CreateServiceSupervisor(system, control.Value!.CapabilityId);
        if (!created.IsSuccess) return Failure("create supervisor", created.Error, created.Message);
        var supervisor = created.Value!;
        var ids = session.AllocateIdentity();
        var identity = new ComponentIdentity($"singplus.emulator.supervised:{ids.Process.Value}");
        var name = identity.Name;
        var protocol = EmulatorSession.CreatePingPongProtocol();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        ManagedServiceDefinition Definition(ulong generation)
        {
            var process = new SingProcessManifestV1(ids.Process, ids.Domain, generation, source.AssemblyName,
                ExecutionRole.Sip, MemoryProfile.SipRegion);
            var provided = new ProvidedServiceManifestV1(name, contract);
            var manifest = new ServiceManifestV1(identity, new ComponentVersion(generation.ToString()), generation == 1 ? digest : nextDigest, process, [provided]);
            return new ManagedServiceDefinition(new ComponentAdmissionPlan(manifest, generation == 1 ? image : nextImage,
                providedServices: [new ComponentProvidedServiceRegistration(provided, protocol)]), name);
        }
        session.EmulatorSpawnedPids.Add(ids.Process.Value); // Track partial admission for shutdown cleanup.
        var registered = supervisor.Register(Definition(1));
        if (!registered.IsSuccess) return Failure("register", registered.Error, registered.Message);
        var started = supervisor.Start(identity);
        if (!started.IsSuccess) return Failure("start", started.Error, started.Message);
        var old = started.Value!.Snapshot.Instance;
        if (old is null) return KernelBootStageResult.Failure("Supervisor did not publish an instance.");
        var oldDescriptor = runtime.ResolveByServiceName(name);
        if (!oldDescriptor.IsSuccess) return Failure("resolve old", oldDescriptor.Error, oldDescriptor.Message);
        var replaced = supervisor.Replace(old.Value, Definition(2));
        if (!replaced.IsSuccess) return Failure("replace", replaced.Error, replaced.Message);
        var next = replaced.Value!.Snapshot.Instance;
        if (!replaced.Value.FreshAdmission || next is null || next.Value.Process.ProcessId != old.Value.Process.ProcessId || next.Value.Process.Generation != 2)
            return KernelBootStageResult.Failure("Supervisor replacement did not freshly admit the next process generation.");
        var stale = runtime.OpenSession(system, oldDescriptor.Value);
        if (stale.IsSuccess || stale.Error != KernelError.StaleGeneration)
            return KernelBootStageResult.Failure($"Old descriptor returned {stale.Error} after supervisor replacement.");
        var component = runtime.QueryComponent(identity);
        if (!component.IsSuccess || component.Value!.ImageDigest != nextDigest)
            return KernelBootStageResult.Failure("Published replacement component digest does not match the verified generation-2 bytes.");
        EmulatorConsole.StageDetail($"generation image digest: {digest} -> {nextDigest}");
        var oldDrain = supervisor.Drain(old.Value);
        if (oldDrain.IsSuccess || oldDrain.Error != KernelError.StaleGeneration) return KernelBootStageResult.Failure("Old instance controlled the new supervisor generation.");
        var drained = supervisor.Drain(next.Value);
        if (!drained.IsSuccess) return Failure("drain replacement", drained.Error, drained.Message);
        if (!drained.Value!.ClosureProven) return KernelBootStageResult.Failure("Replacement closure remains unproven.");
        session.EmulatorSpawnedPids.Remove(ids.Process.Value);
        var revokedControl = runtime.RevokeCapability(control.Value.CapabilityId);
        if (!revokedControl.IsSuccess) return Failure("revoke supervisor control", revokedControl.Error, revokedControl.Message);
        EmulatorConsole.StageDetail($"supervisor Replace: process {ids.Process.Value} generation 1 -> 2; fresh admission; old descriptor refused ({stale.Error}); old instance refused ({oldDrain.Error}); final closure proven");
        EmulatorConsole.StageDetail("static proof checked; runtime lifecycle only; no CLR image invocation or in-flight effect claimed");
        return KernelBootStageResult.Success();
    }
}
