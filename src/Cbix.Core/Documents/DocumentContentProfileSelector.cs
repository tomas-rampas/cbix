namespace Cbix.Core.Documents;

/// <summary>
/// Picks the active <see cref="IDocumentContentProvider"/> profile from the configured provider's
/// capability, and from nothing else (design 5.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the mechanism that makes a provider swap a configuration change.</b> Every registered
/// <see cref="IDocumentContentProfileSource"/> declares which capability it implements; the
/// configured <see cref="DocumentPresentationOptions.Capability"/> selects among them. No call site
/// names a profile type, no <c>if</c> anywhere asks which provider is in play, and adding a fourth
/// profile is a registration rather than an edit to this class.
/// </para>
/// <para>
/// <b>Both failure modes are refusals, deliberately.</b> An unimplemented capability could have
/// fallen back to the text-only profile, and a duplicate registration could have taken the first
/// match - both would keep a misconfigured deployment running, and both would show up only as
/// degraded extraction quality weeks later. Design 5.1's whole argument is that document
/// presentation is where providers differ most sharply; silently presenting a document differently
/// from what was configured is the one outcome that must not be available.
/// </para>
/// </remarks>
public sealed class DocumentContentProfileSelector
{
    private readonly Dictionary<DocumentPresentationCapability, IDocumentContentProfileSource> _sources;
    private readonly DocumentPresentationCapability _configured;

    /// <summary>Initialises a new <see cref="DocumentContentProfileSelector"/>.</summary>
    /// <param name="sources">Every profile source registered in the container.</param>
    /// <param name="options">The configured provider's presentation capability.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Two sources claim the same capability. Indexing them is what surfaces it, and it is surfaced
    /// at composition rather than at first use so a duplicated registration fails the host's startup
    /// instead of one run in the middle of a batch.
    /// </exception>
    public DocumentContentProfileSelector(
        IEnumerable<IDocumentContentProfileSource> sources,
        DocumentPresentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        _sources = [];
        _configured = options.Capability;

        foreach (IDocumentContentProfileSource source in sources)
        {
            if (!_sources.TryAdd(source.Capability, source))
            {
                throw new InvalidOperationException(
                    $"Two document-content profile sources claim capability '{source.Capability}': "
                        + $"'{_sources[source.Capability].GetType().Name}' and '{source.GetType().Name}'. "
                        + "Which one presented a document would decide whether the permission matrix was "
                        + "read visually, so the ambiguity is refused rather than resolved by registration "
                        + "order.");
            }
        }
    }

    /// <summary>Gets the capability the composed graph will present documents with.</summary>
    /// <remarks>
    /// Exposed so a startup log line and the extraction-run record can state it. Reading it is not a
    /// licence to branch on it: a consumer that needs to know whether the presentation is degraded
    /// reads <see cref="DocumentContentCapabilities.IsDegraded"/> on the content it was handed, which
    /// is the profile's own answer rather than an inference from configuration.
    /// </remarks>
    public DocumentPresentationCapability ConfiguredCapability => _configured;

    /// <summary>Creates the active profile for one workflow run.</summary>
    /// <param name="services">The run scope's service provider.</param>
    /// <returns>The profile as the neutral port.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// No registered source implements the configured capability. The message names what was
    /// configured and what is available, because the usual cause is a host that configured a
    /// capability whose adapter it never registered - a wiring mistake, not a runtime condition.
    /// </exception>
    public IDocumentContentProvider Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!_sources.TryGetValue(_configured, out IDocumentContentProfileSource? source))
        {
            throw new InvalidOperationException(
                $"No document-content profile is registered for the configured presentation capability "
                    + $"'{_configured}'. Registered capabilities: "
                    + $"{(_sources.Count == 0 ? "(none)" : string.Join(", ", _sources.Keys.Order()))}. "
                    + "The host registers the profile source for the provider it selected; falling back to "
                    + "another profile would silently change how the document is presented to the model.");
        }

        return source.Create(services);
    }
}
