namespace SingPlus.Contracts;

public readonly record struct KernelEventEndpointId(ulong Value);
public readonly record struct KernelEventEndpointGeneration(ulong Value);
public readonly record struct KernelEventSequence(ulong Value);
public enum KernelEventClass { ExternalSignal = 0, Completion = 1 }
public readonly record struct KernelEventEndpoint(KernelEventEndpointId EndpointId, KernelEventEndpointGeneration Generation, ProcessHandle Owner);
public readonly record struct KernelEvent(KernelEventEndpoint Endpoint, KernelEventSequence Sequence, KernelEventClass EventClass, string SourceResourceId);
