using SingPlus.Contracts;
namespace SingPlus.Sip.Virtualization;
/// <summary>Public entry point for the existing generated bounded session contract.
/// Discovery/protocol/client construction confers no device or virtual-domain authority.</summary>
public static class VirtualizationServiceClient
{
    public static ProtocolDefinitionV1 CreateProtocol()=>IVirtualizationServiceProtocol.CreateDefinition();
    public static ResponseProtocolDefinitionV1 ResponseProtocol=>IVirtualizationServiceResponseProtocol.Definition;
    public static IVirtualizationService Create(ISipClientRuntimeTransport transport)=>IVirtualizationServiceRuntimeClient.Create(transport);
}
