namespace SingPlus.Contracts;

public sealed record ProtocolTransitionV1(uint MessageId, string FromState, string ToState);

public enum OwnershipPayloadKind
{
    None = 0,
    OwnedBuffer = 1,
    OwnedRegion = 2
}

public sealed class ProtocolMessageDescriptorV1
{
    private readonly CapabilityRequirementV1[] _requiredCapabilities;
    private readonly string[] _consumes;
    private readonly string[] _borrows;

    public ProtocolMessageDescriptorV1(
        uint messageId,
        string name,
        IEnumerable<CapabilityRequirementV1>? requiredCapabilities = null,
        IEnumerable<string>? consumes = null,
        IEnumerable<string>? borrows = null,
        bool returnsOwnership = false,
        OwnershipPayloadKind ownershipPayloadKind = OwnershipPayloadKind.None,
        OwnershipPayloadKind returnOwnershipPayloadKind = OwnershipPayloadKind.None)
    {
        if (messageId == 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Message name is required.", nameof(name));
        MessageId = messageId;
        Name = name;
        _requiredCapabilities = (requiredCapabilities ?? []).OrderBy(static x => x.ResourceKind).ThenBy(static x => x.ResourceId, StringComparer.Ordinal).ThenBy(static x => (int)x.Rights).ToArray();
        _consumes = NormalizeOwnershipNames(consumes, nameof(consumes));
        _borrows = NormalizeOwnershipNames(borrows, nameof(borrows));

        if (_consumes.Length > 1 || _borrows.Length > 1)
            throw new ArgumentException("The current channel transport supports at most one ownership-bearing payload per message.");
        if (_consumes.Length != 0 && _borrows.Length != 0)
            throw new ArgumentException("A single ownership-bearing payload cannot be both consumed and borrowed.");

        var hasOwnershipInput = _consumes.Length + _borrows.Length == 1;
        if (hasOwnershipInput != (ownershipPayloadKind != OwnershipPayloadKind.None))
            throw new ArgumentException("Ownership payload metadata must declare exactly one concrete payload kind when Consumes or Borrows is present.", nameof(ownershipPayloadKind));
        if (returnsOwnership != (returnOwnershipPayloadKind != OwnershipPayloadKind.None))
            throw new ArgumentException("ReturnsOwnership metadata must declare a concrete returned ownership payload kind.", nameof(returnOwnershipPayloadKind));

        ReturnsOwnership = returnsOwnership;
        OwnershipPayloadKind = ownershipPayloadKind;
        ReturnOwnershipPayloadKind = returnOwnershipPayloadKind;
    }

    public uint MessageId { get; }
    public string Name { get; }
    public IReadOnlyList<CapabilityRequirementV1> RequiredCapabilities => _requiredCapabilities;
    public IReadOnlyList<string> Consumes => _consumes;
    public IReadOnlyList<string> Borrows => _borrows;
    public bool ReturnsOwnership { get; }
    public OwnershipPayloadKind OwnershipPayloadKind { get; }
    public OwnershipPayloadKind ReturnOwnershipPayloadKind { get; }

    private static string[] NormalizeOwnershipNames(IEnumerable<string>? names, string parameterName)
    {
        var normalized = (names ?? []).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        if (normalized.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Ownership parameter names cannot be empty.", parameterName);
        if (normalized.Length != normalized.Distinct(StringComparer.Ordinal).Count()) throw new ArgumentException("Duplicate ownership parameter name.", parameterName);
        return normalized;
    }
}

public sealed class ProtocolDefinitionV1
{
    private readonly ProtocolMessageDescriptorV1[] _messages;
    private readonly ProtocolTransitionV1[] _transitions;
    private readonly string[] _terminalStates;
    private readonly Dictionary<uint, ProtocolMessageDescriptorV1> _messagesById;
    private readonly Dictionary<(string State, uint MessageId), ProtocolTransitionV1> _transitionsByKey;

    public ProtocolDefinitionV1(
        string contractName,
        string contractDigest,
        string initialState,
        IEnumerable<string>? terminalStates,
        IEnumerable<ProtocolMessageDescriptorV1> messages,
        IEnumerable<ProtocolTransitionV1> transitions)
    {
        if (string.IsNullOrWhiteSpace(contractName)) throw new ArgumentException("Contract name is required.", nameof(contractName));
        if (string.IsNullOrWhiteSpace(contractDigest)) throw new ArgumentException("Contract digest is required.", nameof(contractDigest));
        if (string.IsNullOrWhiteSpace(initialState)) throw new ArgumentException("Initial protocol state is required.", nameof(initialState));
        ContractName = contractName;
        ContractDigest = contractDigest;
        InitialState = initialState;
        _terminalStates = (terminalStates ?? []).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        if (_terminalStates.Length != _terminalStates.Distinct(StringComparer.Ordinal).Count()) throw new ArgumentException("Duplicate terminal state.", nameof(terminalStates));
        _messages = messages.OrderBy(static x => x.MessageId).ToArray();
        _messagesById = [];
        foreach (var message in _messages)
        {
            if (!_messagesById.TryAdd(message.MessageId, message)) throw new ArgumentException($"Duplicate message id {message.MessageId}.", nameof(messages));
        }
        _transitions = transitions.OrderBy(static x => x.FromState, StringComparer.Ordinal).ThenBy(static x => x.MessageId).ThenBy(static x => x.ToState, StringComparer.Ordinal).ToArray();
        _transitionsByKey = [];
        foreach (var transition in _transitions)
        {
            if (!_messagesById.ContainsKey(transition.MessageId)) throw new ArgumentException($"Transition references unknown message {transition.MessageId}.", nameof(transitions));
            if (string.IsNullOrWhiteSpace(transition.FromState) || string.IsNullOrWhiteSpace(transition.ToState)) throw new ArgumentException("Transition states are required.", nameof(transitions));
            if (!_transitionsByKey.TryAdd((transition.FromState, transition.MessageId), transition)) throw new ArgumentException($"Duplicate transition for {transition.FromState}/{transition.MessageId}.", nameof(transitions));
        }
    }

    public string ContractName { get; }
    public string ContractDigest { get; }
    public string InitialState { get; }
    public IReadOnlyList<string> TerminalStates => _terminalStates;
    public IReadOnlyList<ProtocolMessageDescriptorV1> Messages => _messages;
    public IReadOnlyList<ProtocolTransitionV1> Transitions => _transitions;

    public bool TryGetMessage(uint messageId, out ProtocolMessageDescriptorV1 descriptor) => _messagesById.TryGetValue(messageId, out descriptor!);
    public bool TryTransition(string state, uint messageId, out ProtocolTransitionV1 transition) => _transitionsByKey.TryGetValue((state, messageId), out transition!);
    public bool IsTerminal(string state) => Array.BinarySearch(_terminalStates, state, StringComparer.Ordinal) >= 0;
}

public interface IBoundedPayload
{
    int PayloadSize { get; }
    int MaxPayloadSize { get; }
}

public sealed record ChannelEndpoint(
    ChannelEndpointHandle Handle,
    DomainId OwnerDomain,
    ulong OwnerGeneration,
    string ProtocolState,
    ulong Sequence,
    int Capacity);

public sealed record ChannelEnvelope(ulong Sequence, uint MessageId, object? Payload);
