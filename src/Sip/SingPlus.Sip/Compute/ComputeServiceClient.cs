using SingPlus.Contracts;
namespace SingPlus.Sip.Compute;
/// <summary>Existing generated bounded borrow/move session client; no compute authority.</summary>
public static class ComputeServiceClient
{
    public static ProtocolDefinitionV1 CreateProtocol()=>IComputeServiceProtocol.CreateDefinition();
    public static ResponseProtocolDefinitionV1 ResponseProtocol=>IComputeServiceResponseProtocol.Definition;
    public static IComputeService Create(ISipClientRuntimeTransport transport)=>IComputeServiceRuntimeClient.Create(transport);
}
