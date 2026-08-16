using Cbix.Core.Documents;
using Cbix.Core.Ingest;

namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/TextOnlyDocumentContentProfile.feature</c> (story S01-07).
/// A dumb typed carrier, as elsewhere.
/// </summary>
public sealed class TextOnlyProfileState
{
    /// <summary>Gets or sets the registry reference the ingest service minted for the specimen.</summary>
    public DocumentReference? Document { get; set; }

    /// <summary>Gets or sets the text layer ingest extracted for the specimen, which the profile must reproduce.</summary>
    public TextLayer? TextLayer { get; set; }

    /// <summary>Gets or sets the profile under test, held through the port so the scenario uses it as a caller would.</summary>
    public IDocumentContentProvider? Profile { get; set; }

    /// <summary>Gets or sets the prepared content the When step produced.</summary>
    public DocumentContent? Content { get; set; }
}
