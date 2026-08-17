using System.Text.Json.Nodes;

using Cbix.Core.Extraction;

namespace Cbix.UnitTests.Extraction;

/// <summary>
/// The strict half of the first real extraction (story S01-16).
/// </summary>
/// <remarks>
/// Every test but the first few is a refusal, and the ratio is the point: this is where "agents
/// propose, code disposes" is implemented for a section agent. The rule this parser owns beyond the
/// triage parser's idioms is <em>groundability</em> - a value the validator cannot check is a value
/// that would be published on the strength of nothing.
/// </remarks>
public sealed class DocControlSectionParserTests
{
    [Fact]
    public void Parse_ReadsTheDocumentControlBlock()
    {
        DocControlSection section = DocControlSectionParser.Parse(Valid());

        Assert.Equal("CBTI-DE-2026-011", section.DocRef.Value);
        Assert.Equal(1, section.DocRef.SourcePage);
        Assert.Equal("Document Reference CBTI-DE-2026-011", section.DocRef.SourceSnippet);
        Assert.Equal(0.98d, section.DocRef.Confidence);

        Assert.Equal("3.2", section.Version.Value);
        Assert.Equal("DE", section.CountryIso.Value);
        Assert.Equal("Approved", section.Status.Value);
        Assert.Equal(new DateOnly(2026, 8, 16), section.EffectiveDate.Value);
        Assert.Equal(new DateOnly(2027, 8, 15), section.ReviewDate.Value);
        Assert.Equal("CBTI-DE-2023-007 (v3.1)", section.Supersedes.Value);
        Assert.Equal("Legal - Cross-Border Trading Office, EMEA", section.Owner.Value);
    }

    [Fact]
    public void ContractFields_AreDesign6sCountryDocumentsColumns()
    {
        // The prompt is built from this list and the parser enforces it, so the list IS the contract -
        // and design 6's country_documents is where it comes from. The three columns deliberately
        // absent (extraction_run_id, valid_from, valid_to) are facts about the RUN, written by the
        // persist step; a section agent proposing them would be proposing something no document
        // contains.
        Assert.Equal(
            ["DocRef", "Version", "CountryIso", "Status", "EffectiveDate", "ReviewDate", "Supersedes", "Owner"],
            DocControlSectionParser.ContractFields);

        Assert.Equal(
            DocControlSectionParser.ContractFields.Order(StringComparer.Ordinal),
            typeof(DocControlSection)
                .GetProperties()
                .Where(property => !string.Equals(property.Name, "EqualityContract", StringComparison.Ordinal))
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));

        Assert.Equal(["Value", "SourcePage", "SourceSnippet", "Confidence"], DocControlSectionParser.EnvelopeMembers);
    }

    [Fact]
    public void Parse_AcceptsAnAbsentFieldAsANullValueWithNoProvenance()
    {
        // The prompting rules' own shape: a field the document does not state comes back null, and an
        // absence has nothing to quote. Demanding provenance for it would push a model towards
        // inventing a snippet to satisfy the schema - the precise behaviour the envelope exists to
        // prevent.
        DocControlSection section = DocControlSectionParser.Parse(
            Alter(node => node["Supersedes"] = new JsonObject
            {
                ["Value"] = null,
                ["SourcePage"] = null,
                ["SourceSnippet"] = null,
                ["Confidence"] = 0.4d,
            }));

        Assert.Null(section.Supersedes.Value);
        Assert.Null(section.Supersedes.SourcePage);
        Assert.Null(section.Supersedes.SourceSnippet);
        Assert.Equal(0.4d, section.Supersedes.Confidence);
    }

    [Theory]
    [InlineData("SourcePage")]
    [InlineData("SourceSnippet")]
    public void Parse_RefusesAValueTheGroundingGateCouldNotCheck(string missingProvenance)
    {
        // THE RULE THIS PARSER EXISTS FOR. Design 5.6's gate checks a verbatim snippet against the text
        // layer of the page the envelope names, so a value missing either half cannot be defended - and
        // would be published on the strength of nothing. Caught here rather than discovered in Sprint 02
        // as a gate with nothing to check.
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(Alter(node => node["DocRef"]![missingProvenance] = null)));

        Assert.Equal(SectionDefect.UngroundableValue, error.Defect);
        Assert.Equal("DocControl", error.Section);
        Assert.Contains(missingProvenance, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("   \r\n  ")]
    public void Parse_RefusesAWhitespaceSnippetThatWouldGroundEverywhere(string snippet)
    {
        // THE REGRESSION, and it was a security hole rather than a tidiness one. The groundability rule
        // used IsNullOrEmpty while the value check used IsNullOrWhiteSpace, so a snippet of a single
        // space was accepted as a quotation - and a single space is contained in every page of every
        // document, so the ordinal containment check passed too. A wholly fabricated reply carrying " "
        // in each snippet certified as fully grounded, which is the exact opposite of what the grounding
        // gate exists to do. Both U+0020 and U+000A were confirmed to bypass.
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(Alter(node => node["DocRef"]!["SourceSnippet"] = snippet)));

        Assert.Equal(SectionDefect.UngroundableValue, error.Defect);
    }

    [Fact]
    public void Parse_RefusesASnippetBelowTheEvidentialFloor()
    {
        // A single character is contained in essentially every page of every document, so "e" grounds
        // against a document nobody read. The floor removes that whole class.
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(Alter(node => node["DocRef"]!["SourceSnippet"] = "e")));

        Assert.Equal(SectionDefect.UngroundableValue, error.Defect);
    }

    [Fact]
    public void Parse_AcceptsASnippetExactlyAtTheEvidentialFloor()
    {
        // The other half of the pair, and the reason the floor is 2 rather than something that looks
        // stronger: the shortest legitimate quotation any contract admits is an ISO 3166-1 alpha-2
        // country code. A higher floor would refuse a correct extraction the first time a model quoted
        // one tightly - a false refusal on real data, which stops a working pipeline.
        DocControlSection section = DocControlSectionParser.Parse(
            Alter(node => node["CountryIso"]!["SourceSnippet"] = "DE"));

        Assert.Equal("DE", section.CountryIso.SourceSnippet);
    }

    [Fact]
    public void Parse_MeasuresTheFloorOnTrimmedTextButKeepsTheSnippetRaw()
    {
        // Padding is not evidence, so " e " is a one-character quotation and is refused. The converse
        // matters just as much: a snippet that PASSES is stored exactly as the model wrote it, because
        // containment is ordinal and a trimmed copy would fail to match text it was legitimately
        // copied from.
        Assert.Equal(
            SectionDefect.UngroundableValue,
            Assert.Throws<SectionFormatException>(
                () => DocControlSectionParser.Parse(Alter(node => node["DocRef"]!["SourceSnippet"] = " e "))).Defect);

        DocControlSection section = DocControlSectionParser.Parse(
            Alter(node => node["DocRef"]!["SourceSnippet"] = " Document Reference CBTI-DE-2026-011 "));

        Assert.Equal(" Document Reference CBTI-DE-2026-011 ", section.DocRef.SourceSnippet);
    }

    [Fact]
    public void Parse_TreatsAWhitespaceSnippetOnAnAbsentValueAsAbsent()
    {
        // The rule is about VALUES the gate must defend. An envelope with no value has nothing to
        // quote, so white space there is simply an absence written awkwardly - refusing it would push
        // models towards inventing a snippet to satisfy the schema.
        DocControlSection section = DocControlSectionParser.Parse(Alter(node => node["Supersedes"] = new JsonObject
        {
            ["Value"] = null,
            ["SourcePage"] = null,
            ["SourceSnippet"] = "   ",
            ["Confidence"] = 0.3d,
        }));

        Assert.Null(section.Supersedes.Value);
        Assert.Null(section.Supersedes.SourceSnippet);
    }

    [Fact]
    public void Parse_RefusesAnEnvelopeThatBothRepeatsAndOmitsAMember()
    {
        // The same two-errors-cancelling shape as the section-level case, one level down: a count-based
        // check inside the envelope would pass four members with SourcePage twice and Confidence
        // missing, and the missing member's lookup would then throw an exception no caller has a
        // contract for. The envelope check establishes presence by asking, so it refuses.
        string repeatsAndOmits = Valid().Replace(
            "\"Confidence\":0.98",
            "\"SourcePage\":1",
            StringComparison.Ordinal);

        SectionFormatException error =
            Assert.Throws<SectionFormatException>(() => DocControlSectionParser.Parse(repeatsAndOmits));

        Assert.Equal(SectionDefect.DuplicateField, error.Defect);
    }

    [Fact]
    public void Parse_RefusesABlankValueUnderItsOwnDefect()
    {
        // Blank used to bucket as WrongType, which sent an operator to look at a schema that was
        // working: the type was right and the content was not.
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(Alter(node => node["DocRef"]!["Value"] = "   ")));

        Assert.Equal(SectionDefect.BlankValue, error.Defect);
    }

    [Fact]
    public void Parse_RefusesABareValueThatIsNotAnEnvelopeAtAll()
    {
        // The likeliest way a model gets this wrong: answering the question and ignoring the contract
        // that makes the answer auditable. Its own defect because the fix is a prompt, not a schema.
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(Alter(node => node["DocRef"] = "CBTI-DE-2026-011")));

        Assert.Equal(SectionDefect.NotAnEnvelope, error.Defect);
    }

    [Fact]
    public void Parse_RefusesAPartialEnvelope()
    {
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(Alter(node => node["DocRef"]!.AsObject().Remove("Confidence"))));

        Assert.Equal(SectionDefect.MissingField, error.Defect);
        Assert.Contains("Confidence", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAnEnvelopeMemberDesign54DoesNotDeclare()
    {
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(Alter(node => node["DocRef"]!["BoundingBox"] = "0,0,10,10")));

        Assert.Equal(SectionDefect.UndeclaredField, error.Defect);
        Assert.Contains("BoundingBox", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026/08/16")]
    [InlineData("16-08-2026")]
    [InlineData("03/04/2026")]
    [InlineData("16 August 2026")]
    [InlineData("2026-13-01")]
    public void Parse_RefusesADateThatIsNotUnambiguousIso(string date)
    {
        // '03/04/2026' is the case that matters: two different dates depending on who wrote it, and an
        // effective date read the wrong way round is a permission that starts eleven months early. A
        // lenient parse would also make the answer depend on the host's locale, which is not a property
        // an audit trail may have.
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(Alter(node => node["EffectiveDate"]!["Value"] = date)));

        Assert.Equal(SectionDefect.MalformedDate, error.Defect);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Parse_RefusesAPageNumberThatIsAnIndexRatherThanAPage(int page)
    {
        // Logical page numbers are the ones a PDF viewer shows, so they start at one. A zero is a model
        // reporting an array index, and the grounding gate would then look for the snippet on a page
        // that does not exist and report a hallucination that is really an off-by-one.
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(Alter(node => node["DocRef"]!["SourcePage"] = page)));

        Assert.Equal(SectionDefect.PageOutOfRange, error.Defect);
    }

    [Theory]
    [InlineData(1.4)]
    [InlineData(-0.1)]
    public void Parse_RefusesAConfidenceOutsideTheUnitInterval(double confidence)
    {
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(Alter(node => node["DocRef"]!["Confidence"] = confidence)));

        Assert.Equal(SectionDefect.ConfidenceOutOfRange, error.Defect);
    }

    [Fact]
    public void Parse_RefusesAReplyThatBothRepeatsAndOmitsAField()
    {
        // The regression the triage parser records in full: two errors cancelling in a count-based
        // check, after which the omitted field's lookup throws an exception no caller has a contract
        // for. This parser establishes presence by asking field by field, so it refuses.
        string repeatsAndOmits = Valid()
            .Replace("\"Owner\":", "\"DocRef\":", StringComparison.Ordinal);

        SectionFormatException error =
            Assert.Throws<SectionFormatException>(() => DocControlSectionParser.Parse(repeatsAndOmits));

        Assert.Equal(SectionDefect.DuplicateField, error.Defect);
    }

    [Theory]
    [InlineData("Not JSON at all.")]
    [InlineData("")]
    [InlineData("[1, 2, 3]")]
    [InlineData("```json\nnot json\n```")]
    public void Parse_RefusesAReplyThatIsNotTheContractObject(string reply)
    {
        SectionFormatException error =
            Assert.Throws<SectionFormatException>(() => DocControlSectionParser.Parse(reply));

        Assert.Equal(SectionDefect.NotAJsonObject, error.Defect);
    }

    [Fact]
    public void Parse_UnwrapsACodeFence()
    {
        DocControlSection section = DocControlSectionParser.Parse("```json\n" + Valid() + "\n```");

        Assert.Equal("CBTI-DE-2026-011", section.DocRef.Value);
    }

    [Fact]
    public void Parse_RefusesAnOverLongReplyBeforeParsingIt()
    {
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(new string('x', (256 * 1024) + 1)));

        Assert.Equal(SectionDefect.ReplyTooLarge, error.Defect);
        Assert.True(error.Message.Length < 500, $"The refusal message is {error.Message.Length} characters long.");
    }

    [Fact]
    public void Parse_RefusesAnOverLongSnippetTheAuditTrailCouldNotKeep()
    {
        // Bounded at the audit trail's own width: design 6 sizes field_provenance.source_snippet at
        // nvarchar(1000), and a snippet that will not fit there is provenance the trail cannot keep.
        SectionFormatException error = Assert.Throws<SectionFormatException>(
            () => DocControlSectionParser.Parse(
                Alter(node => node["DocRef"]!["SourceSnippet"] = new string('x', 1001))));

        Assert.Equal(SectionDefect.ValueTooLong, error.Defect);
    }

    [Fact]
    public void Parse_ScrubsAHostileFieldNameOutOfTheRefusal()
    {
        // A model's reply is untrusted text from outside the estate, and the refusal lands in a log
        // line and on an operator's terminal. Built from escape sequences so a real U+202E does not
        // reverse how this file renders in an editor and a diff.
        string hostile = Valid().Replace("\"Owner\"", "\"Ow\\nner\\u202e\"", StringComparison.Ordinal);

        SectionFormatException error =
            Assert.Throws<SectionFormatException>(() => DocControlSectionParser.Parse(hostile));

        Assert.Equal(SectionDefect.UndeclaredField, error.Defect);
        Assert.DoesNotContain('\n', error.Message);
        Assert.DoesNotContain((char)0x202E, error.Message);
        Assert.Contains((char)0xFFFD, error.Message);
    }

    [Fact]
    public void Parse_RejectsANullReply() =>
        Assert.Throws<ArgumentNullException>(() => DocControlSectionParser.Parse(null!));

    /// <summary>
    /// A well-formed DocControl reply for the DE specimen.
    /// </summary>
    /// <remarks>
    /// Built here rather than borrowed from the BDD project's canned reply, even though the two are
    /// deliberately the same shape: a unit-test project taking a reference on the BDD project would
    /// invert the dependency, and these tests do not need the snippets to be REAL. That they are
    /// verbatim substrings of the specimen is what the grounding scenarios assert, and asserting it
    /// here as well would move a document-dependent claim into a test that never opens the document.
    /// </remarks>
    private static string Valid()
    {
        static JsonObject Envelope(string? value, string snippet, double confidence) => new()
        {
            ["Value"] = value,
            ["SourcePage"] = 1,
            ["SourceSnippet"] = snippet,
            ["Confidence"] = confidence,
        };

        return new JsonObject
        {
            ["DocRef"] = Envelope("CBTI-DE-2026-011", "Document Reference CBTI-DE-2026-011", 0.98d),
            ["Version"] = Envelope("3.2", "Version / Status 3.2 / Approved", 0.97d),
            ["CountryIso"] = Envelope("DE", "Jurisdiction Federal Republic of Germany (DE)", 0.99d),
            ["Status"] = Envelope("Approved", "Version / Status 3.2 / Approved", 0.96d),
            ["EffectiveDate"] = Envelope("2026-08-16", "Effective Date 2026-08-16", 0.98d),
            ["ReviewDate"] = Envelope("2027-08-15", "Next Review Date 2027-08-15", 0.97d),
            ["Supersedes"] = Envelope("CBTI-DE-2023-007 (v3.1)", "Supersedes CBTI-DE-2023-007 (v3.1)", 0.95d),
            ["Owner"] = Envelope(
                "Legal - Cross-Border Trading Office, EMEA",
                "Document Owner Legal - Cross-Border Trading Office, EMEA",
                0.94d),
        }.ToJsonString();
    }

    /// <summary>A valid reply with exactly one thing changed.</summary>
    private static string Alter(Action<JsonObject> alteration)
    {
        JsonObject section = JsonNode.Parse(Valid())!.AsObject();
        alteration(section);

        return section.ToJsonString();
    }
}
