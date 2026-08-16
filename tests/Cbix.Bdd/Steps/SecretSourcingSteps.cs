using System.Globalization;
using System.Reflection;

using Cbix.Bdd.Support;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/SecretSourcing.feature</c> (story S01-17).
/// </summary>
/// <remarks>
/// <para>
/// The worker's registration extension is resolved by name at run time. That keeps the red phase a
/// failing assertion rather than a broken build, which the BDD working agreement requires, and it
/// is the same technique the S01-08 scenarios use.
/// </para>
/// <para>
/// <b>How the second scenario proves provenance without ever exposing the key.</b> Nothing in the
/// composed graph hands back the resolved credential - deliberately, since the adapter's client and
/// its options were measured printing it in clear. So the scenario proves the key came from the
/// secrets-manager source <em>differentially</em>: composing with that source succeeds, and
/// composing the identical graph without it fails. If an appsettings file (or any other tracked
/// asset) were supplying the key, the second composition would also have succeeded.
/// </para>
/// </remarks>
[Binding]
public sealed class SecretSourcingSteps(SecretSourcingState state)
{
    /// <summary>The assembly holding the worker's composition.</summary>
    private const string WorkerAssemblyName = "Cbix.Worker";

    /// <summary>The provider adapter assembly, whose factory the composed graph must supply.</summary>
    private const string ProviderAssemblyName = "Cbix.Providers.Anthropic";

    /// <summary>The registration extension the thin <c>Program.cs</c> delegates to.</summary>
    private const string RegistrationMethodName = "AddCbixWorker";

    /// <summary>Configuration key the secrets manager (and its local stand-ins) populate.</summary>
    private const string ApiKeyConfigurationKey = "Cbix:Providers:Anthropic:ApiKey";

    /// <summary>
    /// Fragments that make a line of a configuration file or image asset credential-shaped. The
    /// vendor prefix catches a pasted key, the variable names catch an environment block, and
    /// <c>ApiKey</c> catches a bound configuration section.
    /// </summary>
    private static readonly string[] CredentialFragments =
    [
        "sk-ant-",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "ApiKey",
    ];

    /// <summary>
    /// Filename patterns for everything that ships with, or configures, the worker: the .NET
    /// configuration files and the container assets. No Docker asset exists yet - the patterns are
    /// present so the scan covers one the day it is added, rather than being quietly narrower than
    /// the scenario claims.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kept in step with the unit-test asset scan</b> in
    /// <c>ProviderContainmentTests.NoTrackedConfigurationFileCarriesAnApiKey</c>, whose
    /// <c>ConfigurationAssetPatterns</c> covers the JSON subset of this list. Neither supersedes the
    /// other: that one parses JSON and reports the offending key path; this one is a text scan, so
    /// it is the only one that can read a Dockerfile or a <c>.env</c> file. Change one, change the
    /// other.
    /// </para>
    /// <para>
    /// <c>*.settings.json</c> is here because <c>Host.CreateApplicationBuilder</c> was measured
    /// loading <c>{ApplicationName}.settings.json</c> above <c>appsettings.json</c>, and a key
    /// planted in <c>Cbix.Worker.settings.json</c> was a working leak both scans missed.
    /// <c>secrets.json</c> is here because the run-time provider allowlist approves a provider
    /// reading a file of that name (user-secrets lives outside the repository), so a tracked file
    /// with that name would be trusted at run time and must be caught statically instead.
    /// </para>
    /// </remarks>
    private static readonly string[] AssetPatterns =
    [
        "appsettings*.json",
        "*.settings.json",
        "launchSettings.json",
        "secrets.json",
        "Dockerfile",
        "Dockerfile.*",
        "*.Dockerfile",
        "docker-compose*.yml",
        "docker-compose*.yaml",
        ".env",
        ".env.*",
    ];

    [Given("the worker's tracked configuration and image assets")]
    public void GivenTheWorkersTrackedConfigurationAndImageAssets()
    {
        state.ScannedFiles = [.. Discover(FindRepositoryRoot())];

        // Fail closed. A scan that found nothing cannot distinguish "clean" from "broken", and the
        // worker's own appsettings.json is the one file that certainly exists.
        Assert.Contains(
            state.ScannedFiles,
            path => Path.GetFileName(path).Equals("appsettings.json", StringComparison.OrdinalIgnoreCase));
    }

    [When("I search them for credential-shaped content")]
    public void WhenISearchThemForCredentialShapedContent() =>
        state.ScanFindings = [.. state.ScannedFiles.SelectMany(Scan)];

    [Then("no match is found")]
    public void ThenNoMatchIsFound() =>
        Assert.True(
            state.ScanFindings.Count == 0,
            $"Credential-shaped content found in tracked assets: {string.Join(", ", state.ScanFindings)}. "
                + "The Anthropic API key comes from user-secrets, the environment, or the bank's secrets "
                + "manager - never from a tracked configuration file or a baked image layer.");

    [Then("the same search does find a planted key, so the scan is not vacuous")]
    public void ThenTheSameSearchFindsAPlantedKey()
    {
        // Positive control. The assertion above passes over a repository that legitimately contains
        // no credential, which is indistinguishable from a scanner that matched nothing because its
        // patterns, its file list, or its reader was broken.
        //
        // It exercises DISCOVERY, not just the matcher: a planted file per pattern, found by walking
        // a root exactly as the Given step walks the repository. Calling the matcher on a known path
        // would have proved only that string comparison works - and would have kept passing through
        // the whole period when the patterns did not know {ApplicationName}.settings.json existed.
        //
        // The root is a temporary directory rather than the repository itself: the walk is
        // identical, and scratch files in the working tree are exactly what .gitignore's blacklist
        // makes invisible.
        (string Pattern, string FileName)[] samples =
        [
            ("appsettings*.json", "appsettings.Production.json"),
            ("*.settings.json", "Cbix.Worker.settings.json"),
            ("launchSettings.json", "launchSettings.json"),
            ("secrets.json", "secrets.json"),
            ("Dockerfile", "Dockerfile"),
            ("Dockerfile.*", "Dockerfile.worker"),
            ("*.Dockerfile", "worker.Dockerfile"),
            ("docker-compose*.yml", "docker-compose.override.yml"),
            ("docker-compose*.yaml", "docker-compose.yaml"),
            (".env", ".env"),
            (".env.*", ".env.production"),
        ];

        // Every pattern must have a sample: a pattern nobody plants against is a pattern nobody has
        // proved works.
        Assert.Equal(
            AssetPatterns.Order(StringComparer.Ordinal),
            samples.Select(sample => sample.Pattern).Order(StringComparer.Ordinal));

        string root = state.CreateTemporaryDirectory();
        const string PlantedKey = """{ "Cbix": { "Providers": { "Anthropic": { "ApiKey": "planted" } } } }""";

        foreach ((_, string fileName) in samples)
        {
            File.WriteAllText(Path.Combine(root, fileName), PlantedKey);
        }

        // Build output stays excluded: it holds copies of tracked files, so including it would
        // report a stale leak long after the real file was cleaned up.
        string buildOutput = Path.Combine(root, "bin", "Debug");
        Directory.CreateDirectory(buildOutput);
        File.WriteAllText(Path.Combine(buildOutput, "appsettings.json"), PlantedKey);

        List<string> discovered = [.. Discover(root)];
        List<string> discoveredNames = [.. discovered.Select(path => Path.GetFileName(path)!)];

        foreach ((string pattern, string fileName) in samples)
        {
            Assert.True(
                discoveredNames.Contains(fileName, StringComparer.Ordinal),
                $"Pattern '{pattern}' did not discover '{fileName}', so that class of asset is not being "
                    + "scanned at all.");
        }

        Assert.Equal(samples.Length, discovered.Count);
        Assert.Equal(samples.Length, discovered.SelectMany(Scan).Count());
    }

    [Given("a secrets-manager-compatible provider carrying the Anthropic API key")]
    public void GivenASecretsManagerCompatibleProvider()
    {
        // In-memory here; user-secrets in local development; the bank's Vault/CyberArk provider in
        // production. All three are ordinary IConfiguration sources, which is exactly why the
        // stand-in is honest: what the worker depends on is the port, not this source.
        state.SecretsManagerSource = builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?> { [ApiKeyConfigurationKey] = SecretSourcingState.PlaceholderApiKey });
    }

    [Given("an appsettings file that carries no credential")]
    public void GivenAnAppsettingsFileThatCarriesNoCredential()
    {
        string directory = state.CreateTemporaryDirectory();
        string path = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(path, """{ "Logging": { "LogLevel": { "Default": "Information" } } }""");

        state.ConfigurationSources.Add(builder => builder.AddJsonFile(path, optional: false, reloadOnChange: false));
    }

    [Given("no configured source carries the Anthropic API key")]
    public void GivenNoConfiguredSourceCarriesTheKey()
    {
        // An explicitly empty configuration, not merely an absent one: the scenario has to be
        // immune to whatever ANTHROPIC_API_KEY the developer running it happens to have exported.
        state.SecretsManagerSource = null;
        state.ConfigurationSources.Add(builder => builder.AddInMemoryCollection([]));
    }

    [When("the worker composes its services and builds its host")]
    public void WhenTheWorkerComposesAndBuildsItsHost()
    {
        IHost host = Compose(includeSecretsManagerSource: true);
        state.Host = host;
        state.Services = host.Services;
    }

    [When("the worker composes its services")]
    public void WhenTheWorkerComposesItsServices()
    {
        try
        {
            IHost host = Compose(includeSecretsManagerSource: true);
            state.Host = host;
            state.Services = host.Services;
        }
        catch (Exception error)
        {
            state.CompositionFailure = error;
        }
    }

    [Then("the Anthropic agent factory resolves from the host's service provider")]
    public void ThenTheAgentFactoryResolves()
    {
        IServiceProvider services = state.Services
            ?? throw new InvalidOperationException("The When step did not compose a host.");

        Type factoryType = FindExportedType(LoadOrFail(ProviderAssemblyName), "AnthropicAgentFactory");

        object? factory = services.GetService(factoryType);
        Assert.True(
            factory is not null,
            $"'{factoryType.FullName}' is not registered in the worker's service graph, so nothing in the "
                + "pipeline could obtain an agent.");

        // Registered as a singleton: the factory owns one Anthropic client whose HTTP handler pools
        // connections across the seven-agent fan-out, so a second instance would be a real defect.
        Assert.Same(factory, services.GetService(factoryType));

        // The hosted service is part of the same assertion: a graph that resolves the factory but
        // cannot construct the worker would still fail at run time.
        Assert.NotEmpty(services.GetServices<IHostedService>());
    }

    [Then("composing without that provider fails, so the key came from it and not from appsettings")]
    public void ThenComposingWithoutTheProviderFails()
    {
        Assert.NotNull(state.SecretsManagerSource);

        // The differential. Identical graph, identical appsettings file, one source removed.
        Exception error = Assert.ThrowsAny<Exception>(() => Compose(includeSecretsManagerSource: false).Dispose());

        Assert.Contains(ApiKeyConfigurationKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSourcingState.PlaceholderApiKey, error.Message, StringComparison.Ordinal);
    }

    [Then("startup fails naming every source it consulted")]
    public void ThenStartupFailsNamingEverySource()
    {
        Assert.Null(state.Host);

        Exception error = state.CompositionFailure
            ?? throw new InvalidOperationException(
                "Composition succeeded with no key configured; startup must fail fast instead of "
                    + "surfacing a 401 mid-run.");

        // Both sources by name. An operator reading this message must not have to consult the
        // source tree to learn where the worker looked.
        Assert.Contains(ApiKeyConfigurationKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("ANTHROPIC_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Contains("user-secrets", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Then("the failure message carries no key value")]
    public void ThenTheFailureMessageCarriesNoKeyValue()
    {
        Exception error = state.CompositionFailure
            ?? throw new InvalidOperationException("The When step recorded no failure.");

        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            Assert.DoesNotContain(SecretSourcingState.PlaceholderApiKey, current.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Builds a host the way <c>Program.cs</c> does - through the worker's own registration
    /// extension - over exactly the configuration sources the scenario declared.
    /// </summary>
    /// <remarks>
    /// An <em>empty</em> application builder is used on purpose. The default one reads the ambient
    /// environment and the test host's own appsettings.json, so a scenario about which source
    /// supplied a secret would be answering a question the machine had already answered for it.
    /// Logging is added explicitly because the empty builder omits it and the hosted service needs
    /// an <c>ILogger</c>.
    /// </remarks>
    private IHost Compose(bool includeSecretsManagerSource)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();

        foreach (Action<IConfigurationBuilder> source in state.ConfigurationSources)
        {
            source(builder.Configuration);
        }

        if (includeSecretsManagerSource && state.SecretsManagerSource is { } secretsManager)
        {
            secretsManager(builder.Configuration);
        }

        InvokeRegistration(builder);

        return builder.Build();
    }

    /// <summary>Calls the worker's registration extension by name, unwrapping reflection's wrapper.</summary>
    private static void InvokeRegistration(IHostApplicationBuilder builder)
    {
        Assembly worker = LoadOrFail(WorkerAssemblyName);

        List<MethodInfo> candidates =
        [
            .. worker.GetExportedTypes()
                .Where(type => type is { IsAbstract: true, IsSealed: true })
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(method => string.Equals(method.Name, RegistrationMethodName, StringComparison.Ordinal))
                .Where(method => method.GetParameters() is [{ } only]
                    && only.ParameterType.IsInstanceOfType(builder)),
        ];

        Assert.True(
            candidates.Count > 0,
            $"'{WorkerAssemblyName}' exposes no public static '{RegistrationMethodName}(IHostApplicationBuilder)'. "
                + "The worker's service registration must live in one named extension so a test can compose "
                + "the real graph without launching the executable.");
        Assert.True(
            candidates.Count == 1,
            $"'{WorkerAssemblyName}' exposes {candidates.Count} '{RegistrationMethodName}' overloads; the "
                + "composition entry point must be unambiguous.");

        try
        {
            candidates[0].Invoke(null, [builder]);
        }
        catch (TargetInvocationException wrapper) when (wrapper.InnerException is { } inner)
        {
            // Rethrowing the inner exception preserves the message the scenarios assert on;
            // TargetInvocationException's own message says nothing about secrets.
            throw inner;
        }
    }

    /// <summary>Credential-shaped findings in one file, as "relative path: fragment" strings.</summary>
    private static IEnumerable<string> Scan(string path)
    {
        string content = File.ReadAllText(path);

        foreach (string fragment in CredentialFragments)
        {
            if (content.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"{path}: {fragment}");
            }
        }
    }

    /// <summary>Assets under <paramref name="root"/> matching any pattern, excluding build output.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="MatchCasing.CaseInsensitive"/> is set explicitly rather than inherited. The
    /// default follows the file system, which would make this scan case-sensitive on the Linux CI
    /// runner and case-insensitive on a Windows developer machine - so <c>AppSettings.json</c> would
    /// be caught locally and shipped by CI, or the reverse. The run-time provider allowlist compares
    /// file names with <see cref="StringComparison.OrdinalIgnoreCase"/>, and a control that
    /// classifies the same input differently per platform is not a control.
    /// </para>
    /// <para>
    /// <see cref="EnumerationOptions.IgnoreInaccessible"/> stops one unreadable directory aborting
    /// the walk, which would turn a partial scan into a green scenario.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Discover(string root)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        return AssetPatterns
            .SelectMany(pattern => Directory.EnumerateFiles(root, pattern, options))
            .Where(IsNotBuildOutput)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// Excludes build and VCS directories: they hold copies of whatever is tracked, so scanning them
    /// would double-report and, worse, report a stale copy long after the source was fixed.
    /// </summary>
    private static bool IsNotBuildOutput(string path)
    {
        char separator = Path.DirectorySeparatorChar;

        return !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
            && !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
            && !path.Contains($"{separator}.git{separator}", StringComparison.Ordinal);
    }

    /// <summary>Loads an assembly by simple name, turning "not built" into a readable failure.</summary>
    private static Assembly LoadOrFail(string assemblyName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            Assert.Fail($"Assembly '{assemblyName}' could not be loaded: {error.Message}");
            throw; // Unreachable: Assert.Fail always throws.
        }
    }

    /// <summary>Finds the single public type with the given simple name, failing loudly on ambiguity.</summary>
    private static Type FindExportedType(Assembly assembly, string typeName)
    {
        Type[] candidates = Array.FindAll(
            assembly.GetExportedTypes(),
            type => string.Equals(type.Name, typeName, StringComparison.Ordinal));

        Assert.True(
            candidates.Length > 0,
            $"No public type named '{typeName}' exists in '{assembly.GetName().Name}'.");
        Assert.True(
            candidates.Length == 1,
            $"'{assembly.GetName().Name}' declares {candidates.Length} public types named '{typeName}'.");

        return candidates[0];
    }

    /// <summary>Walks up from the test binaries to the directory holding <c>Cbix.sln</c>.</summary>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cbix.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            "Could not locate the repository root (no Cbix.sln above the test binaries); the asset scan "
                + "would have inspected nothing.");

        return directory!.FullName;
    }
}
