using Cbix.Core.Ingest;

namespace Cbix.Core.Documents;

/// <summary>
/// Rasterises every page of a registered document into an image, locally and offline (design 5.1).
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="ITextLayerExtractor"/>, and a port for the same two reasons. First,
/// testability: the profile that consumes this has behaviour of its own - memoisation, handle
/// issuing, failure classification - and asserting any of it should not require a PDF fixture and a
/// native rendering engine. Second, call counting: "one render per document, however many agents
/// ask" is a property about how often a collaborator was invoked, and a property about invocation
/// count can only be asserted against a seam.
/// </para>
/// <para>
/// <b>This port is not part of the provider-agnosticism boundary either.</b> Rasterisation is local,
/// offline and identical whichever model the images are eventually shown to;
/// <see cref="IDocumentContentProvider"/> exists because <em>presentation</em> differs between
/// providers, and this is one of the local inputs a presentation profile is built from.
/// </para>
/// <para>
/// <b>Implementations render every page, or throw.</b> There is no partial result: a caller that
/// received five images for a seven-page document, with no way to tell, would present the model
/// with a truncated document and report full visual fidelity for it.
/// </para>
/// <para>
/// <b>Implementations bound their own resource use against the document, before rendering.</b> Page
/// geometry and page count are supplied by whoever wrote the file, and both drive allocation
/// directly - a 434-byte PDF can declare a page that rasterises to gigabytes. An implementation
/// that renders whatever it is handed is a denial-of-service surface regardless of how careful its
/// caller was, so the ceilings belong here rather than in a caller that cannot see the geometry. See
/// <see cref="PageImageRenderOptions"/>.
/// </para>
/// <para>
/// <b>Two failure families, kept apart.</b> "This document cannot be rendered" and "the renderer
/// failed" are different events wanting different human responses, and collapsing them tells an
/// operator a supplier sent a bad file when in fact a native library faulted on untrusted input.
/// See <see cref="DocumentNotIngestibleException"/> and <see cref="PageRenderFaultException"/>
/// below.
/// </para>
/// </remarks>
public interface IPageImageRenderer
{
    /// <summary>Renders every page of <paramref name="document"/> to an image.</summary>
    /// <param name="document">
    /// The document to render, as the registry recorded it. Implementations open
    /// <see cref="DocumentReference.Location"/>'s local path.
    /// <para>
    /// <b>Implementations do not re-check containment and must not be given a reference that has not
    /// been through it</b> - the same rule, for the same reason, as
    /// <see cref="ITextLayerExtractor.ExtractAsync"/>. A reference minted by
    /// <see cref="DocumentIngestService"/> is resolved, contained and round-trip stable by
    /// construction; one built by hand carries only what its caller put in it.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">Token that cancels the rendering.</param>
    /// <returns>
    /// One image per page, ordered by <see cref="PageImage.LogicalPageNumber"/> ascending, starting
    /// at <see cref="TextLayer.FirstLogicalPageNumber"/> and covering every page with no gaps.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="DocumentNotIngestibleException">
    /// The document is not one images can be produced from. Reported through the shared ingest
    /// refusal type rather than letting the rendering engine's own exception hierarchy escape, so
    /// that a caller handles one refusal family and swapping the rasteriser is not a breaking change
    /// to everyone who catches it. <see cref="DocumentNotIngestibleReason.Unreadable"/> covers a
    /// damaged, non-PDF or unopenable file; <see cref="DocumentNotIngestibleReason.TooLarge"/> covers
    /// a file past the configured byte bound <em>and</em> a document past the page-count or per-page
    /// megapixel ceilings - refusals of the same kind, deterministic in the document and reached
    /// before any rendering is attempted.
    /// </exception>
    /// <exception cref="PageRenderFaultException">
    /// The rasteriser itself failed. Deliberately distinct from the refusal above: it says nothing
    /// about the document's validity and must not be recorded as though it did. Not transient
    /// either - a caller routes to review rather than spending retries against a native library that
    /// has just faulted.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no file at the document's location.</exception>
    /// <exception cref="IOException">
    /// The document could not be read: the share dropped, the handle died, the file system failed.
    /// <b>Implementations must let this propagate and must not rewrite it as a refusal</b>, for the
    /// reason spelled out on <see cref="ITextLayerExtractor"/>: an infrastructure fault is not a
    /// statement about the document, and a refusal is terminal for the run.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// The render exhausted memory. <b>Implementations must let this propagate too.</b> A page whose
    /// declared dimensions are enormous rasterises into an unbounded buffer, so this is the
    /// observable symptom of a resource-exhaustion attack rather than a data-quality outcome.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<PageImage>> RenderAsync(DocumentReference document, CancellationToken cancellationToken = default);
}
