namespace SingPlus.Contracts;

public interface ISipClientRuntimeTransport
{
    ResponseEnvelope Invoke(uint messageId, object? requestPayload = null);

    ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? requestPayload = null);
}
