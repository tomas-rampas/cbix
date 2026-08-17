using System.Text.RegularExpressions;

using Cbix.Core.Review;

namespace Cbix.UnitTests.Review;

/// <summary>
/// Keeps <c>db/schema/review_queue.sql</c> and the in-process types that mirror it from drifting
/// apart (story S01-15, hardened in review).
/// </summary>
/// <remarks>
/// <para>
/// <b>The same class of guard as <c>CbixEventIdTests</c>, and it exists for the same measured
/// reason.</b> Two artefacts have to agree, nothing compiles across the gap between them, and the
/// symptom of disagreement arrives much later and somewhere else: a <see cref="ReviewReason"/> member
/// added without touching the DDL produces code that builds, tests that pass, and - in Sprint 03,
/// against a real database, on the document that most needed a human - an insert refused by a CHECK
/// constraint. A width that drifts produces the same failure one column over.
/// </para>
/// <para>
/// <b>It reads the .sql file as text, deliberately.</b> Nothing in the build parses SQL, so the file
/// is documentation with no compiler behind it; reading it here is what gives it one. The parsing is
/// intentionally shallow - find the CHECK list, find three widths - because a fuller SQL parser in a
/// test would be a second implementation to get wrong, and the properties worth guarding are exactly
/// the two that a human edit forgets.
/// </para>
/// </remarks>
public sealed class ReviewQueueSchemaParityTests
{
    [Fact]
    public void EveryReviewReasonAppearsInTheDdlsCheckConstraint()
    {
        // The direction that matters: adding an enum member without extending the CHECK. The reverse -
        // a CHECK naming a reason the enum no longer has - is harmless (a value nothing writes) and is
        // deliberately not asserted, so that removing a reason in code does not require the DDL to
        // change in the same commit as the schema migration that would drop it.
        string ddl = ReadSchema();

        Match check = Regex.Match(
            ddl,
            @"CONSTRAINT\s+ck_review_queue_reason\s+CHECK\s*\(\s*reason\s+IN\s*\((?<values>[^)]*)\)",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(5));

        Assert.True(
            check.Success,
            "db/schema/review_queue.sql declares no ck_review_queue_reason CHECK, so the database would "
                + "accept any string as a review reason and the enum would be a convention rather than a "
                + "constraint.");

        string values = check.Groups["values"].Value;

        List<string> missing =
        [
            .. Enum.GetNames<ReviewReason>()
                .Where(name => !values.Contains($"'{name}'", StringComparison.Ordinal)),
        ];

        Assert.True(
            missing.Count == 0,
            $"These ReviewReason members are absent from the DDL's CHECK list: {string.Join(", ", missing)}. "
                + "A row carrying one would be refused by the database - in Sprint 03, at run time, on a "
                + "document that needed a human. Extend the CHECK in db/schema/review_queue.sql.");

        // Guard the guard: a regex that matched an empty list would satisfy the assertion above for an
        // enum with no members, and would keep satisfying it if the CHECK were emptied.
        Assert.Contains(nameof(ReviewReason.TriageLowConfidence), values, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("document_id", "varchar", ReviewQueueEntry.MaxDocumentIdLength)]
    [InlineData("detail", "nvarchar", ReviewQueueEntry.MaxDetailLength)]
    [InlineData("agent_name", "nvarchar", ReviewQueueEntry.MaxAgentNameLength)]
    public void ColumnWidthsMatchTheApplicationSideBounds(string column, string sqlType, int expected)
    {
        // The application refuses above these widths so the bound is a decision made where the text is
        // built rather than an error thrown where it is stored - which only works while the two numbers
        // agree. If the column shrinks and the constant does not, the app cheerfully hands the database
        // a value it will reject; if the constant shrinks and the column does not, the app refuses rows
        // the schema would have taken.
        string ddl = ReadSchema();

        Match declaration = Regex.Match(
            ddl,
            $@"^\s*{Regex.Escape(column)}\s+{Regex.Escape(sqlType)}\((?<width>\d+)\)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline,
            TimeSpan.FromSeconds(5));

        Assert.True(
            declaration.Success,
            $"db/schema/review_queue.sql declares no '{column} {sqlType}(n)' column, so the width "
                + $"ReviewQueueEntry enforces ({expected}) is guarding nothing.");

        Assert.Equal(expected, int.Parse(declaration.Groups["width"].Value, null));
    }

    /// <summary>Reads the committed DDL, failing with the reason rather than an IO exception.</summary>
    /// <remarks>
    /// The walk is the one <c>ProviderContainmentTests</c> already uses in this project.
    /// <c>RepositoryLayout</c> would be the obvious home for it, but that type lives in
    /// <c>Cbix.Bdd</c>, and a unit-test project referencing the BDD project to reach a path helper
    /// would invert the dependency for eight lines.
    /// </remarks>
    private static string ReadSchema()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cbix.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"No directory containing 'Cbix.sln' was found above '{AppContext.BaseDirectory}'.");

        string path = Path.Combine(directory!.FullName, "db", "schema", "review_queue.sql");

        Assert.True(File.Exists(path), $"The review-queue schema is missing from the repository at '{path}'.");

        return File.ReadAllText(path);
    }
}
