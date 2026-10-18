using SingPlus.Contracts;
using SingPlus.Sip.Native;
using SingPlus.Sip.Networking;

namespace SingPlus.System;

public sealed class NativeSocket
{
    private readonly INetworkService _client;
    internal NativeSocket(INetworkService client, SocketObjectAuthority authority) { _client = client; Authority = authority; }
    public SocketObjectAuthority Authority { get; }
    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); await _client.SendAsync(new(Authority.Socket, Authority.Capability, new BoundedBytes(packet.Span))).ConfigureAwait(false); }
    public async ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return (await _client.ReceiveAsync(Command()).ConfigureAwait(false)).Packet.ToArray(); }
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); await _client.CloseAsync(Command()).ConfigureAwait(false); }
    private SocketCommand Command() => new(Authority.Socket, Authority.Capability);
}

public sealed class Network
{
    private readonly INetworkService _client; private readonly CapabilityId _endpointCapability;
    public Network(INetworkService generatedClient, CapabilityId endpointCapability) { _client = generatedClient ?? throw new ArgumentNullException(nameof(generatedClient)); _endpointCapability = endpointCapability; }
    public async ValueTask<NativeSocket> OpenAsync(string endpointLabel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await _client.OpenAsync(new(_endpointCapability, endpointLabel)).ConfigureAwait(false);
        return new NativeSocket(_client, response.Authority);
    }
}
