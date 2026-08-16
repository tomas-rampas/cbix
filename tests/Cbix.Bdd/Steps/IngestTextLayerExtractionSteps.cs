using Cbix.Bdd.Support;
using Cbix.Core.Ingest;

using Microsoft.Extensions.Logging.Abstractions;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/IngestTextLayerExtraction.feature</c> (story S01-11).
/// <para>
/// These run the real <see cref="DocumentIngestService"/> and the real
/// <see cref="PdfPigTextLayerExtractor"/> against the real DE specimen on disk, read-only. Nothing
/// about the extraction is simulated: the scenario's claim is that PDFPig produces a text string for
/// every page of <em>that document</em>, and a stub returning two invented pages would satisfy the
/// Gherkin while proving nothing at all.
/// </para>
/// </summary>
[Binding]
public sealed class IngestTextLayerExtractionSteps(IngestTextLayerExtractionState state)
{
    /// <summary>The specimen named by the story's Gherkin, relative to the repository's data directory.</summary>
    private const string SpecimenFileName = "Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf";

    /// <summary>
    /// Pages the DE specimen has, measured from the file itself rather than assumed here.
    /// </summary>
    /// <remarks>
    /// The scenario says "every page in the document", and a test that derived the expected count
    /// from the extractor's own answer would assert nothing. The count is therefore stated
    /// independently, and cross-checked below against what PDFPig reports the document contains.
    /// </remarks>
    private const int SpecimenPageCount = 2;

    [Given("the registered DE specimen")]
    public async Task GivenTheRegisteredDeSpecimen()
    {
        ArrangeService();

        state.FirstResult = await IngestSpecimenAsync();

        Assert.True(state.FirstResult.IsNewRegistration, "The specimen should have been registered by this submission.");
    }

    [Given("the DE specimen was already registered with its text layer extracted")]
    public async Task GivenTheDeSpecimenWasAlreadyRegisteredWithItsTextLayerExtracted()
    {
        await GivenTheRegisteredDeSpecimen();

        Assert.NotNull(state.FirstResult!.TextLayer);
        Assert.Equal(1, RequireExtractor().CallCount);
    }

    [When("the ingest executor extracts the text layer")]
    public void WhenTheIngestExecutorExtractsTheTextLayer()
    {
        // Extraction is not a separate call a caller makes: ingest prepares every document it
        // registers (design 5.1), so the Given step above has already run it. This step exists to
        // keep the Gherkin readable and asserts the step it names actually happened.
        Assert.Equal(1, RequireExtractor().CallCount);
    }

    /// <summary>Re-submits the specimen.</summary>
    /// <remarks>
    /// Worded "for ingest" rather than matching S01-10's identically-meant step on purpose. Reqnroll
    /// resolves step text across the whole assembly, so two features phrasing the same action the
    /// same way is an ambiguous-binding failure in both - and the fix is not to share a binding,
    /// because these two scenarios need different services in their state.
    /// </remarks>
    [When("the same file is submitted for ingest again unchanged")]
    public async Task WhenTheSameFileIsSubmittedForIngestAgainUnchanged()
    {
        state.DuplicateResult = await IngestSpecimenAsync();
    }

    [Then("a text string is produced for every page in the document")]
    public void ThenATextStringIsProducedForEveryPageInTheDocument()
    {
        TextLayer layer = RequireTextLayer();

        Assert.Equal(SpecimenPageCount, layer.PageCount);
        Assert.Equal(SpecimenPageCount, layer.Pages.Count);

        // Every page, not merely the right number of them: a null or absent page would leave the
        // grounding gate with nothing to check a snippet from that page against.
        Assert.All(layer.Pages, text => Assert.NotNull(text));
        Assert.All(layer.Pages, text => Assert.NotEmpty(text));
    }

    [Then("the per-page text is retrievable by logical page number")]
    public void ThenThePerPageTextIsRetrievableByLogicalPageNumber()
    {
        TextLayer layer = RequireTextLayer();

        for (int page = TextLayer.FirstLogicalPageNumber; page <= SpecimenPageCount; page++)
        {
            Assert.True(layer.TryGetPage(page, out string? text), $"Logical page {page} could not be retrieved.");
            Assert.Same(layer.Pages[page - TextLayer.FirstLogicalPageNumber], text);
        }

        // The numbering is the one a reader sees, which is the numbering the extraction prompts
        // mandate and therefore the numbering an agent will report. Page 0 is not a page, and the
        // page after the last one is not either - both have to be misses rather than silent
        // wrap-arounds, or a snippet cited against a page that does not exist would be grounded
        // against a page that does.
        Assert.False(layer.TryGetPage(TextLayer.FirstLogicalPageNumber - 1, out _));
        Assert.False(layer.TryGetPage(SpecimenPageCount + 1, out _));

        // Page identity, proved against the specimen's own printed page numbers rather than against
        // the extractor's ordering: the footer of each page names it.
        Assert.Contains("Page 1", layer.GetPage(1), StringComparison.Ordinal);
        Assert.Contains("Page 2", layer.GetPage(2), StringComparison.Ordinal);

        // And the text layer is faithful enough to be the grounding corpus it exists to be: a phrase
        // copied verbatim off page 1 is found on page 1, non-ASCII intact.
        Assert.Contains(
            "BaFin - Bundesanstalt f\u00fcr Finanzdienstleistungsaufsicht",
            layer.GetPage(1),
            StringComparison.Ordinal);
    }

    [Then("no second text layer is extracted")]
    public void ThenNoSecondTextLayerIsExtracted()
    {
        Assert.False(RequireDuplicateResult().IsNewRegistration, "The duplicate submission was treated as a new registration.");

        // One call for two submissions. This is the ordering the design requires: the registry
        // short-circuits before document preparation, so a re-submission never re-pays for it.
        Assert.Equal(1, RequireExtractor().CallCount);
    }

    [Then("the duplicate result carries no text layer")]
    public void ThenTheDuplicateResultCarriesNoTextLayer()
    {
        Assert.Null(RequireDuplicateResult().TextLayer);

        // The first result keeps its own: the duplicate does not disturb the run that did the work.
        Assert.NotNull(RequireFirstResult().TextLayer);
    }

    /// <summary>
    /// Walks up from the test assembly's location to the directory holding <c>Cbix.sln</c>.
    /// </summary>
    /// <remarks>
    /// The specimen is repository data read in place, not a build artefact copied to the output
    /// directory: copying it would duplicate a binary fixture per test project and let the copies
    /// drift from the golden set that describes them.
    /// </remarks>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cbix.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"No directory containing 'Cbix.sln' was found above '{AppContext.BaseDirectory}'.");

        return directory!.FullName;
    }

    private void ArrangeService()
    {
        string ingestRoot = Path.Combine(FindRepositoryRoot(), "data");
        string specimenPath = Path.Combine(ingestRoot, SpecimenFileName);

        Assert.True(File.Exists(specimenPath), $"The DE specimen is missing from the repository at '{specimenPath}'.");

        // One options instance for both, as the composition root will wire it: the extractor
        // re-applies the gate's own size bound to the handle it reopens, and sharing the object is
        // what stops those two bounds drifting apart.
        DocumentIngestOptions options = new(ingestRoot, DocumentIngestOptions.ClaudeFilesApiLimitBytes);

        state.SpecimenPath = specimenPath;
        state.Extractor = new CountingTextLayerExtractor(
            new PdfPigTextLayerExtractor(options, NullLogger<PdfPigTextLayerExtractor>.Instance));

        state.Service = new DocumentIngestService(
            state.Registry,
            state.AuditLog,
            options,
            state.Extractor,
            NullLogger<DocumentIngestService>.Instance);

        Assert.Empty(state.Registry.Entries);
        Assert.Equal(0, state.Extractor.CallCount);
    }

    private async Task<DocumentIngestResult> IngestSpecimenAsync()
    {
        DocumentIngestService service = state.Service
            ?? throw new InvalidOperationException("The Given step did not construct the ingest service.");
        string specimenPath = state.SpecimenPath
            ?? throw new InvalidOperationException("The Given step did not resolve the specimen path.");

        return await service.IngestAsync(specimenPath);
    }

    private CountingTextLayerExtractor RequireExtractor() =>
        state.Extractor ?? throw new InvalidOperationException("The Given step did not construct the text layer extractor.");

    private DocumentIngestResult RequireFirstResult() =>
        state.FirstResult ?? throw new InvalidOperationException("No first submission was made by an earlier step.");

    private DocumentIngestResult RequireDuplicateResult() =>
        state.DuplicateResult ?? throw new InvalidOperationException("No duplicate submission was made by an earlier step.");

    private TextLayer RequireTextLayer() =>
        RequireFirstResult().TextLayer
            ?? throw new InvalidOperationException("The registered submission did not carry a text layer.");
}
