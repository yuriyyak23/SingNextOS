using SingPlus.Contracts;

namespace SingPlus.Tests.Runtime;

internal static class BudgetSnapshotAssertions
{
    internal static void Equal(BudgetReservationSnapshot expected, BudgetReservationSnapshot actual)
    {
        Assert.Equal(expected with { Amounts = actual.Amounts, ChargedAmounts = actual.ChargedAmounts }, actual);
        Assert.Equal<BudgetAmount>(expected.Amounts, actual.Amounts);
        Assert.Equal<BudgetAmount>(expected.ChargedAmounts!, actual.ChargedAmounts!);
    }
}
