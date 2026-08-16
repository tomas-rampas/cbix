using Cbix.Core.Secrets;

namespace Cbix.UnitTests.Secrets;

/// <summary>
/// Tests for the secret-resolution port and its layered stand-in (story S01-17).
/// <para>
/// Precedence is asserted here rather than through the worker's composition on purpose: at this
/// level the resolved value is directly observable, whereas the composed graph deliberately exposes
/// no way to read the key back. The composition tests prove the wiring; these prove the rule the
/// wiring depends on.
/// </para>
/// <para>
/// No test in this file touches process-global state. The sources are plain delegates, which is the
/// whole reason the port takes delegates rather than reaching for the environment itself.
/// </para>
/// </summary>
public sealed class LayeredSecretResolverTests
{
    /// <summary>
    /// Self-labelling placeholders. Deliberately no <c>sk-ant-</c> vendor prefix: secret scanners
    /// pattern-match that shape, and a repository that trips its own scanner on test data teaches
    /// everyone to ignore the alerts.
    /// </summary>
    private const string PreferredValue = "unit-test-preferred-placeholder-not-a-real-credential";

    private const string FallbackValue = "unit-test-fallback-placeholder-not-a-real-credential";

    private const string SecretName = "Cbix:Providers:Anthropic:ApiKey";

    private static SecretSource Constant(string description, string? value) =>
        new(description, _ => value);

    [Fact]
    public void TryResolve_TakesTheFirstSourceThatCarriesTheValue()
    {
        LayeredSecretResolver resolver = new(
        [
            Constant("the deployment's scoped configuration key", PreferredValue),
            Constant("the vendor-wide environment variable", FallbackValue),
        ]);

        // The precedence decision in one assertion: an ambient, machine-wide variable must not
        // displace the credential this deployment was explicitly configured with.
        Assert.Equal(PreferredValue, resolver.TryResolve(SecretName));
    }

    [Fact]
    public void TryResolve_FallsThroughToTheNextSourceWhenTheFirstIsAbsent()
    {
        LayeredSecretResolver resolver = new(
        [
            Constant("the deployment's scoped configuration key", null),
            Constant("the vendor-wide environment variable", FallbackValue),
        ]);

        Assert.Equal(FallbackValue, resolver.TryResolve(SecretName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_TreatsABlankValueAsAbsentRatherThanAsACredential(string blank)
    {
        // An environment variable exported with no value is the common shape of this. Accepting it
        // would carry a blank credential to a 401 on the first API call - mid-run, after
        // checkpoints - instead of using the source the operator actually populated.
        LayeredSecretResolver resolver = new(
        [
            Constant("the deployment's scoped configuration key", blank),
            Constant("the vendor-wide environment variable", FallbackValue),
        ]);

        Assert.Equal(FallbackValue, resolver.TryResolve(SecretName));
    }

    [Fact]
    public void TryResolve_ReturnsNullWhenNoSourceCarriesTheSecret()
    {
        LayeredSecretResolver resolver = new([Constant("the only source", null)]);

        // Null rather than an exception: absence and "the store is unreachable" are different
        // failures, and only the caller knows whether this particular secret is required.
        Assert.Null(resolver.TryResolve(SecretName));
    }

    [Fact]
    public void TryResolve_PassesTheRequestedNameToEachSource()
    {
        List<string> requested = [];
        LayeredSecretResolver resolver = new(
        [
            new SecretSource(
                "records what it was asked for",
                name =>
                {
                    requested.Add(name);
                    return null;
                }),
            Constant("the vendor-wide environment variable", FallbackValue),
        ]);

        resolver.TryResolve(SecretName);

        Assert.Equal([SecretName], requested);
    }

    [Fact]
    public void TryResolve_LetsAFailingSourcePropagateInsteadOfLookingLikeAnAbsence()
    {
        // An unreachable vault must not be reported as "nobody configured this": the two failures
        // have different fixes, and silently falling through would hide an outage behind a
        // misleading startup message.
        LayeredSecretResolver resolver = new(
        [
            new SecretSource("an unreachable store", _ => throw new InvalidOperationException("vault down")),
            Constant("the vendor-wide environment variable", FallbackValue),
        ]);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => resolver.TryResolve(SecretName));

        Assert.Equal("vault down", error.Message);
    }

    [Fact]
    public void SourceDescriptions_ReportThePrecedenceOrder()
    {
        LayeredSecretResolver resolver = new(
        [
            Constant("first", PreferredValue),
            Constant("second", FallbackValue),
        ]);

        // Order is the contract, not just the content: the startup failure message numbers these,
        // so an operator reads them as "try here first".
        Assert.Equal(["first", "second"], resolver.SourceDescriptions);
    }

    [Fact]
    public void Constructor_RejectsAnEmptySourceList()
    {
        // A resolver over no sources answers "absent" for everything, which is indistinguishable
        // from a correctly composed deployment whose secret is genuinely missing.
        Assert.Throws<ArgumentException>(() => new LayeredSecretResolver([]));
    }

    [Fact]
    public void Constructor_RejectsANullSource() =>
        Assert.Throws<ArgumentException>(() => new LayeredSecretResolver([null!]));

    [Fact]
    public void Constructor_RejectsNullSources() =>
        Assert.Throws<ArgumentNullException>(() => new LayeredSecretResolver(null!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_RejectsABlankSecretName(string? secretName)
    {
        LayeredSecretResolver resolver = new([Constant("the only source", PreferredValue)]);

        Assert.ThrowsAny<ArgumentException>(() => resolver.TryResolve(secretName!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SecretSource_RejectsABlankDescription(string? description)
    {
        // The description is what a startup failure shows an operator. A blank one produces a
        // message that names a place with no name.
        Assert.ThrowsAny<ArgumentException>(() => new SecretSource(description!, _ => null));
    }

    [Fact]
    public void SecretSource_RejectsANullLookup() =>
        Assert.Throws<ArgumentNullException>(() => new SecretSource("a source", null!));

    [Fact]
    public void Require_ReturnsTheValueWhenASourceCarriesIt()
    {
        LayeredSecretResolver resolver = new([Constant("the only source", PreferredValue)]);

        Assert.Equal(PreferredValue, resolver.Require(SecretName));
    }

    [Fact]
    public void Require_FailsNamingEverySourceInPrecedenceOrder()
    {
        LayeredSecretResolver resolver = new(
        [
            Constant("user-secrets under configuration key 'Cbix:Providers:Anthropic:ApiKey'", null),
            Constant("the 'ANTHROPIC_API_KEY' environment variable", null),
        ]);

        SecretNotFoundException error =
            Assert.Throws<SecretNotFoundException>(() => resolver.Require(SecretName));

        Assert.Equal(SecretName, error.SecretName);
        Assert.Equal(resolver.SourceDescriptions, error.SourceDescriptions);

        // The message is what an operator acts on, so both sources have to be in it, numbered in
        // the order the resolver consults them.
        Assert.Contains(SecretName, error.Message, StringComparison.Ordinal);
        Assert.Contains("(1) user-secrets under configuration key", error.Message, StringComparison.Ordinal);
        Assert.Contains("(2) the 'ANTHROPIC_API_KEY' environment variable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Require_MessageNeverEchoesAValueAnySourceHolds()
    {
        // The one case where a value IS present but the requested secret is not: a resolver whose
        // sources hold other credentials must not spill them while reporting a different miss.
        // Exception messages reach logs, telemetry and the review UI.
        LayeredSecretResolver resolver = new(
        [
            new SecretSource(
                "holds a different secret",
                name => string.Equals(name, "Cbix:Some:Other:Secret", StringComparison.Ordinal)
                    ? PreferredValue
                    : null),
            Constant("holds nothing", null),
        ]);

        SecretNotFoundException error =
            Assert.Throws<SecretNotFoundException>(() => resolver.Require(SecretName));

        Assert.DoesNotContain(PreferredValue, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Require_RejectsANullResolver() =>
        Assert.Throws<ArgumentNullException>(() => ((ISecretResolver)null!).Require(SecretName));
}
