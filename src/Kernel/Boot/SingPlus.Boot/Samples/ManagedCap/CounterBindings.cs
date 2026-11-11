using SingPlus.Contracts;

namespace SingPlus.Boot.Samples;

// Public host-debug facade over actual generated bindings, not a handwritten ABI.
public static class CounterBindings
{
    public static uint IncrementMessageId => ICounterServiceProtocol.Message_Increment;
    public static ProtocolDefinitionV1 Protocol => ICounterServiceProtocol.CreateDefinition();
    public static ResponseProtocolDefinitionV1 Responses => ICounterServiceResponseProtocol.Definition;
    public static ICounterService CreateClient(ISipClientRuntimeTransport runtime) => ICounterServiceRuntimeClient.Create(runtime);
    public static CounterValue Dispatch(CounterValue request) => new ICounterServiceDispatcher(new Counter()).Dispatch_Increment(request);

    private sealed class Counter : ICounterService
    {
        public CounterValue Increment(CounterValue request) => new(request.Value + 1);
    }
}
