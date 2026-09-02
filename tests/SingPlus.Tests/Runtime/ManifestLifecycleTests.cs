using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class ManifestLifecycleTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    [Trait("Category", "Determinism")]
    public void ProcessManifestCanonicalizationIsStable()
    {
        var capsA = new[] { new CapabilityRequirementV1(ResourceKind.Device, "b", CapabilityRights.Read), new CapabilityRequirementV1(ResourceKind.Device, "a", CapabilityRights.Write) };
        var capsB = capsA.Reverse();
        var a = new SingProcessManifestV1(new ProcessId(1), new DomainId(1), 1, "entry", ExecutionRole.Sip, MemoryProfile.SipRegion, capsA, new[] { "Z", "A" });
        var b = new SingProcessManifestV1(new ProcessId(1), new DomainId(1), 1, "entry", ExecutionRole.Sip, MemoryProfile.SipRegion, capsB, new[] { "A", "Z" });
        Assert.Equal(a.SerializeCanonical(), b.SerializeCanonical());
        Assert.Equal(a.ComputeDigest(), b.ComputeDigest());
        Assert.Equal(a.ContractDigest, b.ContractDigest);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ManifestRejectsInvalidVersionDuplicateAndMemoryProfile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SingProcessManifestV1(new ProcessId(1), new DomainId(1), 1, "entry", ExecutionRole.Sip, MemoryProfile.SipRegion, schemaVersion: 2));
        Assert.Throws<ArgumentException>(() => new SingProcessManifestV1(new ProcessId(1), new DomainId(1), 1, "entry", ExecutionRole.Sip, MemoryProfile.SipRegion, requiredContracts: new[] { "A", "A" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SingProcessManifestV1(new ProcessId(1), new DomainId(1), 1, "entry", ExecutionRole.Sip, (MemoryProfile)999));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void LifecycleAcceptsValidTransitionsAndRejectsInvalidWithoutMutation()
    {
        var kernel = new RuntimeKernel();
        var (process, handle) = TestFixtures.Create(kernel, 1, 1);
        var invalid = kernel.StartProcess(handle);
        Assert.False(invalid.IsSuccess);
        Assert.Equal(ProcessState.Created, process.State);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.True(kernel.StartProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);
        Assert.True(kernel.ParkProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Parked, process.State);
        Assert.True(kernel.ResumeProcess(handle).IsSuccess);
        Assert.True(kernel.TerminateProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ProcessIdGenerationAndIdentityRulesRejectStaleOrDuplicateInstances()
    {
        var kernel = new RuntimeKernel();
        var (_, first) = TestFixtures.Create(kernel, 7, 7, identity: "same");
        var duplicateId = kernel.CreateProcess(TestFixtures.Manifest(7, 8, 1, "other"));
        Assert.Equal(KernelError.DuplicateProcessId, duplicateId.Error);
        var duplicateIdentity = kernel.CreateProcess(TestFixtures.Manifest(8, 8, 1, "same"));
        Assert.Equal(KernelError.DuplicateIdentity, duplicateIdentity.Error);
        Assert.True(kernel.TerminateProcess(first).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, kernel.CreateProcess(TestFixtures.Manifest(7, 7, 1, "same")).Error);
        Assert.True(kernel.CreateProcess(TestFixtures.Manifest(7, 7, 2, "same")).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void FaultPathRetiresGeneration()
    {
        var kernel = new RuntimeKernel();
        var (process, handle) = TestFixtures.Create(kernel, 9, 9);
        Assert.True(kernel.FaultProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Faulted, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }
}
