using SingPlus.Contracts;
using SingPlus.Sip.FileSystem;
using SingPlus.Sip.Native;

namespace SingPlus.System;

public sealed class NativeFile
{
    private readonly IFileService _client;
    internal NativeFile(IFileService client, FileObjectAuthority authority) { _client = client; Authority = authority; }
    public FileObjectAuthority Authority { get; }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); await _client.WriteAsync(new(Authority.File, Authority.Capability, new BoundedBytes(data.Span))).ConfigureAwait(false); }
    public async ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return (await _client.ReadAsync(Command()).ConfigureAwait(false)).Data.ToArray(); }
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); await _client.CloseAsync(Command()).ConfigureAwait(false); }
    private FileCommand Command() => new(Authority.File, Authority.Capability);
}

public sealed class FileSystem
{
    private readonly IFileService _client; private readonly CapabilityId _namespaceCapability;
    public FileSystem(IFileService generatedClient, CapabilityId namespaceCapability) { _client = generatedClient ?? throw new ArgumentNullException(nameof(generatedClient)); _namespaceCapability = namespaceCapability; }
    public async ValueTask<NativeFile> OpenAsync(string path, bool create = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await _client.OpenAsync(new(_namespaceCapability, path, create)).ConfigureAwait(false);
        return new NativeFile(_client, response.Authority);
    }
}
