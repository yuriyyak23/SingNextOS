namespace SingPlus.Runtime;

public enum KernelError
{
    None = 0,
    InvalidManifest,
    DuplicateProcessId,
    DuplicateIdentity,
    StaleGeneration,
    StaleHandle,
    ProcessNotFound,
    DomainNotFound,
    InvalidTransition,
    CapabilityNotFound,
    WrongCapabilitySubject,
    InsufficientRights,
    CapabilityRevoked,
    MissingCapability,
    DelegationDenied,
    RegionNotFound,
    InvalidRegionState,
    WrongRegionOwner,
    ChannelNotFound,
    EndpointNotFound,
    WrongEndpointOwner,
    InvalidMessage,
    InvalidProtocolTransition,
    CapacityExhausted,
    UnsupportedPayload,
    WrongCapabilityResource,
    PlatformUnavailable,
    PlatformUnsupported,
    PlatformDenied,
    PlatformBindingNotFound,
    PlatformBindingRevoked,
    PlatformBindingActive,
    WrongPlatformDomain,
    PlatformFaulted
}

public readonly record struct KernelResult(bool IsSuccess, KernelError Error, string? Message)
{
    public static KernelResult Ok() => new(true, KernelError.None, null);
    public static KernelResult Fail(KernelError error, string message) => new(false, error, message);
}

public readonly record struct KernelResult<T>(bool IsSuccess, T? Value, KernelError Error, string? Message)
{
    public static KernelResult<T> Ok(T value) => new(true, value, KernelError.None, null);
    public static KernelResult<T> Fail(KernelError error, string message) => new(false, default, error, message);
}
