using System.Collections.Concurrent;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class ProcessAdmissionCommitTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void AdmissionUsesActualFreshSourceAndLeavesMissingRequirementUnmutated(int fault)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        CapabilityRequirementV1 requirement = new CapabilityRequirementV1(ResourceKind.Device, "required", CapabilityRights.Read);
        (Sip.SingProcess? process, ProcessHandle handle) = TestFixtures.Create(kernel, 1, 10, capabilities: [requirement]);
        CapabilityDescriptorV1 minted = kernel.CapabilityAuthority.Mint(new(10), new(10), ResourceKind.Device, "required",
            CapabilityRights.Read, fault == 3 ? 2UL : 1UL).Value!;
        CapabilityDescriptorV1 descriptor = Assert.Single(kernel.CapabilityAuthority.SnapshotForDomain(new(10)));
        if (fault == 0) kernel.CapabilityAuthority.Revoke(minted.CapabilityId);
        if (fault == 1) kernel.CapabilityAuthority.RevokeAllForDomain(new(10));
        if (fault == 2) kernel.CapabilityAuthority.Retire(minted.CapabilityId);
        Assert.Equal(minted, descriptor);
        Assert.Equal(KernelError.MissingCapability, kernel.AdmitProcess(handle).Error);
        Assert.Equal(ProcessState.Created, process.State);
        Assert.Equal(0UL, kernel.CapabilityAuthority.InspectConstraints(minted.CapabilityId)!.Value.HandleConsumed);
        Assert.True(kernel.MintCapability(new(10), handle, ResourceKind.Device, "required", CapabilityRights.Read).IsSuccess);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Admitted, process.State);
    }

    [Fact]
    public void FullRequirementSetCommitsOnceWithoutQuotaConsumption()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (Sip.SingProcess? process, ProcessHandle handle) = TestFixtures.Create(kernel, 1, 10, capabilities:
            [new(ResourceKind.Device, "first", CapabilityRights.Read), new(ResourceKind.File, "second", CapabilityRights.Write)]);
        CapabilityDescriptorV1 first = kernel.CapabilityAuthority.Mint(new(10), new(10), ResourceKind.Device, "first",
            CapabilityRights.Read, 1, 1, null, null, 1, 0).Value!;
        Assert.Equal(KernelError.MissingCapability, kernel.AdmitProcess(handle).Error);
        Assert.Equal(ProcessState.Created, process.State);
        Assert.Equal(1UL, kernel.CapabilityAuthority.InspectConstraints(first.CapabilityId)!.Value.SharedRemaining);
        Assert.True(kernel.MintCapability(new(10), handle, ResourceKind.File, "second", CapabilityRights.Write).IsSuccess);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Admitted, process.State);
        Assert.Equal(1UL, kernel.CapabilityAuthority.InspectConstraints(first.CapabilityId)!.Value.SharedRemaining);
        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
    }

    [Fact]
    public void ConcurrentAdmissionsPublishOneStateTransition()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (Sip.SingProcess? process, ProcessHandle handle) = TestFixtures.Create(kernel, 1, 10);
        ConcurrentBag<KernelResult> results = new ConcurrentBag<KernelResult>();
        Parallel.For(0, 32, _ => results.Add(kernel.AdmitProcess(handle)));
        Assert.Single(results, r => r.IsSuccess);
        Assert.Equal(31, results.Count(r => r.Error == KernelError.InvalidTransition));
        Assert.Equal(ProcessState.Admitted, process.State);
    }

    [Fact]
    public async Task RealRevocationPrecedesSourceCommitAndLifecycleStopCannotInterleaveStateMutation()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (Sip.SingProcess? process, ProcessHandle handle) = TestFixtures.Create(kernel, 1, 10,
            capabilities: [new(ResourceKind.Device, "required", CapabilityRights.Read)]);
        CapabilityDescriptorV1 capability = kernel.MintCapability(new(10), handle, ResourceKind.Device, "required", CapabilityRights.Read).Value!;
        object gate = typeof(CapabilityAuthority).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.CapabilityAuthority)!;
        TaskCompletionSource<KernelResult> admitted = new TaskCompletionSource<KernelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<KernelResult> stopped = new TaskCompletionSource<KernelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread admitting = new Thread(() => { try { admitted.SetResult(kernel.AdmitProcess(handle)); } catch (Exception e) { admitted.SetException(e); } }) { IsBackground = true };
        Thread stopping = new Thread(() => { try { stopped.SetResult(kernel.TerminateProcess(handle)); } catch (Exception e) { stopped.SetException(e); } }) { IsBackground = true };
        lock (gate)
        {
            admitting.Start();
            Assert.True(SpinWait.SpinUntil(() => (admitting.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
            stopping.Start();
            Assert.True(SpinWait.SpinUntil(() => (stopping.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
            Assert.Equal(ProcessState.Created, process.State);
            Assert.False(admitted.Task.IsCompleted);
            Assert.False(stopped.Task.IsCompleted);
            Assert.True(kernel.CapabilityAuthority.Revoke(capability.CapabilityId).IsSuccess);
        }
        Assert.Equal(KernelError.MissingCapability, (await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        Assert.True((await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnknownOrEmptyCapturedMandatoryRequirementCannotAdmit(bool unknown)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (Sip.SingProcess? process, ProcessHandle handle) = TestFixtures.Create(kernel, 1, 10, capabilities:
            [new(ResourceKind.Device, "required", CapabilityRights.Read)]);
        Assert.True(kernel.MintCapability(new(10), handle, ResourceKind.Device, "required", CapabilityRights.Read).IsSuccess);
        // Trusted fault injection corrupts the actual private owner input; the
        // normal public view is immutable after C0 hardening.
        CapabilityRequirementV1[] exposed = (CapabilityRequirementV1[])typeof(SingProcessManifestV1).GetField("_requiredCapabilities",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(process.Manifest)!;
        exposed[0] = unknown ? new((ResourceKind)int.MaxValue, "required", CapabilityRights.Read) :
            new(ResourceKind.Device, "required", CapabilityRights.None);
        Assert.Equal(KernelError.MissingCapability, kernel.AdmitProcess(handle).Error);
        Assert.Equal(ProcessState.Created, process.State);
    }
}

