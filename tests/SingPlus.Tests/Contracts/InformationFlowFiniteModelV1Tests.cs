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
        foreach (DataLabelV1 left in Labels)
        foreach (DataLabelV1 right in Labels)
        {
            Assert.Equal(DataLabelLatticeV1.Join(left, right), DataLabelLatticeV1.Join(right, left));
            Assert.Equal(left, DataLabelLatticeV1.Join(left, left));
            foreach (DataLabelV1 third in Labels)
                Assert.Equal(DataLabelLatticeV1.Join(DataLabelLatticeV1.Join(left, right), third),
                    DataLabelLatticeV1.Join(left, DataLabelLatticeV1.Join(right, third)));
        }
    }

    [Fact]
    public void JoinIsTheLeastUpperBoundForEveryPair()
    {
        foreach (DataLabelV1 left in Labels)
        foreach (DataLabelV1 right in Labels)
        {
                DataLabelV1 joined = DataLabelLatticeV1.Join(left, right);
            Assert.True(Flows(left, joined));
            Assert.True(Flows(right, joined));
            foreach (DataLabelV1 candidate in Labels.Where(candidate => Flows(left, candidate) && Flows(right, candidate)))
                Assert.True(Flows(joined, candidate));
        }
    }

    [Fact]
    public void FlowRelationIsReflexiveAntisymmetricAndTransitive()
    {
        foreach (DataLabelV1 left in Labels)
        {
            Assert.True(Flows(left, left));
            foreach (DataLabelV1 right in Labels)
            {
                if (Flows(left, right) && Flows(right, left)) Assert.Equal(left, right);
                foreach (DataLabelV1 third in Labels)
                    if (Flows(left, right) && Flows(right, third)) Assert.True(Flows(left, third));
            }
        }
    }

    [Fact]
    public void DisallowedInfluenceCannotBeHiddenByJoiningWithAnAllowedValue()
    {
        foreach (DataLabelV1 sink in Labels)
        foreach (DataLabelV1 allowed in Labels.Where(value => Flows(value, sink)))
        foreach (DataLabelV1 disallowed in Labels.Where(value => !Flows(value, sink)))
            Assert.False(Flows(DataLabelLatticeV1.Join(allowed, disallowed), sink));
    }

    private static bool Flows(DataLabelV1 source, DataLabelV1 sink) =>
        DataLabelLatticeV1.CanFlowTo(source, new(1, sink));
}
