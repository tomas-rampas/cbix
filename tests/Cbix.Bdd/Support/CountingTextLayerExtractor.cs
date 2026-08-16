using Cbix.Core.Documents;
using Cbix.Core.Ingest;

namespace Cbix.Bdd.Support;

/// <summary>
/// Counts calls to an <see cref="ITextLayerExtractor"/> and otherwise delegates to it unchanged.
/// </summary>
/// <remarks>
/// A decorator rather than a stub, deliberately. The scenario it serves asserts two different kinds
/// of thing at once - what the real PDFPig extraction produces from the real specimen, and how many
/// times ingest asked for it - and replacing the extractor would answer only the second while
/// quietly abandoning the first.
/// </remarks>
public sealed class CountingTextLayerExtractor : ITextLayerExtractor
{
    private readonly ITextLayerExtractor _inner;

    /// <summary>Initialises a new <see cref="CountingTextLayerExtractor"/>.</summary>
    /// <param name="inner">The extractor to count calls to and delegate to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    public CountingTextLayerExtractor(ITextLayerExtractor inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
    }

    /// <summary>Gets the number of times extraction has been asked for.</summary>
    public int CallCount { get; private set; }

    /// <inheritdoc />
    public Task<TextLayer> ExtractAsync(DocumentReference document, CancellationToken cancellationToken = default)
    {
        CallCount++;

        return _inner.ExtractAsync(document, cancellationToken);
    }
}
