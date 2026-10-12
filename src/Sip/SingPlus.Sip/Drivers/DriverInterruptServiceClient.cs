using SingPlus.Contracts;
namespace SingPlus.Sip.Drivers;
/// <summary>Constructs the existing generated bounded session client. No IRQ authority.</summary>
public static class DriverInterruptServiceClient
{
    public static ProtocolDefinitionV1 CreateProtocol()=>IDriverInterruptServiceProtocol.CreateDefinition();
    public static ResponseProtocolDefinitionV1 ResponseProtocol=>IDriverInterruptServiceResponseProtocol.Definition;
    public static IDriverInterruptService Create(ISipClientRuntimeTransport transport)=>IDriverInterruptServiceRuntimeClient.Create(transport);
}
