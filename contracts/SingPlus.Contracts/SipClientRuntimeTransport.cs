namespace SingPlus.Contracts;

public interface ISipClientRuntimeTransport
{
    ResponseEnvelope Invoke(uint messageId, object? requestPayload = null);

    ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? requestPayload = null);

    ValueTask<ResponseEnvelope> InvokeAsync(
        uint messageId,
        object? requestPayload,
        CancellationToken cancellationToken);

    ResponseEnvelope InvokeOwnershipPair(
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload);

    ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload);

    ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload,
        CancellationToken cancellationToken);
}
