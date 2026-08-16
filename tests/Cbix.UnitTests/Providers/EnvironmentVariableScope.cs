namespace Cbix.UnitTests.Providers;

/// <summary>
/// Sets environment variables for the duration of a test and restores their prior values.
/// </summary>
/// <remarks>
/// Restore happens in <see cref="Dispose"/>, which the <c>using</c> statement runs even when the
/// test fails - a test that leaked a hostile <c>ANTHROPIC_BASE_URL</c> would poison every later
/// test in its collection and be diagnosed as a bug in the wrong place. The values restored are
/// whatever <see cref="AnthropicEnvironmentFixture"/> established as the collection's clean
/// baseline, not the developer's shell.
/// </remarks>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

    internal void Set(string name, string? value)
    {
        if (!_original.ContainsKey(name))
        {
            _original[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _original.Clear();
    }
}
