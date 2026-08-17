using System.Reflection;

using Cbix.Core.Diagnostics;
using Cbix.Core.Documents;

using Microsoft.Extensions.Logging;

namespace Cbix.UnitTests.Diagnostics;

/// <summary>
/// The control that makes duplicate event ids impossible rather than merely discouraged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because the failure it prevents had already happened.</b> Event id <c>1015</c> was on
/// two unrelated <c>LoggerMessage</c> declarations - <c>DocumentIngestService</c>'s Critical
/// "the provider credential is refused, every document will fail" and
/// <c>PdfPigTextLayerExtractor</c>'s Error "this one document could not be opened". Both compiled,
/// both logged, and every review missed it, because nothing in the toolchain looks across files for a
/// repeated integer. An event id is an API - alert rules and dashboards are written against it - so a
/// collision means an operator's fleet-outage alarm fires on routine per-document noise.
/// </para>
/// <para>
/// <b>It asserts over the emitted attributes, not over <see cref="CbixEventIds"/>.</b> Checking the
/// registry's constants for uniqueness would be the easy test and the useless one: it would pass
/// while somebody wrote a literal at a call site, which is exactly how the original collision
/// happened. Reflecting over the <c>LoggerMessage</c> declarations asks the question that matters -
/// what ids does this assembly actually emit - and it is indifferent to whether the id came from the
/// registry or from a literal.
/// </para>
/// </remarks>
public sealed class CbixEventIdTests
{
    [Fact]
    public void NoTwoLoggerMessagesShareAnEventId()
    {
        List<(int EventId, string Site)> declarations = [.. LoggerMessageDeclarations()];

        // Fail closed. A reflection walk that found nothing would report perfect uniqueness over an
        // empty set - indistinguishable from a scanner broken by a changed attribute shape or a
        // source generator that stopped retaining the declaration.
        Assert.True(
            declarations.Count >= 10,
            $"Only {declarations.Count} LoggerMessage declarations were discovered in Cbix.Core; the "
                + "uniqueness assertion would be close to vacuous. Either the scan is broken or the "
                + "events have gone.");

        List<string> collisions =
        [
            .. declarations
                .GroupBy(declaration => declaration.EventId)
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key}: {string.Join(" and ", group.Select(item => item.Site).Order(StringComparer.Ordinal))}")
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            collisions.Count == 0,
            $"Two or more structured-logging events share an event id: {string.Join("; ", collisions)}. "
                + "An event id is the key alert rules and dashboards are written against, so a shared id "
                + "means one event's alarm fires on the other's traffic. Allocate a new constant in "
                + "CbixEventIds.");
    }

    [Fact]
    public void EveryEmittedEventIdIsNamedInTheRegistry()
    {
        // The registry is only useful if it is complete: an id written as a literal at a call site is
        // invisible to anyone reading CbixEventIds to pick the next free number, which is how a
        // collision gets introduced by someone doing their homework.
        HashSet<int> registered =
        [
            .. typeof(CbixEventIds)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(field => field is { IsLiteral: true, IsInitOnly: false, FieldType.FullName: "System.Int32" })
                .Select(field => (int)field.GetRawConstantValue()!),
        ];

        Assert.NotEmpty(registered);

        List<string> unregistered =
        [
            .. LoggerMessageDeclarations()
                .Where(declaration => !registered.Contains(declaration.EventId))
                .Select(declaration => $"{declaration.Site} (id {declaration.EventId})")
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            unregistered.Count == 0,
            $"These events use an id that no CbixEventIds constant names: {string.Join(", ", unregistered)}. "
                + "Add the constant, then reference it from the LoggerMessage attribute - a literal at the "
                + "call site is invisible to the next person choosing an id.");
    }

    [Fact]
    public void RegistryConstantsAreThemselvesDistinct()
    {
        // Cheap, and it catches the copy-paste that would otherwise make two well-behaved call sites
        // collide while both did exactly what this class asked of them.
        List<(string Name, int Value)> constants =
        [
            .. typeof(CbixEventIds)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(field => field is { IsLiteral: true, FieldType.FullName: "System.Int32" })
                .Select(field => (field.Name, (int)field.GetRawConstantValue()!)),
        ];

        List<string> duplicates =
        [
            .. constants
                .GroupBy(constant => constant.Value)
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key}: {string.Join(", ", group.Select(item => item.Name).Order(StringComparer.Ordinal))}")
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(duplicates.Count == 0, $"CbixEventIds names one id twice: {string.Join("; ", duplicates)}.");
    }

    [Fact]
    public void TheScanWouldHaveCaughtTheOriginalCollision()
    {
        // Positive control. Every assertion above passes over a clean assembly, which is
        // indistinguishable from a scan that silently matches nothing. This replays the real defect -
        // the 1015 that sat on both the credential-failure and the text-layer-open-failure events -
        // through the same grouping logic and proves it is reported.
        (int EventId, string Site)[] historical =
        [
            (1015, "DocumentIngestService.LogPreparationCredentialFailure"),
            (1015, "PdfPigTextLayerExtractor.LogTextLayerOpenFailed"),
            (1016, "DocumentIngestService.LogPreparationFailed"),
        ];

        List<int> collisions =
        [
            .. historical
                .GroupBy(declaration => declaration.EventId)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key),
        ];

        Assert.Equal([1015], collisions);
    }

    /// <summary>Every <c>LoggerMessage</c> declaration in <c>Cbix.Core</c>, with its id and site.</summary>
    /// <remarks>
    /// Non-public methods are included because every one of these declarations is a
    /// <c>private static partial</c> - which is the recommended shape and the reason a public-only
    /// reflection walk would have found nothing at all.
    /// </remarks>
    private static IEnumerable<(int EventId, string Site)> LoggerMessageDeclarations()
    {
        const BindingFlags Declarations =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        foreach (Type type in typeof(IDocumentContentProvider).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(Declarations))
            {
                if (method.GetCustomAttribute<LoggerMessageAttribute>() is { } attribute)
                {
                    yield return (attribute.EventId, $"{type.Name}.{method.Name}");
                }
            }
        }
    }
}
