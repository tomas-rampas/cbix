using System.Reflection;

using Cbix.Core.Documents;
using Cbix.Core.Ingest;

using Microsoft.Extensions.Logging.Abstractions;

namespace Cbix.Bdd.Support;

/// <summary>
/// Shared arrangement for the two local document-content profile features (stories S01-06 and
/// S01-07): locating the DE specimen on disk, registering it through the real ingest service, and
/// constructing the profile under test.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the profiles are constructed reflectively.</b> The BDD working agreement requires the red
/// phase to be a <em>failing assertion</em>. A compile-time <c>new GenericVisionDocumentContentProfile(...)</c>
/// against a type that does not exist yet breaks the build of the whole test project instead, which
/// proves nothing and blocks every other scenario. The same reasoning is already recorded on
/// <c>DocumentContentProviderPortSteps</c>; this is that precedent applied to a concrete type rather
/// than to an interface.
/// </para>
/// <para>
/// <b>Nothing else here is reflective, and nothing here is simulated.</b> Once the profile is
/// constructed it is used through <see cref="IDocumentContentProvider"/> like any caller would, and
/// its collaborators are the production PDFPig extractor and the production page renderer running
/// against the real specimen. The scenarios claim things about what those components actually
/// produce from that document; a stub returning invented pages or a one-pixel image would satisfy
/// the Gherkin and prove nothing.
/// </para>
/// </remarks>
public static class LocalDocumentProfileFixture
{
    /// <summary>The specimen named by both stories' Gherkin, relative to the repository's data directory.</summary>
    public const string SpecimenFileName = RepositoryLayout.DeSpecimenFileName;

    /// <summary>
    /// Pages the DE specimen has, stated independently of anything under test.
    /// </summary>
    /// <remarks>
    /// "One rendered image per page" is only an assertion if the page count comes from somewhere
    /// other than the renderer being asserted about. Deriving it from the renderer's own output
    /// would make the step a tautology.
    /// </remarks>
    public const int SpecimenPageCount = 2;

    /// <summary>Gets the ingest options both features share: the repository's data directory as the ingest root.</summary>
    public static DocumentIngestOptions CreateIngestOptions() =>
        new(RepositoryLayout.DataDirectory(), DocumentIngestOptions.ClaudeFilesApiLimitBytes);

    /// <summary>Registers the DE specimen through the real ingest service and returns the reference it minted.</summary>
    /// <remarks>
    /// Going through ingest rather than hand-building a <see cref="DocumentReference"/> is
    /// deliberate: the reference a profile is entitled to assume - resolved, contained, round-trip
    /// stable (see <see cref="DocumentReference.Location"/>) - is a guarantee the registry makes and
    /// a hand-built reference does not. A profile that only ever sees hand-built references is not
    /// being exercised against its actual input.
    /// </remarks>
    public static async Task<(DocumentReference Document, TextLayer TextLayer)> RegisterSpecimenAsync()
    {
        DocumentIngestOptions options = CreateIngestOptions();
        string specimenPath = Path.Combine(options.IngestRoot, SpecimenFileName);

        Assert.True(File.Exists(specimenPath), $"The DE specimen is missing from the repository at '{specimenPath}'.");

        DocumentIngestService service = new(
            new InMemoryDocumentRegistry(),
            new InMemoryIngestAuditLog(),
            options,
            new PdfPigTextLayerExtractor(options, NullLogger<PdfPigTextLayerExtractor>.Instance),
            NullLogger<DocumentIngestService>.Instance);

        DocumentIngestResult result = await service.IngestAsync(specimenPath);

        Assert.True(result.IsNewRegistration, "The specimen should have been registered by this submission.");
        Assert.NotNull(result.TextLayer);

        return (result.Submitted, result.TextLayer!);
    }

    /// <summary>
    /// Resolves a public type by name from <c>Cbix.Core</c>, failing the scenario - rather than the
    /// build - when the story's production type does not exist yet.
    /// </summary>
    public static Type ResolveCoreType(string typeName)
    {
        Assembly core = typeof(IDocumentContentProvider).Assembly;

        Type[] candidates = Array.FindAll(
            core.GetExportedTypes(),
            type => string.Equals(type.Name, typeName, StringComparison.Ordinal));

        Assert.True(
            candidates.Length > 0,
            $"No public type named '{typeName}' exists in assembly '{core.GetName().Name}'.");
        Assert.True(
            candidates.Length == 1,
            $"Assembly '{core.GetName().Name}' declares {candidates.Length} public types named '{typeName}'.");

        return candidates[0];
    }

    /// <summary>
    /// Reads a public constant off a <c>Cbix.Core</c> type by name.
    /// </summary>
    /// <remarks>
    /// Used so a scenario can state the value it exercises - the render density, say - without
    /// hard-coding a number that would then silently disagree with the production default it is
    /// meant to be. It also asserts the constant is public and spelled as the documentation says.
    /// </remarks>
    public static T CoreConstant<T>(string typeName, string constantName)
    {
        Type type = ResolveCoreType(typeName);

        FieldInfo? field = type.GetField(constantName, BindingFlags.Public | BindingFlags.Static);

        Assert.True(field is not null, $"'{type.FullName}' declares no public constant named '{constantName}'.");

        return Assert.IsType<T>(field!.GetValue(null));
    }

    /// <summary>
    /// Builds a <c>NullLogger&lt;T&gt;</c> for a <c>Cbix.Core</c> type named at run time.
    /// </summary>
    /// <remarks>
    /// Generic instantiation by reflection, so that supplying a logger to a reflectively-constructed
    /// collaborator does not require naming that collaborator at compile time - which would undo the
    /// whole reason this fixture resolves types by name (see the class remarks). Scenarios assert on
    /// behaviour, not on log output; the unit tests own the structured-event assertions and use a
    /// capturing logger for them.
    /// </remarks>
    public static object NullLoggerFor(string typeName)
    {
        Type loggerType = typeof(NullLogger<>).MakeGenericType(ResolveCoreType(typeName));

        // Property OR field: NullLogger<T> exposes its singleton as a static FIELD, and a
        // property-only lookup silently finds nothing. Asking for both keeps this working whichever
        // shape the abstractions package uses.
        object? instance =
            loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? loggerType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        Assert.True(instance is not null, $"'{loggerType.FullName}' exposes no static Instance member.");

        return instance!;
    }

    /// <summary>Constructs a public type from <c>Cbix.Core</c> by name, with a scenario-level failure if it cannot be.</summary>
    public static object CreateCoreInstance(string typeName, params object?[] arguments)
    {
        Type type = ResolveCoreType(typeName);

        try
        {
            return Activator.CreateInstance(type, arguments)
                ?? throw new InvalidOperationException($"'{type.FullName}' could not be constructed.");
        }
        catch (MissingMethodException error)
        {
            Assert.Fail(
                $"'{type.FullName}' has no public constructor accepting ({string.Join(", ", Array.ConvertAll(arguments, argument => argument?.GetType().Name ?? "null"))}): {error.Message}");
            throw;
        }
    }
}
