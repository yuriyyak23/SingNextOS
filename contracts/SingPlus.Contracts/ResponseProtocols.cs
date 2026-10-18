namespace SingPlus.Contracts;

public enum ResponsePayloadKind
{
    None = 0,
    Primitive = 1,
    Enum = 2,
    Bounded = 3,
    Ownership = 4
}

public enum ResponsePublicationStatus
{
    Published = 0,
    Cancelled = 1
}

public sealed class ResponsePayloadDescriptorV1
{
    private static readonly HashSet<string> PrimitiveTypeNames = new(StringComparer.Ordinal)
    {
        typeof(byte).FullName!,
        typeof(sbyte).FullName!,
        typeof(short).FullName!,
        typeof(ushort).FullName!,
        typeof(int).FullName!,
        typeof(uint).FullName!,
        typeof(long).FullName!,
        typeof(ulong).FullName!,
        typeof(float).FullName!,
        typeof(double).FullName!,
        typeof(bool).FullName!,
        typeof(char).FullName!,
        typeof(decimal).FullName!
    };

    public ResponsePayloadDescriptorV1(
        ResponsePayloadKind kind,
        string? typeName = null,
        int maxBytes = 0,
        OwnershipPayloadKind ownershipPayloadKind = OwnershipPayloadKind.None)
    {
        Kind = kind;

        if (kind == ResponsePayloadKind.None)
        {
            if (!string.IsNullOrEmpty(typeName) || maxBytes != 0 || ownershipPayloadKind != OwnershipPayloadKind.None)
                throw new ArgumentException("A None response payload cannot carry type, bounds, or ownership metadata.");
            TypeName = string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(typeName))
            throw new ArgumentException("Response payload type name is required.", nameof(typeName));

        TypeName = typeName;
        switch (kind)
        {
            case ResponsePayloadKind.Primitive:
                if (!PrimitiveTypeNames.Contains(typeName))
                    throw new ArgumentException($"'{typeName}' is not a supported primitive response payload type.", nameof(typeName));
                if (maxBytes != 0 || ownershipPayloadKind != OwnershipPayloadKind.None)
                    throw new ArgumentException("Primitive response payloads cannot carry bounds or ownership metadata.");
                break;

            case ResponsePayloadKind.Enum:
                if (maxBytes != 0 || ownershipPayloadKind != OwnershipPayloadKind.None)
                    throw new ArgumentException("Enum response payloads cannot carry bounds or ownership metadata.");
                break;

            case ResponsePayloadKind.Bounded:
                if (maxBytes <= 0)
                    throw new ArgumentOutOfRangeException(nameof(maxBytes), "Bounded response payload limit must be positive.");
                if (ownershipPayloadKind != OwnershipPayloadKind.None)
                    throw new ArgumentException("Bounded response payloads cannot carry ownership metadata.", nameof(ownershipPayloadKind));
                MaxBytes = maxBytes;
                break;

            case ResponsePayloadKind.Ownership:
                if (maxBytes != 0)
                    throw new ArgumentException("Ownership response payloads cannot carry bounded payload metadata.", nameof(maxBytes));
                if (ownershipPayloadKind == OwnershipPayloadKind.None)
                    throw new ArgumentException("Ownership response payloads require a concrete ownership kind.", nameof(ownershipPayloadKind));
                var expectedType = ownershipPayloadKind == OwnershipPayloadKind.OwnedBuffer
                    ? "SingPlus.Sip.OwnedBuffer"
                    : "SingPlus.Sip.OwnedRegion";
                if (!string.Equals(typeName, expectedType, StringComparison.Ordinal))
                    throw new ArgumentException($"Ownership response type '{typeName}' does not match {ownershipPayloadKind}.", nameof(typeName));
                OwnershipPayloadKind = ownershipPayloadKind;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    public static ResponsePayloadDescriptorV1 None { get; } = new(ResponsePayloadKind.None);

    public ResponsePayloadKind Kind { get; }
    public string TypeName { get; }
    public int MaxBytes { get; }
    public OwnershipPayloadKind OwnershipPayloadKind { get; }
}

public sealed class ResponseMessageDescriptorV1
{
    public ResponseMessageDescriptorV1(
        uint messageId,
        string name,
        ResponsePayloadDescriptorV1? payload = null)
    {
        if (messageId == 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Response message name is required.", nameof(name));
        MessageId = messageId;
        Name = name;
        Payload = payload ?? ResponsePayloadDescriptorV1.None;
    }

    public uint MessageId { get; }
    public string Name { get; }
    public ResponsePayloadDescriptorV1 Payload { get; }
}

public sealed class ResponseProtocolDefinitionV1
{
    private readonly ResponseMessageDescriptorV1[] _messages;
    private readonly Dictionary<uint, ResponseMessageDescriptorV1> _messagesById;

    public ResponseProtocolDefinitionV1(
        string contractName,
        string responseMetadataDigest,
        IEnumerable<ResponseMessageDescriptorV1> messages)
    {
        if (string.IsNullOrWhiteSpace(contractName))
            throw new ArgumentException("Contract name is required.", nameof(contractName));
        if (string.IsNullOrWhiteSpace(responseMetadataDigest))
            throw new ArgumentException("Response metadata digest is required.", nameof(responseMetadataDigest));

        ContractName = contractName;
        ResponseMetadataDigest = responseMetadataDigest;
        _messages = messages.OrderBy(static message => message.MessageId).ToArray();
        _messagesById = [];
        foreach (var message in _messages)
        {
            if (!_messagesById.TryAdd(message.MessageId, message))
                throw new ArgumentException($"Duplicate response message id {message.MessageId}.", nameof(messages));
        }
    }

    public string ContractName { get; }
    public string ResponseMetadataDigest { get; }
    public IReadOnlyList<ResponseMessageDescriptorV1> Messages => _messages;

    public bool TryGetMessage(uint messageId, out ResponseMessageDescriptorV1 descriptor) =>
        _messagesById.TryGetValue(messageId, out descriptor!);
}

public sealed record ResponseEnvelope(
    ulong RequestSequence,
    uint MessageId,
    ResponsePublicationStatus Status,
    object? Payload);
