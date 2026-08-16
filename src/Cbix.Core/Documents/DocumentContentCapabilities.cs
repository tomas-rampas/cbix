namespace Cbix.Core.Documents;

/// <summary>
/// Describes how faithfully a <see cref="IDocumentContentProvider"/> was able to present a
/// document to the model, so that downstream code and the eval harness can tell a full-fidelity
/// run from a degraded one instead of assuming they are equivalent.
/// </summary>
/// <remarks>
/// <para>
/// The permission matrix is a visual table: reading it correctly depends on the model seeing
/// page images, not just extracted text. A run whose provider could only supply text is still
/// a run - it is not silently an equal one, and this descriptor is what makes that difference
/// legible (design 5.1, 11).
/// </para>
/// <para>
/// One invariant is enforced here rather than left to convention: content without visual
/// representation is degraded by definition. A profile cannot report
/// <see cref="IncludesVisualContent"/> as <see langword="false"/> while claiming full fidelity.
/// </para>
/// <para>
/// <b>Recorded assumption behind that invariant.</b> "Degraded" is defined relative to the
/// pipeline's primary corpus: visually laid-out PDF country manuals, whose permission matrix is a
/// table that must be seen to be read correctly. It is an assumption about the corpus, not a
/// universal truth about documents - a text-native input (HTML, plain text) would carry no
/// information in its layout, and admitting one would mean revisiting this invariant rather than
/// working around it.
/// </para>
/// <para>
/// Members are get-only on purpose: the compiler-generated copy constructor behind a <c>with</c>
/// expression bypasses this constructor's validation, so <c>init</c> setters would open a route
/// around the invariant above.
/// </para>
/// </remarks>
public sealed record DocumentContentCapabilities
{
    /// <summary>Initialises a new <see cref="DocumentContentCapabilities"/>.</summary>
    /// <param name="profileName">
    /// Stable name of the profile that produced the content (for example <c>claude-native-pdf</c>,
    /// <c>generic-vision</c>, <c>text-only</c>). The eval harness reports metrics per profile, so
    /// this value is part of the measurement record and should not change casually.
    /// </param>
    /// <param name="includesVisualContent">
    /// <see langword="true"/> when the content presents the document's pages visually - a native
    /// PDF block or rendered page images - so the model can read table layout; otherwise
    /// <see langword="false"/>.
    /// </param>
    /// <param name="isDegraded">
    /// <see langword="true"/> when the presentation is below the pipeline's full-fidelity
    /// expectation and results should be treated as provisional. Must be <see langword="true"/>
    /// whenever <paramref name="includesVisualContent"/> is <see langword="false"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="profileName"/> is empty or white space, or <paramref name="includesVisualContent"/>
    /// is <see langword="false"/> while <paramref name="isDegraded"/> is <see langword="false"/>.
    /// </exception>
    public DocumentContentCapabilities(string profileName, bool includesVisualContent, bool isDegraded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        if (!includesVisualContent && !isDegraded)
        {
            throw new ArgumentException(
                "Content without visual representation is degraded by definition: a profile that cannot "
                    + "present pages visually must report isDegraded as true.",
                nameof(isDegraded));
        }

        ProfileName = profileName;
        IncludesVisualContent = includesVisualContent;
        IsDegraded = isDegraded;
    }

    /// <summary>Gets the stable name of the profile that produced the content.</summary>
    public string ProfileName { get; }

    /// <summary>
    /// Gets a value indicating whether the content presents the document's pages visually, so the
    /// model can read the layout of the permission matrix rather than only its extracted text.
    /// </summary>
    public bool IncludesVisualContent { get; }

    /// <summary>
    /// Gets a value indicating whether the presentation is below full fidelity and its extraction
    /// results should be treated as provisional.
    /// </summary>
    public bool IsDegraded { get; }
}
