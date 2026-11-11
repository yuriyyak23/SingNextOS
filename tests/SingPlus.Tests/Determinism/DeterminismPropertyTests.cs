using System.Text;
using SingPlus.Contracts;

namespace SingPlus.Tests.Determinism;

public sealed class DeterminismPropertyTests
{
    [Fact]
    [Trait("Category", "Determinism")]
    public void ProcessManifestDigestIsInvariantAcrossInputPermutations()
    {
        CapabilityRequirementV1[] capabilities = new[]
        {
            new CapabilityRequirementV1(ResourceKind.Device, "timer0", CapabilityRights.Read),
            new CapabilityRequirementV1(ResourceKind.ChannelEndpoint, "console", CapabilityRights.Write),
            new CapabilityRequirementV1(ResourceKind.KernelService, "clock", CapabilityRights.Read | CapabilityRights.Signal)
        };
        string[] contracts = new[] { "Z.Contract", "A.Contract", "M.Contract" };
        string? expectedDigest = null;
        byte[]? expectedCanonical = null;

        foreach (CapabilityRequirementV1[] capabilityOrder in Permutations(capabilities))
        foreach (string[] contractOrder in Permutations(contracts))
        {
                SingProcessManifestV1 manifest = new SingProcessManifestV1(
                new ProcessId(7),
                new DomainId(9),
                3,
                "Kernel.Root::Run",
                ExecutionRole.Kernel,
                MemoryProfile.KernelNoHeap,
                capabilityOrder,
                contractOrder,
                new ResourceLimitsV1(4096, 8, 4, 16));

            expectedDigest ??= manifest.ComputeDigest();
            expectedCanonical ??= manifest.SerializeCanonical();
            Assert.Equal(expectedDigest, manifest.ComputeDigest());
            Assert.Equal(expectedCanonical, manifest.SerializeCanonical());
        }
    }

    [Fact]
    [Trait("Category", "Determinism")]
    public void DriverManifestDigestIsInvariantAcrossCapabilityPermutations()
    {
        CapabilityRequirementV1[] capabilities = new[]
        {
            new CapabilityRequirementV1(ResourceKind.Irq, "irq:4", CapabilityRights.Signal),
            new CapabilityRequirementV1(ResourceKind.MmioRegion, "uart0", CapabilityRights.Map | CapabilityRights.Write),
            new CapabilityRequirementV1(ResourceKind.Dma, "dma:uart0", CapabilityRights.Configure | CapabilityRights.Transfer)
        };
        string? expectedDigest = null;
        byte[]? expectedCanonical = null;

        foreach (CapabilityRequirementV1[] order in Permutations(capabilities))
        {
            DriverManifestV1 manifest = new DriverManifestV1("console-driver", order);
            expectedDigest ??= manifest.ComputeDigest();
            expectedCanonical ??= manifest.SerializeCanonical();
            Assert.Equal(expectedDigest, manifest.ComputeDigest());
            Assert.Equal(expectedCanonical, manifest.SerializeCanonical());
        }
    }

    [Fact]
    [Trait("Category", "Determinism")]
    public void ProtocolTablesHaveCanonicalOrderingRegardlessOfDeclarationOrder()
    {
        ProtocolMessageDescriptorV1[] messages = new[]
        {
            new ProtocolMessageDescriptorV1(3, "Close"),
            new ProtocolMessageDescriptorV1(1, "Open"),
            new ProtocolMessageDescriptorV1(2, "Write")
        };
        ProtocolTransitionV1[] transitions = new[]
        {
            new ProtocolTransitionV1(3, "Active", "Done"),
            new ProtocolTransitionV1(2, "Active", "Active"),
            new ProtocolTransitionV1(1, "Idle", "Active")
        };

        ProtocolDefinitionV1 first = new ProtocolDefinitionV1("Contract", "digest", "Idle", new[] { "Done" }, messages, transitions);
        ProtocolDefinitionV1 second = new ProtocolDefinitionV1("Contract", "digest", "Idle", new[] { "Done" }, messages.Reverse(), transitions.Reverse());

        Assert.Equal(first.Messages.Select(static message => message.MessageId), second.Messages.Select(static message => message.MessageId));
        Assert.Equal(first.Transitions, second.Transitions);
        Assert.Equal(new uint[] { 1, 2, 3 }, first.Messages.Select(static message => message.MessageId));
    }

    [Fact]
    [Trait("Category", "Determinism")]
    public void CanonicalManifestsContainNoEnvironmentSpecificEntropy()
    {
        SingProcessManifestV1 manifest = TestFixtures.Manifest(1, 1, identity: "Entry::Run", contracts: new[] { "Contract.A" });
        string text = Encoding.UTF8.GetString(manifest.SerializeCanonical());

        Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.CurrentDirectory, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timestamp", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guid", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Determinism")]
    [Trait("Category", "Property")]
    public void ProtocolRejectsManyUnknownMessageIdsWithoutAlteringCanonicalState()
    {
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1(
            "Contract",
            "digest",
            "Idle",
            new[] { "Done" },
            new[] { new ProtocolMessageDescriptorV1(1, "Open") },
            new[] { new ProtocolTransitionV1(1, "Idle", "Done") });

        for (uint messageId = 2; messageId < 258; messageId++)
        {
            Assert.False(protocol.TryGetMessage(messageId, out _));
            Assert.False(protocol.TryTransition("Idle", messageId, out _));
            Assert.Equal("Idle", protocol.InitialState);
        }
    }

    private static IEnumerable<T[]> Permutations<T>(IReadOnlyList<T> values)
    {
        T[] buffer = values.ToArray();
        foreach (T[] result in Permute(buffer, 0)) yield return result;
    }

    private static IEnumerable<T[]> Permute<T>(T[] values, int index)
    {
        if (index == values.Length)
        {
            yield return values.ToArray();
            yield break;
        }

        for (int i = index; i < values.Length; i++)
        {
            (values[index], values[i]) = (values[i], values[index]);
            foreach (T[] result in Permute(values, index + 1)) yield return result;
            (values[index], values[i]) = (values[i], values[index]);
        }
    }
}
