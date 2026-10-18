using SingPlus.Kernel;
using SingPlus.Runtime;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunHostedService(EmulatorSession session)
    {
        const string name = "hosted-service-demo";
        var started = HostedCounterApp.Start(session, name);
        if (!started.IsSuccess) return Failure("start hosted service", started.Error, started.Message);
        var app = started.Value!;
        foreach (var input in new[] { 41, -2, 100 })
        {
            var answer = app.Invoke(session, input);
            if (!answer.IsSuccess) return Failure("invoke hosted service", answer.Error, answer.Message);
            EmulatorConsole.StageDetail($"generated SIP session {app.Session.SessionId.Value}: {input} -> {answer.Value}");
        }
        var overflow = app.Invoke(session, int.MaxValue);
        if (overflow.IsSuccess || overflow.Error != KernelError.InvalidMessage || app.Calls != 3)
            return KernelBootStageResult.Failure("Overflow refusal changed successful call state.");
        var revoked = session.Runtime!.RevokeCapability(app.AdmissionCapability);
        if (!revoked.IsSuccess) return Failure("revoke hosted capability", revoked.Error, revoked.Message);
        var refused = app.Invoke(session, 99);
        if (refused.IsSuccess || refused.Error != KernelError.CapabilityRevoked || app.Calls != 3 || app.LastResult != 101)
            return KernelBootStageResult.Failure("Revoked hosted authority changed successful call state.");
        var stopped = app.Stop(session);
        if (!stopped.IsSuccess) return Failure("stop hosted service", stopped.Error, stopped.Message);
        session.HostedCounters.Remove(name);
        EmulatorConsole.StageDetail("3 real session calls; overflow and revoked capability refused; session closed, both processes reclaimed; cooperative host CLR only");
        return KernelBootStageResult.Success();
    }
}