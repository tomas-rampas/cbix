using System.Globalization;
using System.Text.Json;

namespace Cbix.Core.Extraction;

/// <summary>
/// Reads a DocControl agent's reply into a <see cref="DocControlSection"/>, or refuses it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The "code disposes" half of the first real extraction.</b> Every rule is a refusal rather than
/// a repair, for the reason story S01-14's triage parser records: each repair a parser is willing to
/// make is a decision transferred from the pipeline to the model, and each one is invisible
/// afterwards - a defaulted field reads exactly like an extracted one, and a value whose snippet was
/// quietly dropped reads exactly like a grounded one.
/// </para>
/// <para>
/// <b>One rule is this parser's own, and it is the one the whole story turns on: a value must be
/// groundable.</b> An envelope carrying a value but no page, or a value but no snippet, is refused -
/// because design 5.6's gate checks a snippet against the text layer of the page the envelope names,
/// so a value missing either half is one the gate cannot defend and would therefore publish on the
/// strength of nothing. The converse is deliberately allowed: an envelope with a <see langword="null"/>
/// value needs no provenance, since the prompting rules ask for null when the document does not state
/// something, and an absence has nothing to quote.
/// </para>
/// <para>
/// The structural idioms - the reply-size bound before parsing, the single code-fence normalisation,
/// the exact field set with distinct names and positive per-field presence, the sanitised refusals -
/// are <see cref="DocumentProfileParser"/>'s, shared through <see cref="ExtractionText"/> so the two
/// boundaries cannot drift apart on a security bound.
/// </para>
/// </remarks>
public static class DocControlSectionParser
{
    /// <summary>The section this parser reads, as it appears on a refusal and in the retry edge.</summary>
    public const string SectionName = "DocControl";

    /// <summary>The fields the DocControl contract declares, in declaration order.</summary>
    /// <remarks>
    /// Public because the prompt has to ask for exactly what the parser will accept, and two lists
    /// that must agree should be one list. The prompt in <c>DocControlExecutor</c> is built from this.
    /// </remarks>
    public static readonly IReadOnlyList<string> ContractFields =
        ["DocRef", "Version", "CountryIso", "Status", "EffectiveDate", "ReviewDate", "Supersedes", "Owner"];

    /// <summary>The members design 5.4's provenance envelope declares, in its order.</summary>
    public static readonly IReadOnlyList<string> EnvelopeMembers =
        ["Value", "SourcePage", "SourceSnippet", "Confidence"];

    private static readonly JsonDocumentOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,

        // One level deeper than the profile parser's, because this contract is one level deeper: the
        // object, then an envelope per field. Still nowhere near enough for a looping reply to be
        // answered with a stack overflow.
        MaxDepth = 8,
    };

    /// <summary>Parses a DocControl reply into a section, refusing anything that is not one.</summary>
    /// <param name="reply">The agent's verbatim reply text.</param>
    /// <returns>The section the reply declares.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reply"/> is <see langword="null"/>.</exception>
    /// <exception cref="SectionFormatException">The reply does not match the DocControl contract.</exception>
    public static DocControlSection Parse(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        using JsonDocument document = ReadObject(reply);
        JsonElement root = document.RootElement;

        EnsureExactlyTheContractFields(root);

        return new DocControlSection(
            ReadText(root, "DocRef"),
            ReadText(root, "Version"),
            ReadText(root, "CountryIso"),
            ReadText(root, "Status"),
            ReadDate(root, "EffectiveDate"),
            ReadDate(root, "ReviewDate"),
            ReadText(root, "Supersedes"),
            ReadText(root, "Owner"));
    }

    /// <summary>Reads the reply as a JSON object, unwrapping a code fence but never searching inside prose.</summary>
    private static JsonDocument ReadObject(string reply)
    {
        // First, before Trim, before Unfence, before the parser sees a byte of it: every step below
        // allocates in proportion to the input.
        if (reply.Length > ExtractionText.MaxReplyLength)
        {
            throw Refuse(
                SectionDefect.ReplyTooLarge,
                $"The DocControl reply is {reply.Length} characters; this parser reads at most "
                    + $"{ExtractionText.MaxReplyLength}. A section is a few kilobytes at most, so a reply this "
                    + "size is not one - and parsing it to discover that would cost several times its length "
                    + "in allocation first.");
        }

        string payload = ExtractionText.Unfence(reply.Trim());

        if (payload.Length == 0)
        {
            throw Refuse(
                SectionDefect.NotAJsonObject,
                "The DocControl agent replied with nothing. A section cannot be inferred from silence, and "
                    + "guessing one would put the pipeline's authority behind values no model produced.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, ReaderOptions);
        }
        catch (JsonException error)
        {
            throw Refuse(
                SectionDefect.NotAJsonObject,
                "The DocControl agent's reply is not JSON. The prompt asks for the section object and "
                    + "nothing else; a reply carrying prose, an apology or a partial answer is a proposal the "
                    + "pipeline declines rather than one it repairs.",
                error);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            JsonValueKind kind = document.RootElement.ValueKind;
            document.Dispose();

            throw Refuse(
                SectionDefect.NotAJsonObject,
                $"The DocControl agent's reply is JSON of kind '{kind}', not the section object the contract "
                    + "declares.");
        }

        return document;
    }

    /// <summary>
    /// Refuses a reply whose field set is not exactly the contract's, each field exactly once.
    /// </summary>
    /// <remarks>
    /// Three checks - declared name, distinct name, and a POSITIVE per-field presence check - because
    /// only all three together deliver the postcondition the reads below rely on. Recorded on
    /// <see cref="DocumentProfileParser"/> in full: a count-based check passes a reply that repeats one
    /// declared field and omits another, after which the omitted field's lookup throws an exception no
    /// caller has a contract for.
    /// </remarks>
    private static void EnsureExactlyTheContractFields(JsonElement root)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!ContractFields.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Refuse(
                    SectionDefect.UndeclaredField,
                    $"The DocControl reply carries a field the contract does not declare: "
                        + $"'{ExtractionText.ForMessage(property.Name)}'. The contract is "
                        + $"({string.Join(", ", ContractFields)}); an unexpected field means the model answered "
                        + "a different question from the one that was asked.");
            }

            if (!seen.Add(property.Name))
            {
                throw Refuse(
                    SectionDefect.DuplicateField,
                    $"The DocControl reply gives '{ExtractionText.ForMessage(property.Name)}' more than once. "
                        + "Which of two values is the answer is not a question a parser may decide.");
            }
        }

        List<string> missing = [.. ContractFields.Where(field => !seen.Contains(field))];

        if (missing.Count > 0)
        {
            throw Refuse(
                SectionDefect.MissingField,
                $"The DocControl reply omits {string.Join(", ", missing)}. Design 6's country_documents "
                    + "declares each of these, and defaulting one would record data no model produced.");
        }
    }

    /// <summary>Reads one field's envelope, carrying a string value.</summary>
    private static ExtractedField<string> ReadText(JsonElement root, string field)
    {
        JsonElement envelope = RequireEnvelope(root, field);

        string? value = null;

        if (ValueElement(envelope) is { ValueKind: not JsonValueKind.Null } element)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw Refuse(
                    SectionDefect.WrongType,
                    $"The DocControl reply gives '{field}' as JSON '{element.ValueKind}', but the contract "
                        + "declares it as text. Coercing it would let the model choose the type of a value the "
                        + "pipeline publishes.");
            }

            value = element.GetString();

            if (string.IsNullOrWhiteSpace(value))
            {
                // Blank is not an absence and not a value. The prompting rules give a model exactly one
                // way to say "the document does not state this" - a null - so a blank is a model that
                // answered without answering, and it would travel as data.
                throw Refuse(
                    SectionDefect.BlankValue,
                    $"The DocControl reply gives '{field}' as a blank string. The prompt asks for null when "
                        + "the document does not state a value; blank is neither that nor an answer.");
            }

            if (value.Length > ExtractionText.MaxFieldLength)
            {
                throw Refuse(
                    SectionDefect.ValueTooLong,
                    $"The DocControl reply gives '{field}' as {value.Length} characters; the contract accepts "
                        + $"at most {ExtractionText.MaxFieldLength}. These values are copied out of a "
                        + "document-control block and none is longer than a line.");
            }
        }

        return BuildEnvelope(envelope, field, value);
    }

    /// <summary>Reads one field's envelope, carrying an ISO calendar date.</summary>
    private static ExtractedField<DateOnly?> ReadDate(JsonElement root, string field)
    {
        JsonElement envelope = RequireEnvelope(root, field);

        DateOnly? value = null;

        if (ValueElement(envelope) is { ValueKind: not JsonValueKind.Null } element)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw Refuse(
                    SectionDefect.WrongType,
                    $"The DocControl reply gives '{field}' as JSON '{element.ValueKind}', but the contract "
                        + "declares it as an ISO 8601 date in a string.");
            }

            string text = element.GetString() ?? string.Empty;

            // EXACT, invariant, ISO only. Refused rather than best-guessed: '03/04/2026' is two
            // different dates depending on who wrote it, and an effective date read the wrong way round
            // is a permission that starts eleven months early. A lenient parse would also make the
            // answer depend on the host's locale, which is not a property an audit trail may have.
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
            {
                throw Refuse(
                    SectionDefect.MalformedDate,
                    $"The DocControl reply gives '{field}' as '{ExtractionText.ForMessage(text)}', which is not "
                        + "an unambiguous ISO 8601 calendar date (yyyy-MM-dd). Guessing at a date format is how "
                        + "an effective date ends up eleven months out.");
            }

            value = parsed;
        }

        return BuildEnvelope(envelope, field, value);
    }

    /// <summary>Refuses a field that is not a provenance envelope at all.</summary>
    private static JsonElement RequireEnvelope(JsonElement root, string field)
    {
        JsonElement envelope = root.GetProperty(field);

        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw Refuse(
                SectionDefect.NotAnEnvelope,
                $"The DocControl reply gives '{field}' as JSON '{envelope.ValueKind}' rather than a provenance "
                    + $"envelope ({string.Join(", ", EnvelopeMembers)}). A bare value is a model that answered "
                    + "the question and ignored the contract that makes the answer auditable - design 8 needs "
                    + "the page and the source text, not just the value.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (JsonProperty member in envelope.EnumerateObject())
        {
            if (!EnvelopeMembers.Contains(member.Name, StringComparer.Ordinal))
            {
                throw Refuse(
                    SectionDefect.UndeclaredField,
                    $"The envelope for '{field}' carries a member design 5.4 does not declare: "
                        + $"'{ExtractionText.ForMessage(member.Name)}'.");
            }

            if (!seen.Add(member.Name))
            {
                throw Refuse(
                    SectionDefect.DuplicateField,
                    $"The envelope for '{field}' gives '{ExtractionText.ForMessage(member.Name)}' more than once.");
            }
        }

        List<string> missing = [.. EnvelopeMembers.Where(member => !seen.Contains(member))];

        if (missing.Count > 0)
        {
            throw Refuse(
                SectionDefect.MissingField,
                $"The envelope for '{field}' omits {string.Join(", ", missing)}. Design 5.4's envelope is the "
                    + "shape Sprint 02's grounding gate consumes unchanged, so a partial one is a value that "
                    + "gate cannot read.");
        }

        return envelope;
    }

    /// <summary>Reads an envelope's <c>Value</c> member.</summary>
    /// <remarks>
    /// A plain <c>GetProperty</c>, matching <see cref="ReadPage"/>, <see cref="ReadSnippet"/> and
    /// <see cref="ReadConfidence"/>. One style throughout, and it is the one that relies on
    /// <see cref="RequireEnvelope"/>'s postcondition - every declared member present exactly once -
    /// rather than re-checking it here. The earlier <c>TryGetProperty</c>-with-a-refusal was a second,
    /// unreachable guard whose only effect was to suggest the postcondition could not be relied on,
    /// which would in turn invite the next member's reader to hedge too.
    /// </remarks>
    private static JsonElement ValueElement(JsonElement envelope) => envelope.GetProperty("Value");

    /// <summary>Builds the envelope around a parsed value, enforcing the groundability rule.</summary>
    private static ExtractedField<T> BuildEnvelope<T>(JsonElement envelope, string field, T? value)
    {
        int? page = ReadPage(envelope, field);
        string? snippet = ReadSnippet(envelope, field);
        double confidence = ReadConfidence(envelope, field);

        // THE RULE THIS PARSER EXISTS FOR. A value the grounding gate cannot check is a value that
        // would be published on the strength of nothing, so it is refused here rather than discovered
        // in Sprint 02 as a gate that had nothing to check. The converse is allowed on purpose: an
        // absent value has nothing to quote, and demanding provenance for it would push models towards
        // inventing a snippet to satisfy the schema - the exact behaviour the whole envelope exists to
        // prevent.
        //
        // MEASURED HOLE, now closed, and worth recording because the two halves of this class disagreed
        // in a way that read as correct: the VALUE check used IsNullOrWhiteSpace while this used
        // IsNullOrEmpty, so a snippet of a single space satisfied it - and a single space is contained
        // in every page of every document, so the ordinal containment check passed too. A wholly
        // fabricated reply carrying " " in each snippet certified as fully grounded. Both U+0020 and
        // U+000A were confirmed to bypass. ReadSnippet now treats white space as absent and enforces a
        // meaningful floor, and this check agrees with it.
        if (value is not null && (page is null || string.IsNullOrWhiteSpace(snippet)))
        {
            throw Refuse(
                SectionDefect.UngroundableValue,
                $"The DocControl reply gives '{field}' a value with no "
                    + $"{(page is null ? "SourcePage" : "SourceSnippet")}. Design 5.6's grounding gate checks a "
                    + "verbatim snippet against the text layer of the page the envelope names, so a value "
                    + "missing either half cannot be defended and must not be published.");
        }

        return new ExtractedField<T>(value, page, snippet, confidence);
    }

    private static int? ReadPage(JsonElement envelope, string field)
    {
        JsonElement page = envelope.GetProperty("SourcePage");

        if (page.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (page.ValueKind != JsonValueKind.Number || !page.TryGetInt32(out int number))
        {
            throw Refuse(
                SectionDefect.WrongType,
                $"The envelope for '{field}' gives SourcePage as JSON '{page.ValueKind}', but a logical page "
                    + "number is an integer.");
        }

        // Logical page numbers are what a PDF viewer shows, so they start at one. A zero or a negative
        // is a model that reported an array index, and the grounding gate would look for the snippet on
        // a page that does not exist and report a hallucination that is really an off-by-one.
        if (number < Ingest.TextLayer.FirstLogicalPageNumber)
        {
            throw Refuse(
                SectionDefect.PageOutOfRange,
                $"The envelope for '{field}' reports source page {number}. Logical page numbers are the ones a "
                    + "PDF viewer shows, so they start at "
                    + $"{Ingest.TextLayer.FirstLogicalPageNumber}; a lower number is an index, not a page.");
        }

        return number;
    }

    private static string? ReadSnippet(JsonElement envelope, string field)
    {
        JsonElement snippet = envelope.GetProperty("SourceSnippet");

        if (snippet.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (snippet.ValueKind != JsonValueKind.String)
        {
            throw Refuse(
                SectionDefect.WrongType,
                $"The envelope for '{field}' gives SourceSnippet as JSON '{snippet.ValueKind}', but a verbatim "
                    + "quotation is text.");
        }

        string text = snippet.GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            // Empty AND white space are both treated as absent rather than as quotations - see the
            // recorded hole on BuildEnvelope for why the distinction was a security bug rather than a
            // style question. An envelope with no value legitimately has nothing to quote, and
            // BuildEnvelope refuses the combination that actually matters: a VALUE with no snippet.
            return null;
        }

        // The evidential floor. Measured: a single character is contained in essentially every page of
        // every document, so a one-character snippet "grounds" against a document nobody read. Length
        // is measured on the TRIMMED text because leading and trailing space is not evidence, while the
        // RAW text is what is kept - containment is ordinal, so a stored snippet must stay byte-exact.
        // See ExtractionText.MinSnippetLength for why the floor is 2 and not more.
        if (text.Trim().Length < ExtractionText.MinSnippetLength)
        {
            throw Refuse(
                SectionDefect.UngroundableValue,
                $"The envelope for '{field}' carries a {text.Trim().Length}-character snippet; a quotation "
                    + $"shorter than {ExtractionText.MinSnippetLength} characters is contained in almost every "
                    + "page of almost every document, so it would satisfy the grounding gate without being "
                    + "evidence of anything.");
        }

        if (text.Length > ExtractionText.MaxSnippetLength)
        {
            throw Refuse(
                SectionDefect.ValueTooLong,
                $"The envelope for '{field}' carries a {text.Length}-character snippet; the audit trail's "
                    + $"source_snippet column holds {ExtractionText.MaxSnippetLength}. A snippet that will not "
                    + "fit there is provenance the trail cannot keep.");
        }

        return text;
    }

    private static double ReadConfidence(JsonElement envelope, string field)
    {
        JsonElement confidence = envelope.GetProperty("Confidence");

        if (confidence.ValueKind != JsonValueKind.Number || !confidence.TryGetDouble(out double value))
        {
            throw Refuse(
                SectionDefect.WrongType,
                $"The envelope for '{field}' gives Confidence as JSON '{confidence.ValueKind}', but design 5.4 "
                    + "declares it as a number - and it is the only member of the envelope that is never null.");
        }

        if (!double.IsFinite(value) || value < 0d || value > 1d)
        {
            throw Refuse(
                SectionDefect.ConfidenceOutOfRange,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The envelope for '{field}' reports a confidence of {value}, which is outside the unit "
                        + $"interval. Clamping it would manufacture certainty the model never claimed."));
        }

        // Negative zero normalised, for the reason the triage parser records: it passes every check
        // (IEEE says -0.0 == 0.0) and then renders as "-0" wherever it is shown, where it reads as a
        // corrupt value rather than as the "no idea at all" it is.
        return value == 0d ? 0d : value;
    }

    private static SectionFormatException Refuse(SectionDefect defect, string message, Exception? inner = null) =>
        new(SectionName, defect, message, inner);
}
