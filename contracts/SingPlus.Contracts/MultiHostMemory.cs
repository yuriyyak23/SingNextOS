namespace SingPlus.Contracts;

public readonly record struct ExternalHostIdentity(string Value);
public readonly record struct MultiHostBindingId(ulong Value);
public readonly record struct MultiHostBindingGeneration(ulong Value);
public readonly record struct MultiHostBindingHandle(MultiHostBindingId BindingId, MultiHostBindingGeneration Generation);

public enum MultiHostAccessMode { ReadOnly = 0, Writable = 1 }
public enum MultiHostBindingState { Active = 0, Fenced, Reclaiming, Released }

public sealed record MultiHostMemoryBinding(
    MultiHostBindingHandle Binding,
    ExternalHostIdentity Host,
    RegionHandle Region,
    MultiHostAccessMode Access,
    MultiHostBindingState State);
