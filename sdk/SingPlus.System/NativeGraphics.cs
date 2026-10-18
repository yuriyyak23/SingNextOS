using SingPlus.Contracts;
using SingPlus.Sip;
using SingPlus.Sip.Gui;

namespace SingPlus.System;

public sealed class SurfaceBuffer
{
    internal SurfaceBuffer(OwnedBuffer<byte> pixels, SurfaceMetadata metadata, SurfaceAuthority authority)
    {
        Pixels = pixels;
        Metadata = metadata;
        Authority = authority;
    }

    public OwnedBuffer<byte> Pixels { get; internal set; }
    public SurfaceMetadata Metadata { get; }
    public SurfaceAuthority Authority { get; }
    public Span<byte> WritablePixels => Pixels.Span;
}

public sealed class Compositor
{
    private readonly ICompositorService _client;
    private readonly CapabilityId _capability;

    public Compositor(ICompositorService client, CapabilityId capability)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _capability = capability;
    }

    public async ValueTask<SurfaceBuffer> RegisterAsync(OwnedBuffer<byte> pixels, SurfaceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var registered = await _client.RegisterSurfaceAsync(new RegisterSurfaceRequest(_capability, pixels.Handle, metadata)).ConfigureAwait(false);
        return new SurfaceBuffer(pixels, metadata, registered.Authority);
    }

    public async ValueTask PresentMoveAsync(SurfaceBuffer surface)
    {
        Validate(surface);
        await _client.PreparePresentAsync(new PreparePresentRequest(surface.Authority.Surface, PresentTransferMode.Move)).ConfigureAwait(false);
        surface.Pixels = await _client.PresentMoveAsync(surface.Pixels).ConfigureAwait(false);
    }

    public async ValueTask<ReleaseFence> PresentReadLeaseAsync(SurfaceBuffer surface)
    {
        Validate(surface);
        await _client.PreparePresentAsync(new PreparePresentRequest(surface.Authority.Surface, PresentTransferMode.ReadLease)).ConfigureAwait(false);
        return (await _client.PresentReadLeaseAsync(surface.Pixels).ConfigureAwait(false)).Fence;
    }

    private void Validate(SurfaceBuffer surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.Authority.PresentCapability != _capability)
            throw new InvalidOperationException("Surface authority belongs to another compositor capability.");
    }
}

/// <summary>Downstream name/semantic projection; all effects still use the native compositor session.</summary>
public sealed class CompatibilityWindowFacade
{
    private readonly Compositor _native;
    public CompatibilityWindowFacade(CompatibilityPersonality personality, Compositor native)
    {
        if (!Enum.IsDefined(personality)) throw new ArgumentOutOfRangeException(nameof(personality));
        Personality = personality;
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }
    public CompatibilityPersonality Personality { get; }
    public ValueTask PresentAsync(SurfaceBuffer surface) => _native.PresentMoveAsync(surface);
}
