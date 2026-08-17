using System.Globalization;
using System.Text.Json;

namespace Cbix.Core.Extraction;

/// <summary>
/// Reads a triage agent's reply into a <see cref="DocumentProfile"/>, or refuses it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the "code disposes" half of the first agent call.</b> The model proposes a reply; this
/// decides whether the reply is admissible at all. Every rule below is a refusal rather than a
/// repair, because each repair a parser is willing to make is a decision transferred from the
/// pipeline to the model: a missing field filled with a default becomes data nobody extracted, a
/// coerced type becomes a value the document never stated, and an ignored extra field hides the
/// moment a prompt and a schema stopped agreeing.
/// </para>
/// <para>
/// <b>The one normalisation, stated exactly so it is not mistaken for leniency.</b> A reply wrapped
/// in a Markdown code fence is unwrapped before parsing. That is a statement about transport, not
/// about content: the fence is how chat-shaped models present a code block, it is emitted by several
/// providers regardless of instructions, and removing it is deterministic and total - it either is a
/// fence or it is not. What it is <em>not</em> is permission to hunt for JSON inside prose: a reply
/// with an explanatory sentence before the object is refused, because "find the JSON in here" is a
/// search whose failure mode is finding the wrong thing.
/// </para>
/// <para>
/// <b>Nothing model-written reaches a message unsanitised.</b> The only fragment a refusal needs is
/// the name of an undeclared or repeated field, and it is rendered through <see cref="ExtractionText.ForMessage"/> -
/// the same replace-and-bound treatment the ingest boundary gives a submitted path, for the same
/// reason: the message lands in a log, in a review-queue row and on an operator's terminal.
/// </para>
/// <para>
/// <b>Size is bounded here too, because this is the boundary.</b> A whole reply past
/// <see cref="MaxReplyLength"/> is refused before it is parsed, and any single field past
/// <see cref="MaxFieldLength"/> is refused before it is returned. Both bounds are deliberately the
/// parser's rather than a provider's: an adapter's output-token setting is a fact about one vendor,
/// and the agnosticism constraint makes the vendor replaceable - so a limit that lived there would
/// have to be re-argued for every provider and would simply be absent behind a stub.
/// </para>
/// </remarks>
public static class DocumentProfileParser
{
    /// <summary>The fields design Appendix A declares, in its order.</summary>
    /// <remarks>
    /// Public because the prompt has to ask for exactly what the parser will accept, and two lists
    /// that must agree should be one list. The prompt in <c>TriageExecutor</c> is built from this.
    /// </remarks>
    public static readonly IReadOnlyList<string> ContractFields =
        ["DocType", "JurisdictionIso", "DocRef", "Version", "LayoutFamily", "Confidence"];

    /// <summary>
    /// The longest reply this parser will read at all, in characters (256 Ki).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A provider's output-token cap is not this bound, and treating it as one is the mistake worth
    /// naming.</b> <c>MaxOutputTokens</c> is a setting on one adapter, honoured by one vendor, and the
    /// whole point of the agnosticism constraint is that the adapter is replaceable - so a boundary
    /// that relied on it would have to be re-argued for every provider, and would be silently absent
    /// the first time one ignored the setting, streamed, or was swapped for a stub. A boundary check
    /// belongs at the boundary, where it holds regardless of who is upstream.
    /// </para>
    /// <para>
    /// <b>Measured:</b> a 40 MB reply drove roughly 214 MB of allocation through
    /// <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> before any of this class's rules
    /// were consulted - the string, the UTF-8 transcode and the document's own metadata, none of which
    /// a later refusal un-allocates. Refusing on length costs one integer comparison.
    /// </para>
    /// <para>
    /// 256 Ki is roughly three orders of magnitude above the largest legitimate profile (a few hundred
    /// bytes), so it is a bound against absurdity rather than a limit anything real approaches.
    /// </para>
    /// <para>
    /// <b>An alias of <see cref="ExtractionText.MaxReplyLength"/>, not a second copy of the number.</b>
    /// It stays published here because callers and tests name it, but the value - and the reasoning
    /// above - belong to the one place every parser at this boundary shares.
    /// </para>
    /// </remarks>
    public const int MaxReplyLength = ExtractionText.MaxReplyLength;

    /// <summary>
    /// The longest value this parser will accept in any of the contract's string fields.
    /// </summary>
    /// <remarks>
    /// The fields are a document type, an ISO country code, a document reference, a version
    /// designation and a layout-family name - all copied verbatim out of a document's own front
    /// matter, and none of them longer than a line. 200 characters is generous for every one of them
    /// and far below anything that could be a paragraph, so a value past it is a model that pasted a
    /// page into a field rather than a document with an unusually long title. It also keeps every
    /// value comfortably inside the columns design 6 sizes for published text.
    /// <para>
    /// An alias of <see cref="ExtractionText.MaxFieldLength"/>, for the reason given above on
    /// <see cref="MaxReplyLength"/>.
    /// </para>
    /// </remarks>
    public const int MaxFieldLength = ExtractionText.MaxFieldLength;

    /// <summary>The JSON reader's own settings: no comments, no trailing commas, no depth to speak of.</summary>
    /// <remarks>
    /// All three are refusals in disguise. A comment or a trailing comma in a structured-output reply
    /// means the model produced JavaScript rather than JSON, which is worth knowing rather than
    /// absorbing; and the depth bound means a hostile or looping reply cannot be answered with a
    /// stack overflow.
    /// </remarks>
    private static readonly JsonDocumentOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4,
    };

    /// <summary>Parses a triage reply into a profile, refusing anything that is not one.</summary>
    /// <param name="reply">The agent's verbatim reply text.</param>
    /// <returns>The profile the reply declares.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reply"/> is <see langword="null"/>.</exception>
    /// <exception cref="DocumentProfileFormatException">
    /// The reply is not a JSON object, omits or adds a field, carries a field of the wrong type or a
    /// blank string, or reports a confidence outside the unit interval.
    /// </exception>
    public static DocumentProfile Parse(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        using JsonDocument document = ReadObject(reply);
        JsonElement root = document.RootElement;

        EnsureExactlyTheContractFields(root);

        return new DocumentProfile(
            ReadString(root, "DocType"),
            ReadString(root, "JurisdictionIso"),
            ReadString(root, "DocRef"),
            ReadString(root, "Version"),
            ReadString(root, "LayoutFamily"),
            ReadConfidence(root));
    }

    /// <summary>Parses a reply, reporting a refusal instead of throwing it.</summary>
    /// <param name="reply">The agent's verbatim reply text.</param>
    /// <param name="profile">The parsed profile, or <see langword="null"/> when the reply was refused.</param>
    /// <param name="refusal">The refusal, or <see langword="null"/> when the reply parsed.</param>
    /// <returns><see langword="true"/> when <paramref name="reply"/> was a valid profile.</returns>
    /// <remarks>
    /// The try-shape exists for exactly one caller: the triage executor, for which a refused reply is
    /// an ordinary routing outcome (design 9's "New/unknown layout family" row) rather than an error.
    /// It hands back the exception rather than a bool because the executor has to record <em>which</em>
    /// part of the contract broke on the review-queue row it writes.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reply"/> is <see langword="null"/>.</exception>
    public static bool TryParse(
        string reply,
        out DocumentProfile? profile,
        out DocumentProfileFormatException? refusal)
    {
        ArgumentNullException.ThrowIfNull(reply);

        try
        {
            profile = Parse(reply);
            refusal = null;

            return true;
        }
        catch (DocumentProfileFormatException error)
        {
            profile = null;
            refusal = error;

            return false;
        }
    }

    /// <summary>Reads the reply as a JSON object, unwrapping a code fence but never searching inside prose.</summary>
    private static JsonDocument ReadObject(string reply)
    {
        // FIRST, before Trim, before Unfence, before the parser sees a byte of it. Every step below
        // allocates in proportion to the input, so a length check that ran after any of them would be
        // paying the cost it exists to avoid. See MaxReplyLength for why the provider's own output cap
        // is not a substitute.
        if (reply.Length > MaxReplyLength)
        {
            throw new DocumentProfileFormatException(
                DocumentProfileDefect.ReplyTooLarge,
                $"The triage agent's reply is {reply.Length} characters; this parser reads at most "
                    + $"{MaxReplyLength}. A DocumentProfile is a few hundred bytes, so a reply this size is "
                    + "not one - and parsing it to discover that would cost several times its length in "
                    + "allocation first.");
        }

        string payload = ExtractionText.Unfence(reply.Trim());

        if (payload.Length == 0)
        {
            throw new DocumentProfileFormatException(
                DocumentProfileDefect.NotAJsonObject,
                "The triage agent replied with nothing. A profile cannot be inferred from silence, and "
                    + "guessing one would put the pipeline's authority behind a value no model produced.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, ReaderOptions);
        }
        catch (JsonException error)
        {
            throw new DocumentProfileFormatException(
                DocumentProfileDefect.NotAJsonObject,
                "The triage agent's reply is not JSON. The prompt asks for the DocumentProfile object and "
                    + "nothing else; a reply carrying prose, an apology or a partial answer is a proposal the "
                    + "pipeline declines rather than one it repairs.",
                error);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            JsonValueKind kind = document.RootElement.ValueKind;
            document.Dispose();

            throw new DocumentProfileFormatException(
                DocumentProfileDefect.NotAJsonObject,
                $"The triage agent's reply is JSON of kind '{kind}', not the DocumentProfile object the "
                    + "contract declares.");
        }

        return document;
    }

    /// <summary>
    /// Refuses a reply whose property set is not exactly the contract's, each field exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This method's postcondition is what the rest of the parser relies on:</b> when it returns,
    /// every field in <see cref="ContractFields"/> is present exactly once, so the
    /// <c>GetProperty</c> calls that follow cannot fail. Three checks, and all three are needed to
    /// deliver that - a declared-name check, a distinct-name check, and a <em>positive</em>
    /// per-field presence check.
    /// </para>
    /// <para>
    /// <b>Recorded, because an earlier version of this method was wrong and its comment said
    /// otherwise.</b> It counted properties and checked names against the allowlist, and claimed
    /// duplicates "land here too" because a repeat would push the count past six. That reasoning held
    /// only while nothing else was missing. A reply that DUPLICATES one declared field and OMITS
    /// another - six properties, every name declared - satisfied both checks, and the omitted field's
    /// <c>GetProperty</c> then threw <see cref="KeyNotFoundException"/>: not a
    /// <see cref="DocumentProfileFormatException"/>, so <see cref="TryParse"/>'s catch did not see it
    /// and it escaped through the triage executor and killed the run. The boundary neither accepted
    /// nor refused the reply - it threw - and the document that most needed a human reached nobody.
    /// A model's reply is attacker-influenced text (prompt injection through a supplied document
    /// reaches it), so that shape is reachable on purpose and not only by accident.
    /// </para>
    /// <para>
    /// The fix is structural rather than an added special case: presence is now established by asking
    /// for each contract field, never inferred from a count. A count is a proxy for the property that
    /// actually matters, and proxies fail exactly where two errors cancel.
    /// </para>
    /// </remarks>
    private static void EnsureExactlyTheContractFields(JsonElement root)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!Declares(property.Name))
            {
                throw new DocumentProfileFormatException(
                    DocumentProfileDefect.UndeclaredField,
                    $"The triage reply carries a field the DocumentProfile contract does not declare: "
                        + $"'{ExtractionText.ForMessage(property.Name)}'. The contract is "
                        + $"({string.Join(", ", ContractFields)}); an unexpected field means the model answered "
                        + "a different question from the one that was asked, and dropping it silently would hide "
                        + "the drift until something needed the answer that was really meant.");
            }

            if (!seen.Add(property.Name))
            {
                // Which of two values for one field is "the" value is not a question a parser may
                // decide - and System.Text.Json's own last-one-wins would have decided it silently.
                throw new DocumentProfileFormatException(
                    DocumentProfileDefect.DuplicateField,
                    $"The triage reply gives '{ExtractionText.ForMessage(property.Name)}' more than once. Which of two "
                        + "values is the answer is not a question a parser may decide, and taking either "
                        + "would publish a value on the strength of document order.");
            }
        }

        // POSITIVE presence, asked field by field. Not `seen.Count == ContractFields.Count`: that is
        // the count check whose failure this method's remarks record, and it is only sound because the
        // duplicate check above already ran. Asking directly is sound on its own, and being sound on
        // its own is the property worth having in a boundary check.
        List<string> missing = [.. ContractFields.Where(field => !seen.Contains(field))];

        if (missing.Count > 0)
        {
            throw new DocumentProfileFormatException(
                DocumentProfileDefect.MissingField,
                $"The triage reply omits {string.Join(", ", missing)}. Every field of the DocumentProfile "
                    + "contract is a value, not an option: defaulting one would record data no model produced.");
        }
    }

    private static bool Declares(string name)
    {
        // Ordinal, case-SENSITIVE. 'docType' is not 'DocType': a model that changed the casing changed
        // what it thinks the schema is, and accepting both spellings would make the contract's shape a
        // matter of taste rather than agreement.
        for (int index = 0; index < ContractFields.Count; index++)
        {
            if (string.Equals(ContractFields[index], name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadString(JsonElement root, string field)
    {
        JsonElement value = root.GetProperty(field);

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new DocumentProfileFormatException(
                DocumentProfileDefect.WrongType,
                $"The triage reply gives '{field}' as JSON '{value.ValueKind}', but the DocumentProfile "
                    + "contract declares it as a string. Coercing it would let the model choose the type of a "
                    + "value the pipeline publishes.");
        }

        string text = value.GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DocumentProfileFormatException(
                DocumentProfileDefect.BlankValue,
                $"The triage reply gives '{field}' as a blank string. Blank is neither a value nor an honest "
                    + "'unknown': the prompt asks for the literal UNKNOWN with a lowered confidence when the "
                    + "document does not state something, which routes the document to review instead of "
                    + "travelling as data.");
        }

        // The other end of the same boundary. The value is bound HERE, at the edge, rather than by
        // whichever consumer happens to store or render it first: a cap enforced downstream is a cap
        // each consumer has to remember, and the one that forgets is a log line, a review-queue row or
        // a database column discovering it at run time. Refused rather than truncated - a silently
        // shortened DocRef is a document reference that matches nothing and looks fine.
        if (text.Length > MaxFieldLength)
        {
            throw new DocumentProfileFormatException(
                DocumentProfileDefect.ValueTooLong,
                $"The triage reply gives '{field}' as {text.Length} characters; the contract accepts at most "
                    + $"{MaxFieldLength}. These fields are copied out of a document's front matter and none of "
                    + "them is longer than a line, so a value this size is a model that pasted a page into a "
                    + "field. Truncating it would publish a value the document does not contain.");
        }

        return text;
    }

    private static double ReadConfidence(JsonElement root)
    {
        JsonElement value = root.GetProperty("Confidence");

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double confidence))
        {
            throw new DocumentProfileFormatException(
                DocumentProfileDefect.WrongType,
                $"The triage reply gives 'Confidence' as JSON '{value.ValueKind}', but the DocumentProfile "
                    + "contract declares it as a number. Confidence is what the routing edge compares against "
                    + "its threshold, so a value that is not a number is a document that cannot be routed.");
        }

        if (!double.IsFinite(confidence) || confidence < 0d || confidence > 1d)
        {
            throw new DocumentProfileFormatException(
                DocumentProfileDefect.ConfidenceOutOfRange,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The triage reply reports a confidence of {confidence}, which is outside the unit "
                        + $"interval. Clamping it would manufacture certainty the model never claimed - and "
                        + $"the routing threshold this is compared against decides whether a human ever sees "
                        + $"the document."));
        }

        // Negative zero normalised to zero. It passes every check above - IEEE says -0.0 == 0.0 - and
        // then renders as "-0" in a log line, an exception message and a review-queue row, where it
        // reads as a corrupt value rather than as the "no idea at all" it actually is. One comparison
        // removes a class of confused incident reports; nothing else about the value changes.
        return confidence == 0d ? 0d : confidence;
    }

}
