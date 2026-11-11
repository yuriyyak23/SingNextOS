using System.Globalization;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Boot.Shell;

public static partial class EmulatorCommands
{
    private static bool ReserveLocalBudget(EmulatorSession session, string[] args)
    {
        if (args.Length != 3 || !TryReadPid(args, 0, out ulong pid) ||
            !Enum.TryParse<ServiceBudgetDimension>(args[1], true, out ServiceBudgetDimension dimension) || !Enum.IsDefined(dimension) ||
            !ulong.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong amount) || amount == 0)
        {
            EmulatorConsole.Error("[emulator] usage: budget-reserve <pid> <dimension> <positive amount>\r\n");
            return false;
        }
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        KernelResult<ProcessHandle> owner = session.ResolvePid(pid);
        if (!owner.IsSuccess) return Fail(owner.Error, owner.Message);
        KernelResult<BudgetReservationSnapshot> reserved = runtime.ReserveBudget(owner.Value, [new BudgetAmount(dimension, amount)], BudgetReservationLifetime.LocalResource);
        if (!reserved.IsSuccess) return Fail(reserved.Error, reserved.Message);
        BudgetReservationSnapshot snapshot = reserved.Value!;
        session.BudgetReservations[snapshot.Reservation.ReservationId.Value] = snapshot;
        EmulatorConsole.Ok($"[kernel] reservation {snapshot.Reservation.ReservationId.Value} state={snapshot.State}: {dimension}={amount}; reservation does not authorize an effect\r\n");
        return true;
    }

    private static bool ReleaseLocalBudget(EmulatorSession session, string[] args)
    {
        if (args.Length != 2 || !TryReadPid(args, 0, out ulong pid) ||
            !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
        {
            EmulatorConsole.Error("[emulator] usage: budget-release <pid> <reservationId>\r\n");
            return false;
        }
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        if (!session.BudgetReservations.TryGetValue(id, out BudgetReservationSnapshot? tracked))
            return Fail(KernelError.InvalidTransition, "Reservation is not an explicit emulator reservation.");
        KernelResult<ProcessHandle> owner = session.ResolvePid(pid);
        if (!owner.IsSuccess) return Fail(owner.Error, owner.Message);
        KernelResult<BudgetReservationSnapshot> released = runtime.ReleaseBudget(owner.Value, tracked.Reservation);
        if (!released.IsSuccess) return Fail(released.Error, released.Message);
        if (released.Value!.State != BudgetReservationState.Released)
            return Fail(KernelError.InvalidTransition, $"Reservation remains {released.Value.State}.");
        session.BudgetReservations[id] = released.Value;
        EmulatorConsole.Ok($"[kernel] reservation {id} released\r\n");
        return true;
    }

    private static bool ShowBudgetReservations(EmulatorSession session, string[] args)
    {
        if (args.Length != 0)
        {
            EmulatorConsole.Error("[emulator] usage: budget-reservations\r\n");
            return false;
        }
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        List<IReadOnlyList<string>> rows = new List<IReadOnlyList<string>>();
        foreach (BudgetReservationSnapshot? tracked in session.BudgetReservations.Values.OrderBy(static item => item.Reservation.ReservationId.Value))
        {
            KernelResult<BudgetReservationSnapshot> queried = runtime.QueryBudget(tracked.Reservation);
            if (!queried.IsSuccess) return Fail(queried.Error, queried.Message);
            BudgetReservationSnapshot value = queried.Value!;
            rows.Add(new[] { value.Reservation.ReservationId.Value.ToString(), value.Owner.ProcessId.Value.ToString(),
                value.Owner.Generation.ToString(), value.State.ToString(), value.Lifetime.ToString(),
                string.Join(", ", value.Amounts.Select(static amount => $"{amount.Dimension}={amount.Amount}")) });
        }
        EmulatorConsole.Table(["reservation", "pid", "generation", "state", "lifetime", "amounts"], rows);
        return true;
    }
}
