using Cbix.Core.Ingest;

using Microsoft.Extensions.DependencyInjection;

namespace Cbix.Core.Documents;

/// <summary>
/// The profile sources for the two capabilities Core can satisfy on its own - the ones that need no
/// provider SDK because they present the document from locally parsed and locally rendered material.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the agnosticism fallback, and that is why they live in Core.</b> A provider with no
/// native document mode is read through <see cref="GenericVisionDocumentContentProfile"/>; a provider
/// or deployment with no visual channel at all is read through
/// <see cref="TextOnlyDocumentContentProfile"/>. Neither can be contributed by a provider adapter,
/// because the case they cover is "no provider adapter is present".
/// </para>
/// <para>
/// <c>NativeDocument</c> has no source here on purpose: only an adapter holding a provider client can
/// build that profile, so the host registers it. A host that configures
/// <see cref="DocumentPresentationCapability.NativeDocument"/> without registering such a source gets
/// a named refusal from <see cref="DocumentContentProfileSelector"/> rather than a quiet downgrade.
/// </para>
/// </remarks>
public static class LocalDocumentContentProfileSources
{
    /// <summary>The generic-vision profile: text layer plus locally rendered page images (story S01-06).</summary>
    public sealed class Vision : IDocumentContentProfileSource
    {
        /// <inheritdoc/>
        public DocumentPresentationCapability Capability => DocumentPresentationCapability.Vision;

        /// <inheritdoc/>
        public IDocumentContentProvider Create(IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);

            return new GenericVisionDocumentContentProfile(
                services.GetRequiredService<ITextLayerExtractor>(),
                services.GetRequiredService<IPageImageRenderer>());
        }
    }

    /// <summary>The text-only profile: text layer alone, explicitly degraded (story S01-07).</summary>
    public sealed class TextOnly : IDocumentContentProfileSource
    {
        /// <inheritdoc/>
        public DocumentPresentationCapability Capability => DocumentPresentationCapability.TextOnly;

        /// <inheritdoc/>
        public IDocumentContentProvider Create(IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);

            return new TextOnlyDocumentContentProfile(services.GetRequiredService<ITextLayerExtractor>());
        }
    }
}
