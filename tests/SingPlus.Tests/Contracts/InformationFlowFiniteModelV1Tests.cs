using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class InformationFlowFiniteModelV1Tests
{
    private static readonly DataLabelV1[] Labels =
        (from confidentiality in Enum.GetValues<ConfidentialityClassV1>()
         from integrity in Enum.GetValues<IntegrityClassV1>()
         select new DataLabelV1(1, confidentiality, integrity)).ToArray();

    [Fact]
    public void JoinIsACommutativeAssociativeIdempotentOperationAcrossTheFiniteLattice()
    {
        foreach (var left in Labels)
        foreach (var right in Labels)
        {
            Assert.Equal(DataLabelLatticeV1.Join(left, right), DataLabelLatticeV1.Join(right, left));
            Assert.Equal(left, DataLabelLatticeV1.Join(left, left));
            foreach (var third in Labels)
                Assert.Equal(DataLabelLatticeV1.Join(DataLabelLatticeV1.Join(left, right), third),
                    DataLabelLatticeV1.Join(left, DataLabelLatticeV1.Join(right, third)));
        }
    }

    [Fact]
    public void JoinIsTheLeastUpperBoundForEveryPair()
    {
        foreach (var left in Labels)
        foreach (var right in Labels)
        {
            var joined = DataLabelLatticeV1.Join(left, right);
            Assert.True(Flows(left, joined));
            Assert.True(Flows(right, joined));
            foreach (var candidate in Labels.Where(candidate => Flows(left, candidate) && Flows(right, candidate)))
                Assert.True(Flows(joined, candidate));
        }
    }

    [Fact]
    public void FlowRelationIsReflexiveAntisymmetricAndTransitive()
    {
        foreach (var left in Labels)
        {
            Assert.True(Flows(left, left));
            foreach (var right in Labels)
            {
                if (Flows(left, right) && Flows(right, left)) Assert.Equal(left, right);
                foreach (var third in Labels)
                    if (Flows(left, right) && Flows(right, third)) Assert.True(Flows(left, third));
            }
        }
    }

    [Fact]
    public void DisallowedInfluenceCannotBeHiddenByJoiningWithAnAllowedValue()
    {
        foreach (var sink in Labels)
        foreach (var allowed in Labels.Where(value => Flows(value, sink)))
        foreach (var disallowed in Labels.Where(value => !Flows(value, sink)))
            Assert.False(Flows(DataLabelLatticeV1.Join(allowed, disallowed), sink));
    }

    private static bool Flows(DataLabelV1 source, DataLabelV1 sink) =>
        DataLabelLatticeV1.CanFlowTo(source, new(1, sink));
}
