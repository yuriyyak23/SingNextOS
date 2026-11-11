namespace YAKSys_Hybrid_CPU.Core;

/// <summary>One original external borrower, captured before backing effect.
/// A loan pins native lifetime; it is not capability permission or closure.</summary>
public sealed record NeutralOriginalDeviceLoanRequest(Guid InvocationId,
    NeutralOriginalDeviceBindRequest DeviceAdmission, NeutralDeviceLease Device,
    NeutralDeviceRights Rights, INeutralOriginalDeviceLoanLifetime Lifetime);
public sealed record NeutralOriginalDeviceLoan(NeutralOriginalDeviceLoanRequest Request);
public sealed record NeutralOriginalDeviceLoanClosure(NeutralOriginalDeviceLoan Loan, bool NoFurtherUse);

/// <summary>Pure same-original external backing lookup. Missing is unknown.
/// NoFurtherUse covers every backing/device user admitted through this loan.</summary>
public interface INeutralOriginalDeviceLoanLifetime
{
    NeutralOriginalDeviceLoanClosure? ResolveOriginalDeviceLoanClosure(NeutralOriginalDeviceLoan loan);
}
public interface INeutralOriginalDeviceLoanProvider : INeutralOriginalDeviceProvider
{
    NeutralOriginalDeviceLoan? AcquireOriginalDeviceLoan(NeutralOriginalDeviceLoanRequest request);
    bool ValidateOriginalDeviceLoan(NeutralOriginalDeviceLoan loan);
    bool RetireOriginalDeviceLoan(NeutralOriginalDeviceLoan loan);
}

/// <summary>Pure lookup of an already retained original borrower; absence is unknown, never no-effect.</summary>
public interface INeutralOriginalDeviceLoanResolutionProvider
{
    NeutralOriginalDeviceLoan? ResolveOriginalDeviceLoan(NeutralOriginalDeviceLoanRequest originalRequest);
}

public sealed record NeutralOriginalDeviceLoanNoEffect(NeutralOriginalDeviceLoanRequest Request, bool NoEffect, bool NoFurtherAdmission);
public interface INeutralOriginalDeviceLoanNoEffectProvider
{
    NeutralOriginalDeviceLoanNoEffect? ResolveOriginalDeviceLoanNoEffect(NeutralOriginalDeviceLoanRequest originalRequest);
}

public sealed record NeutralOriginalVirtualIoRequest(Guid InvocationId,
    NeutralOriginalDeviceBindRequest DeviceAdmission, NeutralVirtualIoRequest Request);
public interface INeutralOriginalVirtualIoProvider : INeutralVirtualIoProvider
{
    void AttachOriginalDeviceLoanProvider(INeutralOriginalDeviceLoanProvider provider);
    NeutralVirtualizationResult<ulong> DescribeOriginalVirtualIoPrivateBackingBytes(NeutralVirtualIoProfile profile);
    NeutralVirtualizationResult<NeutralVirtualIoLease> BindOriginalVirtualIo(NeutralOriginalVirtualIoRequest request);
}

public sealed record NeutralOriginalVirtualIoCloseRequest(Guid InvocationId,
    NeutralOriginalVirtualIoRequest Admission, NeutralVirtualIoLease Lease);
public interface INeutralOriginalVirtualIoCloseProvider
{
    NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> CloseOriginalVirtualIo(NeutralOriginalVirtualIoCloseRequest request);
    NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> ContinueOriginalVirtualIoClose(NeutralOriginalVirtualIoCloseRequest request);
}

public sealed record NeutralOriginalVirtualIoBindCleanupClosure(NeutralOriginalVirtualIoRequest Admission, bool NoFurtherUse, bool NoEffect = false);
public interface INeutralOriginalVirtualIoBindCleanupProvider
{
    bool SupportsOriginalVirtualIoBindCleanup { get; }
    NeutralVirtualizationResult<NeutralOriginalVirtualIoBindCleanupClosure> CleanupOriginalVirtualIoBind(NeutralOriginalVirtualIoRequest request);
    NeutralOriginalVirtualIoBindCleanupClosure? ResolveOriginalVirtualIoBindCleanup(NeutralOriginalVirtualIoRequest request);
}
