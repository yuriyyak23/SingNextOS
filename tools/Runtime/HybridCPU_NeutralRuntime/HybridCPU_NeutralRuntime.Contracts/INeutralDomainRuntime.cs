namespace YAKSys_Hybrid_CPU.Core;

/// <summary>Typed provider-facing NeutralRuntime surface.</summary>
public interface INeutralDomainRuntime :
    INeutralRuntimeFeatureProvider,
    INeutralDomainOperations,
    INeutralMappingOperations,
    INeutralDeviceOperations,
    INeutralMmioOperations,
    INeutralInterruptOperations,
    INeutralDmaOperations
{
    NeutralRuntimeImplementationProfile ImplementationProfile { get; }
}

public enum NeutralRuntimeImplementationProfile
{
    ModelOnly,
    ExecutableAdapter,
}
