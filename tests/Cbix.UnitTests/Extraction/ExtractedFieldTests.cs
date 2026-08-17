using System.Reflection;

using Cbix.Core.Extraction;

namespace Cbix.UnitTests.Extraction;

/// <summary>
/// The provenance envelope the whole pipeline stands on (story S01-16, design 5.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>These are conformance tests against the design, not behaviour tests.</b> The type has no
/// behaviour - it is a record of four members - and that is exactly why it needs pinning: story
/// S01-16's Done-means requires the shape to match design 5.4 EXACTLY so Sprint 02's grounding gate
/// consumes it unchanged, and a record's shape is the one thing a compiler will happily let anyone
/// change without a single call site complaining.
/// </para>
/// <para>
/// Pinned the same way <c>DocumentProfile</c> is pinned against design Appendix A, and for the
/// stronger reason: every published value's row in <c>field_provenance</c> (design 6) is built from
/// this, so a member renamed here is a change to the audit trail of everything the pipeline will ever
/// publish.
/// </para>
/// </remarks>
public sealed class ExtractedFieldTests
{
    [Fact]
    public void Shape_IsExactlyTheOneDesign54Declares()
    {
        // The design's line, verbatim:
        //   public sealed record ExtractedField<T>(
        //       T?      Value,
        //       int?    SourcePage,
        //       string? SourceSnippet,   // verbatim text copied from the document
        //       double  Confidence);
        //
        // Asserted through the PRIMARY CONSTRUCTOR rather than through the properties, because the
        // positional parameters are the record's declared shape: their names and their ORDER are what
        // a positional deconstruction, a `with` expression and any future source-generated mapper all
        // depend on. Two members swapped would leave the property set identical and every positional
        // use of the type silently wrong.
        ConstructorInfo constructor = Assert.Single(typeof(ExtractedField<string>).GetConstructors());

        Assert.Equal(
            ["Value", "SourcePage", "SourceSnippet", "Confidence"],
            constructor.GetParameters().Select(parameter => parameter.Name));

        Assert.Equal(
            [typeof(string), typeof(int?), typeof(string), typeof(double)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));

        // The NULLABLE ANNOTATIONS, which the CLR types above cannot show: `string` and `string?` are
        // the same runtime type, so the assertion above would pass just as happily against
        // `ExtractedField<T>(T Value, int? SourcePage, string SourceSnippet, double Confidence)` - a
        // shape that cannot express an absent value or an absent quotation at all, which is most of
        // what design 5.4's envelope is for. A test that claims to pin a shape has to read the
        // annotations, so it does.
        NullabilityInfoContext nullability = new();

        Assert.Equal(
            [
                NullabilityState.Nullable,   // Value
                NullabilityState.Nullable,   // SourcePage
                NullabilityState.Nullable,   // SourceSnippet
                NullabilityState.NotNull,    // Confidence
            ],
            constructor.GetParameters().Select(parameter => nullability.Create(parameter).WriteState));
    }

    [Fact]
    public void Confidence_IsTheOneMemberThatIsNeverNull()
    {
        // Design 5.4's own asymmetry, and it is deliberate rather than an oversight in the design: a
        // model that produced a field at all produced it with some certainty, so there is no such
        // thing as a field with no confidence. Making it nullable "for symmetry" would give every
        // consumer a null to handle that can never occur, and the first one to default it to zero would
        // be silently routing perfectly good extractions to review.
        ParameterInfo confidence = Assert.Single(
            typeof(ExtractedField<string>).GetConstructors()[0].GetParameters(),
            parameter => parameter.Name == "Confidence");

        Assert.Equal(typeof(double), confidence.ParameterType);
        Assert.Null(Nullable.GetUnderlyingType(confidence.ParameterType));
    }

    [Fact]
    public void Envelope_RecordsAnAbsenceWithItsOwnProvenance()
    {
        // The property that makes wrapping worth anything: an ABSENT value is still an envelope. The
        // prompting rules say to return null for a field the document does not state rather than
        // inventing one, and a section that dropped the whole envelope for such a field would lose the
        // difference between "the document does not say" and "nobody looked".
        ExtractedField<string> absent = new(null, 2, null, 0.9d);

        Assert.Null(absent.Value);
        Assert.Equal(2, absent.SourcePage);
        Assert.Equal(0.9d, absent.Confidence);
    }

    [Fact]
    public void Envelope_CarriesAnOptionalValueTypeThroughANullableTypeArgument()
    {
        // Why DocControlSection says ExtractedField<DateOnly?> rather than ExtractedField<DateOnly>.
        // `T?` on an UNCONSTRAINED type parameter carries no nullability for a value type - the
        // annotation is erased - so ExtractedField<DateOnly>.Value is a non-nullable DateOnly and an
        // absent date would arrive as 0001-01-01: a date the document does not contain, indistinguishable
        // from one it does. The nullable type argument is what keeps an absence expressible.
        Assert.Equal(typeof(DateOnly), Nullable.GetUnderlyingType(typeof(ExtractedField<DateOnly?>)
            .GetProperty(nameof(ExtractedField<DateOnly?>.Value))!.PropertyType));

        Assert.Null(new ExtractedField<DateOnly?>(null, null, null, 0.1d).Value);
    }

    [Fact]
    public void Equality_IsStructural()
    {
        // A record, so this is free - but it is asserted because Sprint 02's golden-set harness compares
        // extracted fields against expected ones, and a reference-equality envelope would make every
        // comparison fail while looking like an extraction regression.
        ExtractedField<string> first = new("CBTI-DE-2026-011", 1, "Document Reference CBTI-DE-2026-011", 0.98d);
        ExtractedField<string> second = new("CBTI-DE-2026-011", 1, "Document Reference CBTI-DE-2026-011", 0.98d);

        Assert.Equal(first, second);
        Assert.NotEqual(first, first with { SourcePage = 2 });
    }
}
