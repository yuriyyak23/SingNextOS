using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests;

internal static class TestFixtures
{
    public static SingProcessManifestV1 Manifest(
        ulong processId,
        ulong domainId,
        ulong generation = 1,
        string? identity = null,
        IEnumerable<CapabilityRequirementV1>? capabilities = null,
        IEnumerable<string>? contracts = null) =>
        new(new ProcessId(processId), new DomainId(domainId), generation, identity ?? $"entry-{processId}-{generation}", ExecutionRole.Sip, MemoryProfile.SipRegion, capabilities, contracts);

    public static (SingProcess Process, ProcessHandle Handle) Create(RuntimeKernel kernel, ulong processId, ulong domainId, ulong generation = 1, string? identity = null, IEnumerable<CapabilityRequirementV1>? capabilities = null)
    {
        SingProcessManifestV1 manifest = Manifest(processId, domainId, generation, identity, capabilities);
        KernelResult<SingProcess> result = kernel.CreateProcess(manifest);
        Assert.True(result.IsSuccess, result.Message);
        ProcessHandle handle = new ProcessHandle(manifest.ProcessId, manifest.Generation);
        return (result.Value!, handle);
    }
}
