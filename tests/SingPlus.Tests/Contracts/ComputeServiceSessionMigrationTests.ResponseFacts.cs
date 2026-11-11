using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;
using SpyTypes =SingPlus.Tests.Platform.PlatformDsc1ComputeTests;

namespace SingPlus.Tests.Contracts;
public sealed partial class ComputeServiceSessionMigrationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OriginalHostContinuationRequiresExactResponseFactEvenAfterDelivery(bool publicationCopy)
    {
        SpyTypes.IOriginalDsc1Runtime runtime =DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime,SpyTypes.Dsc1NativeSpy>();
        SpyTypes.Dsc1NativeSpy spy =(SpyTypes.Dsc1NativeSpy)(object)runtime;spy.Native=new();
        ComponentScenario s =CreateComponentScenario(new HybridCpuPlatformAuthorityProvider(runtime));
        s.Source.Span.Fill(0x63);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        FieldInfo pendingRoute =typeof(RuntimeComputeServiceHost).GetField("_pending",flags)!;
        object? pending=null;spy.AfterCopy=()=>pending=pendingRoute.GetValue(s.Host);
        Task<OwnedBuffer<byte>> reply =ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session)).CopyAsync(s.Source,s.Destination).AsTask();
        Assert.True(s.Host.ProcessNextCopy().IsSuccess);Assert.NotNull(pending);
        OwnedBuffer<byte> output =await reply.WaitAsync(TimeSpan.FromSeconds(2));Assert.All(output.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        object registry =typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(s.Kernel)!;
        IDictionary records =(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        object record =records[(s.Session.SessionId,s.Session.Generation,new EndpointSessionInvocationId(1))]!;
        PropertyInfo property =record.GetType().GetProperty(publicationCopy?"OriginalResponse":"SettlementOutcome")!;
        object original =property.GetValue(record)!;
        property.SetValue(record,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
        // Restore the same surviving original host continuation; delivery is a
        // separate completed fact and cannot authenticate a substituted raw fact.
        pendingRoute.SetValue(s.Host,pending);
        Assert.False(s.Host.AdvancePendingCopy().IsSuccess);Assert.True(s.Host.HasPendingCopy);
        Assert.Equal(1,spy.Copies);Assert.All(output.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        property.SetValue(record,original);
        Assert.True(s.Host.AdvancePendingCopy().IsSuccess);Assert.False(s.Host.HasPendingCopy);Assert.Equal(1,spy.Copies);
        Assert.True(s.Kernel.ReleaseRegion(s.Caller,output).IsSuccess);
        Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
        Assert.True(s.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);Assert.Equal(0,spy.Native.ActiveBindingCount);
    }
}
