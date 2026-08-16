using Cbix.Core.Documents;

namespace Cbix.UnitTests.Documents;

/// <summary>
/// The exception's job is to route the failure: transient means retry with backoff, permanent
/// means human review. These tests pin the property that carries that decision.
/// </summary>
public sealed class DocumentPreparationExceptionTests
{
    [Fact]
    public void Constructor_TransientFailure_ReportsRetryable()
    {
        DocumentPreparationException error = new("Files API returned 429.", isTransient: true);

        Assert.True(error.IsTransient);
        Assert.Equal("Files API returned 429.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Constructor_PermanentFailure_ReportsNotRetryable()
    {
        DocumentPreparationException error = new("The document is not a readable PDF.", isTransient: false);

        Assert.False(error.IsTransient);
    }

    [Fact]
    public void Constructor_WithInnerException_PreservesCauseAndTransience()
    {
        // Profiles wrap provider and I/O exceptions rather than letting provider types escape, so
        // the original cause has to survive for diagnostics.
        IOException cause = new("The process cannot access the file.");

        DocumentPreparationException error = new("Could not read the document.", isTransient: true, cause);

        Assert.True(error.IsTransient);
        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public void Exception_IsCatchableAsException()
    {
        Exception error = new DocumentPreparationException("boom", isTransient: false);

        DocumentPreparationException typed = Assert.IsType<DocumentPreparationException>(error);
        Assert.False(typed.IsTransient);
    }
}
