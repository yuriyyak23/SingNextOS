using System.Buffers;
using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class OriginalDmaCopyTests
{
    private static (NeutralDomainRuntimeFacade Runtime,NeutralOriginalDmaCopyLeg Source,NeutralOriginalDmaCopyLeg Destination) Pair()
    {
        var r = new NeutralDomainRuntimeFacade();
        var root = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),r.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        var domain = r.Bind(root).Observation.Lease;
        var device = r.BindDevice(domain,new("copy"),NeutralDeviceRights.Read | NeutralDeviceRights.Write | NeutralDeviceRights.Configure).Lease;
        NeutralOriginalDmaCopyLeg Leg(bool read)
        {
            var map = new NeutralOriginalOwnedRegionMapRequest(Guid.NewGuid(),r.MappingClosureIncarnation,root.InvocationId,domain,new(0,64,read ? NeutralMemoryAccess.Read : NeutralMemoryAccess.Write));
            var mapping = r.MapOwnedRegion(map).Observation.Lease;
            var bind = new NeutralOriginalDmaGrantRequest(Guid.NewGuid(),r.MappingClosureIncarnation,root.InvocationId,map.InvocationId,device,mapping,new(0,64),read ? NeutralDmaDirection.DeviceReadsMemory : NeutralDmaDirection.DeviceWritesMemory);
            var grant = r.BindDmaGrant(bind).Observation.Grant;
            var prepare = new NeutralOriginalDmaVisibilityRequest(Guid.NewGuid(),r.MappingClosureIncarnation,bind.InvocationId,grant,false,default);
            var visible = r.PrepareDmaVisibility(prepare);
            Assert.True(visible.Prepare!.IsPrepared);
            return new(bind,grant,prepare,visible.Prepare.Evidence.Cycle);
        }
        return (r,Leg(true),Leg(false));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CanonicalPreparedRefusalRetainsOriginalNoEffectWithoutNewByteUse(bool destination)
    {
        var p = Pair(); var input = Enumerable.Repeat((byte)0x51,64).ToArray(); var output = new byte[64];
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = p.Runtime.GetType().GetField("_dependencies",flags)!.GetValue(p.Runtime)!;
        var grants = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
        var owner = grants[(destination ? p.Destination : p.Source).Grant.Handle]!;
        var history = (IList)owner.GetType().GetProperty("OriginalVisibilityHistory")!.GetValue(owner)!;
        history.Add(history[0]); owner.GetType().GetProperty("OriginalVisibilityIssued")!.SetValue(owner,(ulong)history.Count);
        var q = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,input,output);
        var raw = p.Runtime.CopyDma(q); Assert.True(raw.NoEffectGuaranteed); Assert.True(raw.NoFurtherByteUseGuaranteed); Assert.False(raw.Completed);
        Assert.True(q.SourceBytes.IsEmpty); Assert.True(q.DestinationBytes.IsEmpty); Assert.All(output,b => Assert.Equal((byte)0,b));
        Assert.Same(raw,p.Runtime.ResolveDmaCopy(q)); Assert.Same(raw,p.Runtime.CopyDma(q)); Assert.Null(p.Runtime.ValidateDmaCopy(q));
        var copy = new NeutralOriginalDmaCopyRequest(q.InvocationId,q.RuntimeIncarnation,q.Source,q.Destination,input,output);
        Assert.False(p.Runtime.CopyDma(copy).NoEffectGuaranteed); Assert.Null(p.Runtime.ResolveDmaCopy(copy));
        foreach(var leg in new[]{p.Source,p.Destination})
            Assert.Equal(NeutralDmaGrantCloseDecision.Closed,p.Runtime.CloseDmaGrant(new NeutralOriginalDmaGrantCloseRequest(Guid.NewGuid(),q.RuntimeIncarnation,leg.Admission.InvocationId,leg.Grant)).Observation.Decision);
        Assert.Same(raw,p.Runtime.ResolveDmaCopy(q)); Assert.Same(raw,p.Runtime.CopyDma(q)); Assert.Equal(0,p.Runtime.ActiveDmaGrantCount);
    }

    [Fact]
    public void OriginalCopyExecutesBytesOnceAndCompletedLifetimeSurvivesAcquireAndClose()
    {
        var p = Pair(); var input = Enumerable.Repeat((byte)0x51,64).ToArray(); var output = new byte[64];
        var q = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,input,output);
        var raw = p.Runtime.CopyDma(q);
        Assert.True(raw.Completed); Assert.True(raw.NoFurtherByteUseGuaranteed); Assert.False(raw.NoEffectGuaranteed);
        Assert.Equal(input,output); Assert.True(q.SourceBytes.IsEmpty); Assert.True(q.DestinationBytes.IsEmpty);
        Assert.Same(raw,p.Runtime.ResolveDmaCopy(q)); Assert.Same(raw,p.Runtime.ValidateDmaCopy(q));
        Array.Fill(input,(byte)0x73);
        Assert.Same(raw,p.Runtime.CopyDma(q)); Assert.All(output,b => Assert.Equal((byte)0x51,b));
        var copy = new NeutralOriginalDmaCopyRequest(q.InvocationId,q.RuntimeIncarnation,q.Source,q.Destination,input,output);
        Assert.False(p.Runtime.CopyDma(copy).NoEffectGuaranteed); Assert.Null(p.Runtime.ResolveDmaCopy(copy));
        Assert.True(p.Runtime.AcquireDmaVisibility(new NeutralOriginalDmaVisibilityRequest(Guid.NewGuid(),q.RuntimeIncarnation,p.Destination.Admission.InvocationId,p.Destination.Grant,true,p.Destination.Cycle)).Acquire!.IsAcquired);
        Assert.Null(p.Runtime.ValidateDmaCopy(q)); Assert.Same(raw,p.Runtime.ResolveDmaCopy(q));
        foreach (var leg in new[] {p.Source,p.Destination}) Assert.Equal(NeutralDmaGrantCloseDecision.Closed,p.Runtime.CloseDmaGrant(new NeutralOriginalDmaGrantCloseRequest(Guid.NewGuid(),q.RuntimeIncarnation,leg.Admission.InvocationId,leg.Grant)).Observation.Decision);
        Assert.Same(raw,p.Runtime.ResolveDmaCopy(q)); Assert.Equal(0,p.Runtime.ActiveDmaGrantCount);
    }

    [Theory]
    [InlineData("history")] [InlineData("owner")] [InlineData("cycle")] [InlineData("exhaustion")]
    public void OriginalCopyHistoryOrOwnerLossNeverRepeatsByteEffect(string fault)
    {
        var p = Pair(); var input = Enumerable.Repeat((byte)0x51,64).ToArray(); var output = new byte[64];
        var q = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,input,output);
        var raw = p.Runtime.CopyDma(q); Assert.True(raw.Completed);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = p.Runtime.GetType().GetField("_dependencies",flags)!.GetValue(p.Runtime)!;
        var owners = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
        var owner = owners[p.Source.Grant.Handle]!;
        var history = (IList)owner.GetType().GetProperty("OriginalCopies")!.GetValue(owner)!;
        if (fault == "history") history.Clear();
        if (fault == "owner") owners[p.Source.Grant.Handle] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(owner,null);
        if (fault is "cycle" or "exhaustion")
        {
            var cycle = owner.GetType().GetProperty("Visibility")!.GetValue(owner)!;
            if (fault == "exhaustion") cycle.GetType().GetProperty("Revision")!.SetValue(cycle,ulong.MaxValue);
            cycle.GetType().GetProperty("Acquired")!.SetValue(cycle,false);
            Assert.Null(p.Runtime.ValidateDmaCopy(q)); // Completed lookup is independent of later visibility revisions.
        }
        Array.Fill(input,(byte)0x77);
        var newRequest = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),q.RuntimeIncarnation,q.Source,q.Destination,input,output);
        var refused = p.Runtime.CopyDma(newRequest); Assert.False(refused.Completed);
        Assert.All(output,b => Assert.Equal((byte)0x51,b));
        if (fault is "history" or "owner") Assert.Null(p.Runtime.ResolveDmaCopy(q));
        else Assert.Same(raw,p.Runtime.ResolveDmaCopy(q));
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void MalformedIndependentCopyHistoryRefusesNewByteEffect(bool destination,bool late)
    {
        var p = Pair(); var shared = new byte[64];
        var old = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,shared,shared);
        Assert.True(p.Runtime.CopyDma(old).NoEffectGuaranteed); // Actual retained overlap refusal.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = p.Runtime.GetType().GetField("_dependencies",flags)!.GetValue(p.Runtime)!;
        var grants = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
        var owner = grants[(destination ? p.Destination : p.Source).Grant.Handle]!;
        var history = (IList)owner.GetType().GetProperty("OriginalCopies")!.GetValue(owner)!;
        void Corrupt()
        {
            history.Add(history[0]);
            owner.GetType().GetProperty("OriginalCopiesIssued")!.SetValue(owner,(ulong)history.Count);
        }
        using var memory = new CallbackMemory(Enumerable.Repeat((byte)0x51,64).ToArray());
        var reads = 0; var input = memory.Memory; var output = new byte[64];
        memory.OnRead = () => { reads++; if(late) Corrupt(); };
        if(!late) Corrupt();
        var q = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,input,output);
        var raw = p.Runtime.CopyDma(q);
        Assert.False(raw.Completed); Assert.Equal(late,raw.NoEffectGuaranteed);
        Assert.Equal(late ? 1 : 0,reads); Assert.All(output,b => Assert.Equal((byte)0,b));
        Assert.Null(p.Runtime.ResolveDmaCopy(q)); Assert.Null(p.Runtime.ValidateDmaCopy(q));
        Assert.False(p.Runtime.CopyDma(q).Completed); Assert.All(output,b => Assert.Equal((byte)0,b));
        Assert.Equal(late ? 3 : 2,history.Count);
        Assert.Null(owner.GetType().GetProperty("PendingCopy")!.GetValue(owner));
    }

    private sealed class CallbackMemory(byte[] bytes) : MemoryManager<byte>
    {
        public Action? OnRead { get; set; }
        public override Span<byte> GetSpan() { var action = OnRead; OnRead = null; action?.Invoke(); return bytes; }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalCopyMemoryCallbacksRunOutsideLockAndLateCycleDriftRefusesEffect(bool drift)
    {
        var p = Pair(); using var memory = new CallbackMemory(Enumerable.Repeat((byte)0x51,64).ToArray()); var output = new byte[64];
        var q = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,memory.Memory,output);
        memory.OnRead = () =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.False(Monitor.IsEntered(p.Runtime.GetType().GetField("_lifecycleGate",flags)!.GetValue(p.Runtime)!));
            Assert.False(p.Runtime.CopyDma(q).Completed);
            Assert.False(p.Runtime.PrepareDmaVisibility(p.Source.Grant).IsPrepared);
            Assert.NotEqual(NeutralDmaGrantCloseDecision.Closed,p.Runtime.CloseDmaGrant(p.Source.Grant).Decision);
            if (drift)
            {
                var registry = p.Runtime.GetType().GetField("_dependencies",flags)!.GetValue(p.Runtime)!;
                var owners = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
                var owner = owners[p.Source.Grant.Handle]!; var cycle = owner.GetType().GetProperty("Visibility")!.GetValue(owner)!;
                cycle.GetType().GetProperty("Acquired")!.SetValue(cycle,true);
            }
        };
        var raw = p.Runtime.CopyDma(q); Assert.Equal(!drift,raw.Completed); Assert.Equal(drift,raw.NoEffectGuaranteed);
        Assert.All(output,b => Assert.Equal(drift ? (byte)0 : (byte)0x51,b));
        Assert.Same(raw,p.Runtime.ResolveDmaCopy(q));
    }
}
