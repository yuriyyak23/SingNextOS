using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class SoftwareSecureRegisterLocalPublicationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("normal", 19UL, 23UL)]
    [InlineData("wrap", ulong.MaxValue, 1UL)]
    [InlineData("high-bit", 0x8000000000000000UL, 0x8000000000000000UL)]
    [InlineData("lost-native-reply", 19UL, 23UL)]
    [InlineData("revoke-before-publication", 19UL, 23UL)]
    [InlineData("revoke-after-staging", 19UL, 23UL)]
    [InlineData("bridge-aba", 19UL, 23UL)]
    [InlineData("bridge-root-aba", 19UL, 23UL)]
    [InlineData("late-source", 19UL, 23UL)]
    [InlineData("missing-core-observation", 19UL, 23UL)]
    [InlineData("child-core-aba", 19UL, 23UL)]
    public void RetireAndOsPublicationAreSeparateOriginalOwners(string schedule, ulong left, ulong right)
    {
        var adapter = new HybridCpuExecutableChildAdapter();
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), adapter);
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 895, 896, 1);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var vm = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value;
        Assert.True(kernel.ConfigureVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess);
        var execute = kernel.MintCapability(process.DomainId, owner, ResourceKind.SecureCompute,
            RuntimeKernel.SoftwareSecureRegisterLocalResource(vm.Domain), CapabilityRights.Execute).Value!.CapabilityId;
        var budgetCap = kernel.MintCapability(process.DomainId, owner, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(owner, budgetCap, owner, "software-arithmetic", [new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!;
        int machineCallbacks = 0, stages = 0;
        object? record = null;
        adapter.GetType().GetProperty("BeforeSoftwareSecureAdmissionPublication", Hidden)!.SetValue(adapter, (Action)(() =>
        {
            machineCallbacks++;
            var routes = (IDictionary)typeof(RuntimeKernel).GetField("_softwareSecureRoutes", Hidden)!.GetValue(kernel)!;
            record = routes.Values.Cast<object>().Single();
            var operation = (ExternalOperationHandle)record.GetType().GetField("Operation", Hidden)!.GetValue(record)!;
            Assert.Null(record.GetType().GetField("PublishedResult", Hidden)!.GetValue(record));
            Assert.Equal(ExternalOperationState.Submitted, kernel.QueryExternalOperation(owner, operation).Value!.State);
            Assert.Equal(1UL, kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.ExternalOperations).Used);
            // Actual native retire is already known to the adapter; it has not written an OS result.
            var nativeFacts = (IDictionary)adapter.GetType().GetField("softwareAdmissions", Hidden)!.GetValue(adapter)!;
            object native = nativeFacts.Values.Cast<object>().Single();
            var original = (NeutralSoftwareSecureAdmissionRequest)native.GetType().GetField("Request", Hidden)!.GetValue(native)!;
            Assert.False(adapter.ObserveSoftwareSecureAdmission(original).ResourcesClosed);
            Assert.Equal(NeutralSoftwareSecureAdmissionStatus.CleanupRequired, adapter.ExecuteSoftwareSecureRegisterLocal(original).Status);
            var admission = (SecureComputeCanonicalAdmissionResult)native.GetType().GetField("Admission", Hidden)!.GetValue(native)!;
            Assert.True(admission.IsAllowed);
            Assert.Equal(unchecked(left + right), admission.RegisterLocalRetirement!.Register3Value);
            Assert.Equal(1, admission.RegisterLocalRetirement.EffectCount);
            Assert.Same(admission.Request, admission.BackendAttempt!.Request);
            Assert.Same(admission.BackendAttempt, admission.RegisterLocalRetirement.Submission.Attempt);
            Assert.Equal(original.Operation, admission.Request.Operation.Handle.Value);
            Assert.Equal(original.Generation, admission.Request.Operation.Generation.Value);
            Assert.Equal(left, admission.Request.Left);
            Assert.Equal(right, admission.Request.Right);
            var host = (SoftwareSecureRegisterLocalCoreHost)native.GetType().GetField("CoreHost", Hidden)!.GetValue(native)!;
            var coreObservation = (SoftwareSecureRegisterLocalCoreObservation)native.GetType().GetField("CoreObservation", Hidden)!.GetValue(native)!;
            Assert.Same(admission.Request, coreObservation.Request);
            Assert.Same(admission, coreObservation.Admission);
            Assert.Same(coreObservation, host.QueryCoreObservation(admission.Request, admission));
            Assert.Equal(unchecked(left + right), coreObservation.CoreValue);
            Assert.Equal(0, admission.BackendAttempt.OwnerContext);
            Assert.Equal(2, admission.BackendAttempt.SourceSlot);
            if (schedule == "missing-core-observation") native.GetType().GetField("CoreObservation", Hidden)!.SetValue(native, null);
            if (schedule == "child-core-aba")
            {
                object child = native.GetType().GetField("Child", Hidden)!.GetValue(native)!;
                var state = child.GetType().GetProperty("State")!;
                state.SetValue(child, state.GetValue(child));
            }
            if (schedule == "lost-native-reply") throw new IOException("Lost native post-retire reply.");
        }));
        typeof(RuntimeKernel).GetProperty("BeforeSoftwareSecureRegisterLocalPublication", Hidden)!.SetValue(kernel, (Action)(() =>
        {
            Assert.NotNull(record);
            Assert.Null(record!.GetType().GetField("PublishedResult", Hidden)!.GetValue(record));
            if (schedule == "revoke-before-publication") Assert.True(kernel.RevokeCapability(execute).IsSuccess);
            if (schedule == "late-source") Assert.False(kernel.DestroyVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess);
        }));
        typeof(RuntimeKernel).GetProperty("AfterSoftwareSecureRegisterLocalStaging", Hidden)!.SetValue(kernel, (Action)(() =>
        {
            stages++;
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", Hidden)!.GetValue(kernel)!));
            Assert.Null(record!.GetType().GetField("PublishedResult", Hidden)!.GetValue(record));
            if (schedule == "revoke-after-staging") Assert.True(kernel.RevokeCapability(execute).IsSuccess);
            if (schedule is "bridge-aba" or "bridge-root-aba")
            {
                string field = schedule == "bridge-aba" ? "_childBindings" : "_domains";
                string property = schedule == "bridge-aba" ? "State" : "AuthorityState";
                var bindings = (IDictionary)typeof(PlatformAuthorityBridge).GetField(field, Hidden)!.GetValue(kernel.PlatformAuthority)!;
                object source = bindings.Values.Cast<object>().Single();
                var state = source.GetType().GetProperty(property)!;
                state.SetValue(source, state.GetValue(source)); // Same value, fresh owner revision.
            }
        }));
        var result = kernel.ExecuteSoftwareSecureRegisterLocal(owner, vm.Domain, vm.ConfigureCapability, execute, left, right);
        Assert.True(result.IsSuccess, result.Message);
        adapter.GetType().GetProperty("BeforeSoftwareSecureAdmissionPublication", Hidden)!.SetValue(adapter, null);
        var resolved = kernel.ResolveSoftwareSecureRegisterLocal(owner, result.Value!.Operation);
        Assert.True(resolved.IsSuccess, resolved.Message);
        Assert.True(resolved.Value!.ResourcesClosed);
        bool positive = schedule is "normal" or "wrap" or "high-bit";
        Assert.Equal(positive, resolved.Value.WasPublished);
        Assert.Equal(positive ? unchecked(left + right) : (ulong?)null, resolved.Value.Value);
        Assert.Equal(positive ? 1UL : 0UL, resolved.Value.RegisterLocalCommit);
        Assert.Equal(1, machineCallbacks);
        Assert.Equal(positive || schedule is "revoke-after-staging" or "bridge-aba" or "bridge-root-aba" ? 1 : 0, stages);
        Assert.Equal(ExternalOperationState.Released, kernel.QueryExternalOperation(owner, result.Value.Operation).Value!.State);
        Assert.Equal(0UL, kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.ExternalOperations).Used);
        Assert.Equal(resolved.Value, kernel.ResolveSoftwareSecureRegisterLocal(owner, result.Value.Operation).Value);
        Assert.Equal(1, machineCallbacks);
        Assert.True(kernel.DestroyVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(PlatformFeatureAvailability.Unavailable, provider.QueryFeatures().Resolve(PlatformFeatureFamily.SecureDomains).Availability);
    }

    [Fact]
    public void NoEffectCapabilityCannotAuthorizeArithmetic()
    {
        var adapter = new HybridCpuExecutableChildAdapter();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), adapter));
        var (process, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 897, 898, 1);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var vm = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value;
        Assert.True(kernel.ConfigureVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess);
        var noEffect = kernel.MintCapability(process.DomainId, owner, ResourceKind.SecureCompute,
            RuntimeKernel.SoftwareSecureAdmissionResource(vm.Domain), CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        Assert.False(kernel.ExecuteSoftwareSecureRegisterLocal(owner, vm.Domain, vm.ConfigureCapability, noEffect, 1, 2).IsSuccess);
        Assert.Empty((IDictionary)adapter.GetType().GetField("softwareAdmissions", Hidden)!.GetValue(adapter)!);
        Assert.True(kernel.DestroyVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess);
    }
}
