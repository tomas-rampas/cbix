namespace Cbix.Bdd.Support;

/// <summary>
/// Locates the repository's committed inputs from the test binaries.
/// </summary>
/// <remarks>
/// <para>
/// The scenarios run against the real specimens rather than fixtures built to be easy, so more than
/// one feature needs this walk. It lives here because a copy of it is a second place to get the
/// failure message wrong - and the failure message is the whole value: a scenario whose input is
/// missing must say that, not fail somewhere inside PDFPig. Seven copies of the walk existed in this
/// assembly before story S01-09; six were folded in here and one deliberately remains (see below).
/// </para>
/// <para>
/// The specimen is repository data read <em>in place</em>, not a build artefact copied to the output
/// directory: copying it would duplicate a binary fixture per test project and let the copies drift
/// from the golden set that describes them. That is why the walk exists at all rather than a
/// <c>CopyToOutputDirectory</c> item.
/// </para>
/// <para>
/// <b>One copy deliberately remains</b>, in <c>SecretSourcingSteps</c>. Its walk carries a different
/// diagnostic - it names the asset scan that would otherwise report clean having read nothing - and
/// that message is the point of the control it serves, so folding it in here would replace a
/// specific failure with a generic one.
/// </para>
/// </remarks>
public static class RepositoryLayout
{
    /// <summary>The DE country instruction specimen (design's golden set v0 input).</summary>
    public const string DeSpecimenFileName = "Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf";

    /// <summary>Gets the repository's <c>data</c> directory: the ingest root the scenarios use.</summary>
    /// <returns>The fully qualified path.</returns>
    public static string DataDirectory() => Path.Combine(Root(), "data");

    /// <summary>Gets the fully qualified path of the DE specimen, asserting that it is present.</summary>
    /// <returns>The fully qualified path.</returns>
    public static string DeSpecimenPath()
    {
        string path = Path.Combine(DataDirectory(), DeSpecimenFileName);

        Assert.True(File.Exists(path), $"The DE specimen is missing from the repository at '{path}'.");

        return path;
    }

    /// <summary>Walks up from the test assembly's location to the directory holding <c>Cbix.sln</c>.</summary>
    /// <returns>The fully qualified repository root.</returns>
    public static string Root()
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
}
