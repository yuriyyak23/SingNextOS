namespace SingPlus.Platform;

/// <summary>Additional private-buffer payload bytes allocated by the producing path.
/// This is capacity input, never permission/closure. V1 transfer bounds alone do not describe allocation.</summary>
public interface IPlatformVirtualIoPrivateBackingProvider
{
    PlatformAuthorityResult<ulong> DescribeVirtualIoPrivateBackingBytes(PlatformVirtualIoProfile profile);
}
