using System.Buffers;
using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class OriginalDmaCopyTests
{
    private static (NeutralDomainRuntimeFacade Runtime,NeutralOriginalDmaCopyLeg Source,NeutralOriginalDmaCopyLeg Destination) Pair()
    {
        NeutralDomainRuntimeFacade r = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest root = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),r.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease domain = r.Bind(root).Observation.Lease;
        NeutralDeviceLease device = r.BindDevice(domain,new("copy"),NeutralDeviceRights.Read | NeutralDeviceRights.Write | NeutralDeviceRights.Configure).Lease;
        NeutralOriginalDmaCopyLeg Leg(bool read)
        {
            NeutralOriginalOwnedRegionMapRequest map = new NeutralOriginalOwnedRegionMapRequest(Guid.NewGuid(),r.MappingClosureIncarnation,root.InvocationId,domain,new(0,64,read ? NeutralMemoryAccess.Read : NeutralMemoryAccess.Write));
            NeutralOwnedRegionMappingLease mapping = r.MapOwnedRegion(map).Observation.Lease;
            NeutralOriginalDmaGrantRequest bind = new NeutralOriginalDmaGrantRequest(Guid.NewGuid(),r.MappingClosureIncarnation,root.InvocationId,map.InvocationId,device,mapping,new(0,64),read ? NeutralDmaDirection.DeviceReadsMemory : NeutralDmaDirection.DeviceWritesMemory);
            NeutralDmaGrant grant = r.BindDmaGrant(bind).Observation.Grant;
            NeutralOriginalDmaVisibilityRequest prepare = new NeutralOriginalDmaVisibilityRequest(Guid.NewGuid(),r.MappingClosureIncarnation,bind.InvocationId,grant,false,default);
            NeutralOriginalDmaVisibilityObservation visible = r.PrepareDmaVisibility(prepare);
            Assert.True(visible.Prepare!.IsPrepared);
            return new(bind,grant,prepare,visible.Prepare.Evidence.Cycle);
        }
        return (r,Leg(true),Leg(false));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CanonicalPreparedRefusalRetainsOriginalNoEffectWithoutNewByteUse(bool destination)
    {
        (NeutralDomainRuntimeFacade Runtime, NeutralOriginalDmaCopyLeg Source, NeutralOriginalDmaCopyLeg Destination) p = Pair(); byte[] input = Enumerable.Repeat((byte)0x51,64).ToArray(); byte[] output = new byte[64];
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = p.Runtime.GetType().GetField("_dependencies",flags)!.GetValue(p.Runtime)!;
        IDictionary grants = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
        object owner = grants[(destination ? p.Destination : p.Source).Grant.Handle]!;
        IList history = (IList)owner.GetType().GetProperty("OriginalVisibilityHistory")!.GetValue(owner)!;
        history.Add(history[0]); owner.GetType().GetProperty("OriginalVisibilityIssued")!.SetValue(owner,(ulong)history.Count);
        NeutralOriginalDmaCopyRequest q = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,input,output);
        NeutralOriginalDmaCopyObservation raw = p.Runtime.CopyDma(q); Assert.True(raw.NoEffectGuaranteed); Assert.True(raw.NoFurtherByteUseGuaranteed); Assert.False(raw.Completed);
        Assert.True(q.SourceBytes.IsEmpty); Assert.True(q.DestinationBytes.IsEmpty); Assert.All(output,b => Assert.Equal((byte)0,b));
        Assert.Same(raw,p.Runtime.ResolveDmaCopy(q)); Assert.Same(raw,p.Runtime.CopyDma(q)); Assert.Null(p.Runtime.ValidateDmaCopy(q));
        NeutralOriginalDmaCopyRequest copy = new NeutralOriginalDmaCopyRequest(q.InvocationId,q.RuntimeIncarnation,q.Source,q.Destination,input,output);
        Assert.False(p.Runtime.CopyDma(copy).NoEffectGuaranteed); Assert.Null(p.Runtime.ResolveDmaCopy(copy));
        foreach(NeutralOriginalDmaCopyLeg leg in new[]{p.Source,p.Destination})
            Assert.Equal(NeutralDmaGrantCloseDecision.Closed,p.Runtime.CloseDmaGrant(new NeutralOriginalDmaGrantCloseRequest(Guid.NewGuid(),q.RuntimeIncarnation,leg.Admission.InvocationId,leg.Grant)).Observation.Decision);
        Assert.Same(raw,p.Runtime.ResolveDmaCopy(q)); Assert.Same(raw,p.Runtime.CopyDma(q)); Assert.Equal(0,p.Runtime.ActiveDmaGrantCount);
    }

    [Fact]
    public void OriginalCopyExecutesBytesOnceAndCompletedLifetimeSurvivesAcquireAndClose()
    {
        (NeutralDomainRuntimeFacade Runtime, NeutralOriginalDmaCopyLeg Source, NeutralOriginalDmaCopyLeg Destination) p = Pair(); byte[] input = Enumerable.Repeat((byte)0x51,64).ToArray(); byte[] output = new byte[64];
        NeutralOriginalDmaCopyRequest q = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,input,output);
        NeutralOriginalDmaCopyObservation raw = p.Runtime.CopyDma(q);
        Assert.True(raw.Completed); Assert.True(raw.NoFurtherByteUseGuaranteed); Assert.False(raw.NoEffectGuaranteed);
        Assert.Equal(input,output); Assert.True(q.SourceBytes.IsEmpty); Assert.True(q.DestinationBytes.IsEmpty);
        Assert.Same(raw,p.Runtime.ResolveDmaCopy(q)); Assert.Same(raw,p.Runtime.ValidateDmaCopy(q));
        Array.Fill(input,(byte)0x73);
        Assert.Same(raw,p.Runtime.CopyDma(q)); Assert.All(output,b => Assert.Equal((byte)0x51,b));
        NeutralOriginalDmaCopyRequest copy = new NeutralOriginalDmaCopyRequest(q.InvocationId,q.RuntimeIncarnation,q.Source,q.Destination,input,output);
        Assert.False(p.Runtime.CopyDma(copy).NoEffectGuaranteed); Assert.Null(p.Runtime.ResolveDmaCopy(copy));
        Assert.True(p.Runtime.AcquireDmaVisibility(new NeutralOriginalDmaVisibilityRequest(Guid.NewGuid(),q.RuntimeIncarnation,p.Destination.Admission.InvocationId,p.Destination.Grant,true,p.Destination.Cycle)).Acquire!.IsAcquired);
        Assert.Null(p.Runtime.ValidateDmaCopy(q)); Assert.Same(raw,p.Runtime.ResolveDmaCopy(q));
        foreach (NeutralOriginalDmaCopyLeg leg in new[] {p.Source,p.Destination}) Assert.Equal(NeutralDmaGrantCloseDecision.Closed,p.Runtime.CloseDmaGrant(new NeutralOriginalDmaGrantCloseRequest(Guid.NewGuid(),q.RuntimeIncarnation,leg.Admission.InvocationId,leg.Grant)).Observation.Decision);
        Assert.Same(raw,p.Runtime.ResolveDmaCopy(q)); Assert.Equal(0,p.Runtime.ActiveDmaGrantCount);
    }

    [Theory]
    [InlineData("history")] [InlineData("owner")] [InlineData("cycle")] [InlineData("exhaustion")]
    public void OriginalCopyHistoryOrOwnerLossNeverRepeatsByteEffect(string fault)
    {
        (NeutralDomainRuntimeFacade Runtime, NeutralOriginalDmaCopyLeg Source, NeutralOriginalDmaCopyLeg Destination) p = Pair(); byte[] input = Enumerable.Repeat((byte)0x51,64).ToArray(); byte[] output = new byte[64];
        NeutralOriginalDmaCopyRequest q = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,input,output);
        NeutralOriginalDmaCopyObservation raw = p.Runtime.CopyDma(q); Assert.True(raw.Completed);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = p.Runtime.GetType().GetField("_dependencies",flags)!.GetValue(p.Runtime)!;
        IDictionary owners = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
        object owner = owners[p.Source.Grant.Handle]!;
        IList history = (IList)owner.GetType().GetProperty("OriginalCopies")!.GetValue(owner)!;
        if (fault == "history") history.Clear();
        if (fault == "owner") owners[p.Source.Grant.Handle] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(owner,null);
        if (fault is "cycle" or "exhaustion")
        {
            object cycle = owner.GetType().GetProperty("Visibility")!.GetValue(owner)!;
            if (fault == "exhaustion") cycle.GetType().GetProperty("Revision")!.SetValue(cycle,ulong.MaxValue);
            cycle.GetType().GetProperty("Acquired")!.SetValue(cycle,false);
            Assert.Null(p.Runtime.ValidateDmaCopy(q)); // Completed lookup is independent of later visibility revisions.
        }
        Array.Fill(input,(byte)0x77);
        NeutralOriginalDmaCopyRequest newRequest = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),q.RuntimeIncarnation,q.Source,q.Destination,input,output);
        NeutralOriginalDmaCopyObservation refused = p.Runtime.CopyDma(newRequest); Assert.False(refused.Completed);
        Assert.All(output,b => Assert.Equal((byte)0x51,b));
        if (fault is "history" or "owner") Assert.Null(p.Runtime.ResolveDmaCopy(q));
        else Assert.Same(raw,p.Runtime.ResolveDmaCopy(q));
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void MalformedIndependentCopyHistoryRefusesNewByteEffect(bool destination,bool late)
    {
        (NeutralDomainRuntimeFacade Runtime, NeutralOriginalDmaCopyLeg Source, NeutralOriginalDmaCopyLeg Destination) p = Pair(); byte[] shared = new byte[64];
        NeutralOriginalDmaCopyRequest old = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,shared,shared);
        Assert.True(p.Runtime.CopyDma(old).NoEffectGuaranteed); // Actual retained overlap refusal.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = p.Runtime.GetType().GetField("_dependencies",flags)!.GetValue(p.Runtime)!;
        IDictionary grants = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
        object owner = grants[(destination ? p.Destination : p.Source).Grant.Handle]!;
        IList history = (IList)owner.GetType().GetProperty("OriginalCopies")!.GetValue(owner)!;
        void Corrupt()
        {
            history.Add(history[0]);
            owner.GetType().GetProperty("OriginalCopiesIssued")!.SetValue(owner,(ulong)history.Count);
        }
        using CallbackMemory memory = new CallbackMemory(Enumerable.Repeat((byte)0x51,64).ToArray());
        int reads = 0; Memory<byte> input = memory.Memory; byte[] output = new byte[64];
        memory.OnRead = () => { reads++; if(late) Corrupt(); };
        if(!late) Corrupt();
        NeutralOriginalDmaCopyRequest q = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,input,output);
        NeutralOriginalDmaCopyObservation raw = p.Runtime.CopyDma(q);
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
        public override Span<byte> GetSpan() { Action? action = OnRead; OnRead = null; action?.Invoke(); return bytes; }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalCopyMemoryCallbacksRunOutsideLockAndLateCycleDriftRefusesEffect(bool drift)
    {
        (NeutralDomainRuntimeFacade Runtime, NeutralOriginalDmaCopyLeg Source, NeutralOriginalDmaCopyLeg Destination) p = Pair(); using CallbackMemory memory = new CallbackMemory(Enumerable.Repeat((byte)0x51,64).ToArray()); byte[] output = new byte[64];
        NeutralOriginalDmaCopyRequest q = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),p.Runtime.MappingClosureIncarnation,p.Source,p.Destination,memory.Memory,output);
        memory.OnRead = () =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.False(Monitor.IsEntered(p.Runtime.GetType().GetField("_lifecycleGate",flags)!.GetValue(p.Runtime)!));
            Assert.False(p.Runtime.CopyDma(q).Completed);
            Assert.False(p.Runtime.PrepareDmaVisibility(p.Source.Grant).IsPrepared);
            Assert.NotEqual(NeutralDmaGrantCloseDecision.Closed,p.Runtime.CloseDmaGrant(p.Source.Grant).Decision);
            if (drift)
            {
                object registry = p.Runtime.GetType().GetField("_dependencies",flags)!.GetValue(p.Runtime)!;
                IDictionary owners = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
                object owner = owners[p.Source.Grant.Handle]!; object cycle = owner.GetType().GetProperty("Visibility")!.GetValue(owner)!;
                cycle.GetType().GetProperty("Acquired")!.SetValue(cycle,true);
            }
        };
        NeutralOriginalDmaCopyObservation raw = p.Runtime.CopyDma(q); Assert.Equal(!drift,raw.Completed); Assert.Equal(drift,raw.NoEffectGuaranteed);
        Assert.All(output,b => Assert.Equal(drift ? (byte)0 : (byte)0x51,b));
        Assert.Same(raw,p.Runtime.ResolveDmaCopy(q));
    }
}
