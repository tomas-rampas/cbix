using System.Reflection;

using Cbix.Bdd.Support;
using Cbix.Core.Documents;
using Cbix.Core.Ingest;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/GenericVisionDocumentContentProfile.feature</c> (story S01-06).
/// <para>
/// The profile is constructed reflectively and then used through the port; see
/// <see cref="LocalDocumentProfileFixture"/> for why. Everything it is asked to do is done for
/// real - the production PDFPig extractor, the production page renderer, the real DE specimen - so
/// that "one rendered image per page" is a statement about pixels PDFium actually produced from
/// that document rather than about a stub's return value.
/// </para>
/// </summary>
[Binding]
public sealed class GenericVisionProfileSteps(GenericVisionProfileState state)
{
    /// <summary>The eight-byte PNG signature (RFC 2083 §3.1), used to prove the bytes are a real image and not a placeholder.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Assemblies a type reachable from this profile may come from. An allowlist, not a search for
    /// the string "Anthropic": a denylist only catches the provider somebody already thought of,
    /// whereas this fails closed, so a Groq, OpenAI or Bedrock type introduced into the profile
    /// breaks the scenario without anyone remembering to extend it.
    /// </summary>
    private static readonly string[] AllowedAssemblies =
    [
        "Microsoft.Extensions.AI.Abstractions",
        "Cbix.Core",
        "mscorlib",
        "netstandard",
    ];

    [Given("the DE specimen and its PDFPig-extracted text layer")]
    public async Task GivenTheDeSpecimenAndItsPdfPigExtractedTextLayer()
    {
        (DocumentReference document, TextLayer textLayer) = await LocalDocumentProfileFixture.RegisterSpecimenAsync();

        state.Document = document;
        state.TextLayer = textLayer;

        Assert.Equal(LocalDocumentProfileFixture.SpecimenPageCount, textLayer.PageCount);

        DocumentIngestOptions ingestOptions = LocalDocumentProfileFixture.CreateIngestOptions();

        // The same options instance the extractor gets, as the composition root will wire it: the
        // renderer re-applies ingest's own size bound to the handle it reopens, and sharing the
        // object is what stops those two bounds drifting apart.
        // The production default, read off the type rather than repeated here: the scenario asserts
        // pages are legible at the density the pipeline actually renders at, and a number copied
        // into the test would keep passing after the default moved.
        int dpi = LocalDocumentProfileFixture.CoreConstant<int>("PageImageRenderOptions", "DefaultDpi");
        int maxPageCount = LocalDocumentProfileFixture.CoreConstant<int>("PageImageRenderOptions", "DefaultMaxPageCount");
        int maxPageMegapixels = LocalDocumentProfileFixture.CoreConstant<int>("PageImageRenderOptions", "DefaultMaxPageMegapixels");
        int maxTotalMegapixels = LocalDocumentProfileFixture.CoreConstant<int>("PageImageRenderOptions", "DefaultMaxTotalMegapixels");

        // Every parameter passed explicitly, defaults and all: Activator.CreateInstance does not fill
        // optional parameters, so a constructor that grows silently breaks this step rather than
        // silently picking defaults. The values still come off the type, so the scenario exercises
        // the production configuration.
        object renderOptions = LocalDocumentProfileFixture.CreateCoreInstance(
            "PageImageRenderOptions",
            dpi,
            maxPageCount,
            maxPageMegapixels,
            maxTotalMegapixels);
        object renderer = LocalDocumentProfileFixture.CreateCoreInstance(
            "PdfiumPageImageRenderer",
            ingestOptions,
            renderOptions,
            LocalDocumentProfileFixture.NullLoggerFor("PdfiumPageImageRenderer"));

        ITextLayerExtractor extractor = new PdfPigTextLayerExtractor(
            ingestOptions,
            NullLogger<PdfPigTextLayerExtractor>.Instance);

        state.Profile = (IDocumentContentProvider)LocalDocumentProfileFixture.CreateCoreInstance(
            "GenericVisionDocumentContentProfile",
            extractor,
            renderer,
            // Null means the profile's own default deadline, which is what production gets. Passed
            // rather than omitted for the reason above: Activator does not fill optional parameters.
            null);
    }

    [When("the generic-vision profile prepares document content")]
    public async Task WhenTheGenericVisionProfilePreparesDocumentContent()
    {
        IDocumentContentProvider profile = state.Profile
            ?? throw new InvalidOperationException("The Given step did not construct the profile.");
        DocumentReference document = state.Document
            ?? throw new InvalidOperationException("The Given step did not register the specimen.");

        state.Content = await profile.PrepareAsync(document);
    }

    [Then("the content includes the extracted text")]
    public void ThenTheContentIncludesTheExtractedText()
    {
        DocumentContent content = RequireContent();
        TextLayer textLayer = RequireTextLayer();

        List<string> texts = [.. content.Content.OfType<TextContent>().Select(block => block.Text)];

        Assert.NotEmpty(texts);

        // Exact equality against the extractor's own output for every page, not containment of some
        // remembered phrase. The text this profile sends is the text the grounding gate will do
        // verbatim containment against in Sprint 02, so anything the profile trims, re-wraps or
        // re-encodes here becomes a snippet the gate rejects even though the model copied it
        // correctly. "Includes the extracted text" therefore has to mean the extracted text, not a
        // tidied rendering of it.
        for (int page = TextLayer.FirstLogicalPageNumber; page <= textLayer.PageCount; page++)
        {
            string expected = textLayer.GetPage(page);

            Assert.True(
                texts.Contains(expected, StringComparer.Ordinal),
                $"No text block carries logical page {page} verbatim as the extractor produced it.");
        }

        // And the page it belongs to is stated, because the extraction prompts require agents to
        // report logical page numbers and a model cannot report what it was never told. The marker
        // is a block of its own rather than a prefix on the page text - see the profile's remarks:
        // prefixing would put text into the block the model is instructed to copy from verbatim,
        // and a snippet contaminated with a marker fails grounding on a page it was read from
        // correctly.
        for (int page = TextLayer.FirstLogicalPageNumber; page <= textLayer.PageCount; page++)
        {
            Assert.Contains(
                texts,
                text => text.Contains($"page {page}", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(text, textLayer.GetPage(page), StringComparison.Ordinal));
        }
    }

    [Then("the content includes one rendered image per page")]
    public void ThenTheContentIncludesOneRenderedImagePerPage()
    {
        DocumentContent content = RequireContent();

        List<DataContent> images =
        [
            .. content.Content.OfType<DataContent>().Where(block => block.HasTopLevelMediaType("image")),
        ];

        // The count comes from the specimen, not from the renderer: see SpecimenPageCount.
        Assert.Equal(LocalDocumentProfileFixture.SpecimenPageCount, images.Count);

        foreach (DataContent image in images)
        {
            Assert.StartsWith("image/", image.MediaType, StringComparison.Ordinal);

            // Real pixels, not a placeholder: the signature proves the decoder produced an image,
            // and the size floor rules out a blank or one-pixel render passing as a page. The
            // matrix agent has to read 8-10pt table cells off these in Sprint 02.
            Assert.True(image.Data.Length > 4096, $"A rendered page image was only {image.Data.Length} bytes.");
            Assert.True(
                image.Data.Span.StartsWith(PngSignature),
                "A rendered page image does not begin with the PNG signature.");
        }

        // Page order, and each image adjacent to the text of the page it renders. A caller cannot
        // re-associate an image with its page after the fact - the blocks carry no page number of
        // their own - so the ordering is the association.
        Assert.True(
            content.Capabilities.IncludesVisualContent,
            "A profile that sends page images must report that it presents the document visually.");

        List<int> imagePositions = [];
        for (int index = 0; index < content.Content.Count; index++)
        {
            if (content.Content[index] is DataContent candidate && candidate.HasTopLevelMediaType("image"))
            {
                imagePositions.Add(index);
            }
        }

        Assert.Equal(images.Count, imagePositions.Count);
        for (int index = 1; index < imagePositions.Count; index++)
        {
            Assert.True(imagePositions[index] > imagePositions[index - 1], "Page images are not in document order.");
        }
    }

    [Then("no Files API or Anthropic-specific type is referenced anywhere in this profile")]
    public void ThenNoFilesApiOrAnthropicSpecificTypeIsReferencedAnywhereInThisProfile()
    {
        DocumentContent content = RequireContent();
        IDocumentContentProvider profile = state.Profile
            ?? throw new InvalidOperationException("The Given step did not construct the profile.");

        // 1. The assembly the profile lives in. Cbix.Core's own reference table is asserted by
        //    CoreAssemblyNeutralityTests; repeating it here would add nothing. What this adds is the
        //    profile's own surface: its constructor parameters and public members are how a provider
        //    type would enter through the front door.
        Type profileType = profile.GetType();

        List<Type> surface = [];
        foreach (ConstructorInfo constructor in profileType.GetConstructors())
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                surface.Add(parameter.ParameterType);
            }
        }

        foreach (MethodInfo method in profileType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            surface.Add(method.ReturnType);
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                surface.Add(parameter.ParameterType);
            }
        }

        List<string> offenders = [];
        foreach (Type type in surface)
        {
            foreach (Type reachable in Flatten(type))
            {
                string assembly = reachable.Assembly.GetName().Name ?? string.Empty;
                if (!IsAllowedAssembly(assembly))
                {
                    offenders.Add($"{reachable.FullName} (assembly {assembly})");
                }
            }
        }

        // 2. The blocks it actually produced. A provider payload does not have to appear in a
        //    signature to be present - AIContent.RawRepresentation is exactly the smuggling route the
        //    Claude profile uses legitimately, so the fallback profile has to be shown not to use it.
        foreach (AIContent block in content.Content)
        {
            string blockAssembly = block.GetType().Assembly.GetName().Name ?? string.Empty;
            if (!IsAllowedAssembly(blockAssembly))
            {
                offenders.Add($"{block.GetType().FullName} (assembly {blockAssembly})");
            }

            Assert.True(
                block.RawRepresentation is null,
                $"A content block carries a provider payload in RawRepresentation ({block.RawRepresentation?.GetType().FullName}); "
                    + "the generic-vision profile is the fallback that must work with no provider SDK at all.");
        }

        Assert.True(
            offenders.Count == 0,
            $"The generic-vision profile reaches types outside the allowed assemblies: {string.Join(", ", offenders)}.");

        // 3. Nothing was uploaded anywhere. A Files API reference would surface as a content block
        //    naming a remote resource; every block this profile emits carries its own bytes.
        foreach (DataContent data in content.Content.OfType<DataContent>())
        {
            Assert.True(
                data.Uri.StartsWith("data:", StringComparison.Ordinal),
                $"A content block points at the remote resource '{data.Uri}'; the fallback profile sends local bytes only.");
        }

        // 4. The handle promises no remote artefact either, so a resume re-prepares locally rather
        //    than trying to redeem a token against an API this profile never called.
        Assert.Null(content.Handle.ProviderToken);
    }

    /// <summary>Adds a type and everything structurally reachable from it through generic arguments and element types.</summary>
    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is Type element)
        {
            foreach (Type nested in Flatten(element))
            {
                yield return nested;
            }
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            foreach (Type nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }

    private static bool IsAllowedAssembly(string assemblyName)
    {
        if (assemblyName.StartsWith("System.", StringComparison.Ordinal)
            || string.Equals(assemblyName, "System", StringComparison.Ordinal))
        {
            return true;
        }

        return Array.Exists(AllowedAssemblies, allowed => string.Equals(allowed, assemblyName, StringComparison.Ordinal));
    }

    private DocumentContent RequireContent() =>
        state.Content ?? throw new InvalidOperationException("The When step did not prepare any content.");

    private TextLayer RequireTextLayer() =>
        state.TextLayer ?? throw new InvalidOperationException("The Given step did not capture the text layer.");
}
