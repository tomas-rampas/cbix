using System.Reflection;

using Cbix.Agnosticism;

namespace Cbix.UnitTests;

/// <summary>
/// Build-time backstop for story S01-09's "zero Anthropic dependency" claim, asserted against
/// <c>Cbix.Agnosticism</c> - the assembly that declares the stub chat client and the stub-backed
/// composition the agnosticism run executes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists next to the BDD scenario rather than instead of it.</b> The scenario walks the
/// whole transitive closure of one run and is the story's acceptance criterion; this is one hop and
/// one assembly, and it holds whether or not that scenario is ever run. Story S01-09's "Done means"
/// asks for exactly this pairing - "a build-time or test-time dependency check ... a passing run
/// alone is not sufficient evidence" - and the two fail differently: the walk says "a provider became
/// reachable", this says "the stub assembly itself took a provider dependency".
/// </para>
/// <para>
/// <b>It is also the reason <c>Cbix.UnitTests</c> references that project at all.</b> The reference
/// is not incidental: it puts <c>Cbix.Agnosticism.dll</c> in this project's output, where
/// <see cref="Providers.CbixAssemblies"/> discovers it automatically and the existing containment
/// assertions cover it with no list to maintain. This class gives the specific diagnostic; discovery
/// gives the guarantee. Keep both, for the same reason <see cref="CoreAssemblyNeutralityTests"/> and
/// the BDD port scenario are both kept.
/// </para>
/// </remarks>
public sealed class StubProviderAssemblyNeutralityTests
{
    private static Assembly StubProvider => typeof(StubChatClient).Assembly;

    [Fact]
    public void StubProviderAssembly_ReferencesNoProviderAssembly()
    {
        // Ordinal-contains on the vendor token, not equality on two names: it has to catch
        // `Anthropic`, `Microsoft.Agents.AI.Anthropic` and any future `Anthropic.Something` without
        // anybody keeping a list current. The Cbix.Providers. prefix is banned alongside, because a
        // stub reaching this solution's own adapter is the same defect one hop earlier.
        List<string> offenders =
        [
            .. StubProvider.GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .Where(name => name.Contains("Anthropic", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Cbix.Providers.", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            offenders.Count == 0,
            $"The stub provider assembly references {string.Join(", ", offenders)}. It is the provider "
                + "side of the agnosticism run, so a provider dependency here would make design 3's "
                + "acceptance criterion circular: the run would be proving independence from a package it "
                + "had loaded to run at all.");
    }

    [Fact]
    public void StubProviderAssembly_StillBuildsAnAgentThroughTheNeutralPath()
    {
        // Guards the guard. An assembly that had lost its composition - emptied, or reduced to a data
        // holder - would pass the assertion above while proving nothing, because the property being
        // claimed is that the neutral path from an IChatClient to an AIAgent is what drives the
        // pipeline. These two references are that path: MAF's ChatClientAgent and the
        // Microsoft.Extensions.AI abstraction it wraps.
        List<string> referenced =
        [
            .. StubProvider.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty),
        ];

        Assert.Contains("Microsoft.Agents.AI", referenced, StringComparer.Ordinal);
        Assert.Contains("Microsoft.Extensions.AI.Abstractions", referenced, StringComparer.Ordinal);
        Assert.Contains("Cbix.Core", referenced, StringComparer.Ordinal);
    }

    [Fact]
    public void StubProviderAssembly_IsNotATestAssembly()
    {
        // Pins a decision made in Cbix.Agnosticism.csproj that nothing else would report. Everything
        // under tests/ inherits the shared test stack, and this project removes it deliberately:
        // Microsoft.NET.Test.Sdk would make `dotnet test Cbix.sln` collect an assembly with no tests
        // in it, which TreatNoTestsAsError in /.runsettings turns into a failed run, and a test
        // framework in the reference table would enlarge the very closure this assembly exists to
        // keep small. A future edit that re-adds the packages would be caught here rather than by a
        // confusing CI failure two projects away.
        Assert.DoesNotContain(
            StubProvider.GetReferencedAssemblies(),
            reference => (reference.Name ?? string.Empty).StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
    }
}
