using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class FailureDurabilitySemanticsV1Tests
{
    [Fact]
    public void FailureSemanticsRoundTripAndRefineWithoutBecomingClosureAuthority()
    {
        var weak = Failure(FailureContainmentSemanticsV1.QuarantineAmbiguousEffect,
            FailureRecoverySemanticsV1.RebindRequired);
        var strong = Failure(FailureContainmentSemanticsV1.ExactSubrangeQuarantine,
            FailureRecoverySemanticsV1.FreshAdmissionRequired);

        Assert.Equal(strong, FailureSemanticsV1.ParseCanonical(strong.SerializeCanonical()));
        Assert.Equal(FailureSemanticsV1.ExtensionClassId,
            strong.ToClause(SemanticExtensionRequirement.Mandatory).ClassId);
        Assert.True(FailureSemanticPartialOrderV1.Refines(strong, weak));
        Assert.False(FailureSemanticPartialOrderV1.Refines(weak, strong));
        Assert.False(strong.AuthorizesRegionMutation);
        Assert.False(strong.ProvesEffectClosure);
        Assert.False(strong.AuthorizesReclaim);
    }

    [Fact]
    public void FailureContainmentCannotOmitExplicitClosureOrAcceptMalformedPayload()
    {
        Assert.Throws<ArgumentException>(() => (Failure(
            FailureContainmentSemanticsV1.QuarantineAmbiguousEffect,
            FailureRecoverySemanticsV1.RebindRequired) with
            { ExplicitEffectClosureRequired = false }).Validate());
        Assert.Throws<FormatException>(() => FailureSemanticsV1.ParseCanonical([0, 1, 1, 1, 2]));
    }

    [Fact]
    public void DurabilitySemanticsKeepPhysicalDomainsIncomparableAndExplicit()
    {
        var managed = Durable(DurabilityDomainRequirementV1.ManagedModel,
            DurabilityAssuranceSemanticsV1.ModelOnly);
        var any = managed with { Domain = DurabilityDomainRequirementV1.AnyNamedDomain };
        var eadr = Durable(DurabilityDomainRequirementV1.Eadr,
            DurabilityAssuranceSemanticsV1.HardwareQualified);

        Assert.True(DurabilitySemanticPartialOrderV1.Refines(managed, any));
        Assert.True(DurabilitySemanticPartialOrderV1.Refines(eadr, any));
        Assert.False(DurabilitySemanticPartialOrderV1.Refines(managed, eadr));
        Assert.False(DurabilitySemanticPartialOrderV1.Refines(eadr, managed));
        Assert.Throws<ArgumentException>(() => (eadr with
            { Assurance = DurabilityAssuranceSemanticsV1.ModelOnly }).Validate());
    }

    [Fact]
    public void StrongRecoveryIntegrityRefinesWeakOnlyWithinTheSameDomain()
    {
        var digest = Durable(DurabilityDomainRequirementV1.ManagedModel,
            DurabilityAssuranceSemanticsV1.ModelOnly) with
            { RecoveryIntegrity = RecoveryIntegritySemanticsV1.DigestValidated };
        var antiRollback = digest with
            { RecoveryIntegrity = RecoveryIntegritySemanticsV1.GenerationAndAntiRollbackValidated };

        Assert.Equal(antiRollback,
            DurabilitySemanticsV1.ParseCanonical(antiRollback.SerializeCanonical()));
        Assert.True(DurabilitySemanticPartialOrderV1.Refines(antiRollback, digest));
        Assert.False(DurabilitySemanticPartialOrderV1.Refines(digest, antiRollback));
        Assert.False(antiRollback.AuthorizesPublication);
        Assert.False(antiRollback.ProvesPhysicalPersistence);
        Assert.False(antiRollback.RestoresAuthority);
    }

    [Fact]
    public void CombinedMandatoryExtensionsFailWhenEitherGuaranteeIsWeakened()
    {
        var requiredFailure = Failure(FailureContainmentSemanticsV1.QuarantineAmbiguousEffect,
            FailureRecoverySemanticsV1.FreshAdmissionRequired);
        var requiredDurability = Durable(DurabilityDomainRequirementV1.ManagedModel,
            DurabilityAssuranceSemanticsV1.ModelOnly);
        var requirements = OperationSemanticExtensionsV1.Create([
            requiredFailure.ToClause(SemanticExtensionRequirement.Mandatory),
            requiredDurability.ToClause(SemanticExtensionRequirement.Mandatory)]);
        var exact = ExecutionGuaranteeExtensionsV1.Create([
            requiredFailure.ToClause(SemanticExtensionRequirement.Mandatory),
            requiredDurability.ToClause(SemanticExtensionRequirement.Mandatory)]);
        var weakFailure = ExecutionGuaranteeExtensionsV1.Create([
            (requiredFailure with { Recovery = FailureRecoverySemanticsV1.RebindRequired })
                .ToClause(SemanticExtensionRequirement.Mandatory),
            requiredDurability.ToClause(SemanticExtensionRequirement.Mandatory)]);
        var volatileDurability = ExecutionGuaranteeExtensionsV1.Create([
            requiredFailure.ToClause(SemanticExtensionRequirement.Mandatory),
            (requiredDurability with
            {
                Publication = DurablePublicationSemanticsV1.VolatileStaged,
                RecoveryIntegrity = RecoveryIntegritySemanticsV1.None,
            }).ToClause(SemanticExtensionRequirement.Mandatory)]);

        Assert.True(FailureDurabilityExtensionRefinementV1.Evaluate(requirements, exact).IsAccepted);
        Assert.False(FailureDurabilityExtensionRefinementV1.Evaluate(requirements, weakFailure).IsAccepted);
        Assert.False(FailureDurabilityExtensionRefinementV1.Evaluate(requirements, volatileDurability).IsAccepted);
    }

    [Fact]
    public void UnknownMandatoryDimensionFailsClosedAndOptionalAbsenceDoesNotGrantAnything()
    {
        var unknown = SemanticExtensionClauseV1.Create(new("unknown.failure"), "unknown/1", 1,
            SemanticExtensionRequirement.Mandatory, [1]);
        var requirements = OperationSemanticExtensionsV1.Create([unknown]);
        var empty = ExecutionGuaranteeExtensionsV1.Create([]);

        Assert.Equal(SemanticExtensionMatchStatus.UnknownMandatory,
            FailureDurabilityExtensionRefinementV1.Evaluate(requirements, empty).Status);

        var optional = OperationSemanticExtensionsV1.Create([
            SemanticExtensionClauseV1.Create(new("unknown.failure"), "unknown/1", 1,
                SemanticExtensionRequirement.Optional, [1])
        ]);
        Assert.True(FailureDurabilityExtensionRefinementV1.Evaluate(optional, empty).IsAccepted);
    }

    [Fact]
    public void MalformedKnownGuaranteeFailsClosedWithoutEscapingParserFailure()
    {
        var requirement = Failure(FailureContainmentSemanticsV1.QuarantineAmbiguousEffect,
            FailureRecoverySemanticsV1.RebindRequired)
            .ToClause(SemanticExtensionRequirement.Mandatory);
        var malformed = SemanticExtensionClauseV1.Create(FailureSemanticsV1.ExtensionClassId,
            "singnext.failure-semantics/1", 1, SemanticExtensionRequirement.Mandatory, [0]);

        var decision = FailureDurabilityExtensionRefinementV1.Evaluate(
            OperationSemanticExtensionsV1.Create([requirement]),
            ExecutionGuaranteeExtensionsV1.Create([malformed]));

        Assert.Equal(SemanticExtensionMatchStatus.GuaranteeDoesNotRefine, decision.Status);
    }

    [Fact]
    public void CombinedSidecarsBindGenerationVectorWithoutGrantingAuthority()
    {
        var failure = Failure(FailureContainmentSemanticsV1.ExactSubrangeQuarantine,
            FailureRecoverySemanticsV1.FreshAdmissionRequired);
        var durability = Durable(DurabilityDomainRequirementV1.ManagedModel,
            DurabilityAssuranceSemanticsV1.ModelOnly) with
            { RecoveryIntegrity = RecoveryIntegritySemanticsV1.GenerationAndAntiRollbackValidated };
        var requirements = OperationSemanticExtensionsV1.Create([
            failure.ToClause(SemanticExtensionRequirement.Mandatory),
            durability.ToClause(SemanticExtensionRequirement.Mandatory)]);
        var guarantees = ExecutionGuaranteeExtensionsV1.Create([
            failure.ToClause(SemanticExtensionRequirement.Mandatory),
            durability.ToClause(SemanticExtensionRequirement.Mandatory)]);
        var bound = SemanticBindingExtensionSetV1.Create(new string('a', 64), new string('b', 64),
            requirements, guarantees, [new("provider", 1), new("media", 2), new("failure-domain", 3)]);
        var stale = SemanticBindingExtensionSetV1.Create(new string('a', 64), new string('b', 64),
            requirements, guarantees, [new("provider", 1), new("media", 2), new("failure-domain", 4)]);

        Assert.NotEqual(bound.Digest, stale.Digest);
        Assert.Equal(64, bound.Digest.Value.Length);
        Assert.False(bound.AuthorizesExecution);
        Assert.False(bound.AuthorizesEffect);
    }

    [Fact]
    public void FailureAndDurabilityPartialOrdersAreReflexiveAndTransitiveOnQualifiedSamples()
    {
        var failures = new[]
        {
            Failure(FailureContainmentSemanticsV1.None, FailureRecoverySemanticsV1.RebindRequired, false),
            Failure(FailureContainmentSemanticsV1.QuarantineAmbiguousEffect, FailureRecoverySemanticsV1.RebindRequired),
            Failure(FailureContainmentSemanticsV1.ExactSubrangeQuarantine, FailureRecoverySemanticsV1.FreshAdmissionRequired),
        };
        var durability = new[]
        {
            new DurabilitySemanticsV1(1, DurablePublicationSemanticsV1.VolatileStaged,
                RecoveryIntegritySemanticsV1.None, DurabilityDomainRequirementV1.AnyNamedDomain,
                DurabilityAssuranceSemanticsV1.ModelOnly).Validate(),
            Durable(DurabilityDomainRequirementV1.ManagedModel, DurabilityAssuranceSemanticsV1.ModelOnly),
            Durable(DurabilityDomainRequirementV1.ManagedModel, DurabilityAssuranceSemanticsV1.ModelOnly) with
                { RecoveryIntegrity = RecoveryIntegritySemanticsV1.GenerationAndAntiRollbackValidated },
        };
        AssertOrder(failures, FailureSemanticPartialOrderV1.Refines);
        AssertOrder(durability, DurabilitySemanticPartialOrderV1.Refines);
    }

    private static FailureSemanticsV1 Failure(
        FailureContainmentSemanticsV1 containment, FailureRecoverySemanticsV1 recovery,
        bool closure = true) => new FailureSemanticsV1(1, containment, recovery, closure).Validate();

    private static DurabilitySemanticsV1 Durable(
        DurabilityDomainRequirementV1 domain, DurabilityAssuranceSemanticsV1 assurance) =>
        new DurabilitySemanticsV1(1, DurablePublicationSemanticsV1.DurableBeforePublication,
            RecoveryIntegritySemanticsV1.DigestValidated, domain, assurance).Validate();

    private static void AssertOrder<T>(IReadOnlyList<T> values, Func<T, T, bool> refines)
    {
        Assert.All(values, value => Assert.True(refines(value, value)));
        foreach (var strong in values)
        foreach (var middle in values)
        foreach (var weak in values)
            if (refines(strong, middle) && refines(middle, weak))
                Assert.True(refines(strong, weak));
    }
}
