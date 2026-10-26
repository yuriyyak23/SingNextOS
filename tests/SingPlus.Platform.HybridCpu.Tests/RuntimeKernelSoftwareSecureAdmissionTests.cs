using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class RuntimeKernelSoftwareSecureAdmissionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("normal")]
    [InlineData("lost-reply")]
    [InlineData("revoke")]
    [InlineData("route-loss")]
    public void PublicKernelRouteRetiresNoEffectAndReconcilesOriginalCleanup(string schedule)
    {
        var adapter = new HybridCpuExecutableChildAdapter();
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), adapter);
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 891, 892, 1);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var vm = kernel.CreateVirtualDomain(owner, create, new(1, 4096));
        Assert.True(vm.IsSuccess, vm.Message);
        Assert.True(kernel.ConfigureVirtualDomain(owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
        var cap = kernel.MintCapability(process.DomainId, owner, ResourceKind.SecureCompute,
            RuntimeKernel.SoftwareSecureAdmissionResource(vm.Value.Domain), CapabilityRights.Configure).Value!.CapabilityId;
        var budgetCap = kernel.MintCapability(process.DomainId, owner, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(owner, budgetCap, owner, "software-secure", [new(ServiceBudgetDimension.ExternalOperations, 1)]);
        Assert.True(budget.IsSuccess, budget.Message);
        int calls = 0;
        object? original = null; object? key = null;
        var routes = (IDictionary)typeof(RuntimeKernel).GetField("_softwareSecureRoutes", Private)!.GetValue(kernel)!;
        adapter.GetType().GetProperty("BeforeSoftwareSecureAdmissionPublication", Private)!.SetValue(adapter, (Action)(() =>
        {
            calls++;
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", Private)!.GetValue(kernel)!));
            var operation = (ExternalOperationHandle)routes.Keys.Cast<object>().Single();
            Assert.Equal(ExternalOperationState.Submitted, kernel.QueryExternalOperation(owner, operation).Value!.State);
            Assert.False(kernel.ReleaseExternalOperation(owner, operation, new(true, false)).IsSuccess);
            Assert.False(kernel.ExternalOperations.Release(operation, new(true, false)).IsSuccess);
            Assert.Equal(1UL, kernel.QueryBudget(budget.Value!.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.ExternalOperations).Used);
            int publications = 0;
            Assert.False(kernel.PublishExternalOperation(owner, operation, default, new(ExternalPublicationPolicy.Staged), () => publications++).IsSuccess);
            Assert.Equal(0, publications);
            if (schedule == "revoke") Assert.True(kernel.RevokeCapability(cap).IsSuccess);
            if (schedule == "route-loss") { key = operation; original = routes[operation]; routes.Remove(operation); }
            if (schedule == "lost-reply") throw new IOException("Lost original response after retire/cleanup.");
        }));
        var result = kernel.ObserveSoftwareSecureAdmission(owner, vm.Value.Domain, vm.Value.ConfigureCapability, cap, 19, 23);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, calls);
        Assert.False(result.Value!.IsExecutionAllowed);
        if (schedule == "route-loss")
        {
            Assert.False(result.Value.ResourcesClosed);
            Assert.Equal(1UL, kernel.QueryBudget(budget.Value!.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.ExternalOperations).Used);
            Assert.False(kernel.ResolveSoftwareSecureAdmission(owner, result.Value.Operation).IsSuccess);
            Assert.False(kernel.DestroyVirtualDomain(owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
            Assert.False(kernel.DestroyVirtualDomain(owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
            Assert.Equal(1UL, kernel.QueryBudget(budget.Value!.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.ExternalOperations).Used);
            routes[key!] = original;
        }
        adapter.GetType().GetProperty("BeforeSoftwareSecureAdmissionPublication", Private)!.SetValue(adapter, null);
        var resolved = kernel.ResolveSoftwareSecureAdmission(owner, result.Value.Operation);
        Assert.True(resolved.IsSuccess, resolved.Message);
        Assert.True(resolved.Value!.ResourcesClosed);
        Assert.Equal(schedule is "normal" or "lost-reply", resolved.Value.ExpectedDeny);
        Assert.Equal(schedule is "normal" or "lost-reply" ? 1UL : 0UL, resolved.Value.NoEffectCommit);
        Assert.Equal(1, calls);
        Assert.Equal(ExternalOperationState.Released, kernel.QueryExternalOperation(owner, result.Value.Operation).Value!.State);
        Assert.Equal(0UL, kernel.QueryBudget(budget.Value!.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.ExternalOperations).Used);
        Assert.True(kernel.ResolveSoftwareSecureAdmission(owner, result.Value.Operation).Value!.ResourcesClosed);
        Assert.False(kernel.ResolveSoftwareSecureAdmission(owner with { Generation = owner.Generation + 1 }, result.Value.Operation).IsSuccess);
        Assert.Equal(PlatformFeatureAvailability.Unavailable, provider.QueryFeatures().Resolve(PlatformFeatureFamily.SecureDomains).Availability);
        Assert.True(kernel.DestroyVirtualDomain(owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }

    [Fact]
    public void ForeignOrRevokedSemanticCapabilityNeverInvokesAdapter()
    {
        var adapter = new HybridCpuExecutableChildAdapter();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), adapter));
        var (process, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 893, 894, 1);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var vm = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value;
        Assert.True(kernel.ConfigureVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess);
        var cap = kernel.MintCapability(process.DomainId, owner, ResourceKind.SecureCompute,
            RuntimeKernel.SoftwareSecureAdmissionResource(vm.Domain), CapabilityRights.Configure).Value!.CapabilityId;
        Assert.False(kernel.ObserveSoftwareSecureAdmission(owner, vm.Domain, vm.ConfigureCapability, create, 1, 2).IsSuccess);
        Assert.False(kernel.PrepareExternalOperation(owner, [], ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).IsSuccess);
        Assert.True(kernel.RevokeCapability(cap).IsSuccess);
        Assert.False(kernel.ObserveSoftwareSecureAdmission(owner, vm.Domain, vm.ConfigureCapability, cap, 1, 2).IsSuccess);
        Assert.Empty((IDictionary)adapter.GetType().GetField("softwareAdmissions", Private)!.GetValue(adapter)!);
        Assert.True(kernel.DestroyVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess);
    }
}
