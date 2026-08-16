using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

using Cbix.Core.Documents;

using Cbix.Providers.Anthropic;

namespace Cbix.UnitTests.Providers;

/// <summary>
/// Backstop for the provider-containment half of story S01-08, complementing the BDD scenario in
/// <c>AnthropicProviderAdapter.feature</c>.
/// <para>
/// It is the sibling of <see cref="CoreAssemblyNeutralityTests"/> and answers a different
/// question. That one asks "may <c>Cbix.Core</c> depend on this at all" and enforces a closed
/// allowlist. This one asks the story's question - "did an Anthropic type escape its adapter" -
/// across <em>every</em> non-adapter solution assembly, and so is a denylist of provider
/// assemblies. An allowlist would be the wrong instrument here: the composition host must be free
/// to reference the adapter project in order to construct the provider, and gains new neutral
/// dependencies (hosting, DI, logging) as a matter of course.
/// </para>
/// <para>
/// The distinction that makes this meaningful: a project reference is fine, a <em>type</em>
/// reference is not. <c>GetReferencedAssemblies</c> reports only assemblies the compiler actually
/// emitted a reference to, so a host may reference the adapter project all day without appearing
/// to fail here - and fails the moment a line of its code names an Anthropic type.
/// </para>
/// </summary>
public sealed class ProviderContainmentTests
{
    /// <summary>
    /// Assemblies carrying Anthropic-specific types: MAF's provider integration and the official
    /// Anthropic .NET SDK it wraps. The SDK is listed alongside the integration on purpose - code
    /// that reached past the integration straight to <c>AnthropicClient</c> would violate the same
    /// rule while naming a different assembly.
    /// </summary>
    private static readonly string[] ProviderAssemblies =
    [
        "Microsoft.Agents.AI.Anthropic",
        "Anthropic",
    ];

    /// <summary>
    /// Member-name fragment identifying the raw-representation escape hatch. It is a member on the
    /// neutral <c>ChatOptions</c> / <c>AIContent</c> types, so it cannot be caught by an
    /// assembly-level check - only by looking at which members an assembly references.
    /// </summary>
    private const string EscapeHatchMember = "RawRepresentation";

    [Fact]
    public void NeutralAssemblies_ReferenceNoProviderAssembly()
    {
        List<string> offenders = [];

        foreach (Assembly assembly in CbixAssemblies.Neutral())
        {
            foreach (string reference in assembly.GetReferencedAssemblies().Select(r => r.Name ?? string.Empty))
            {
                if (Array.Exists(ProviderAssemblies, p => string.Equals(p, reference, StringComparison.Ordinal)))
                {
                    offenders.Add($"{CbixAssemblies.Name(assembly)} -> {reference}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Provider types escaped the adapter: {string.Join(", ", offenders)}. Anthropic types live in "
                + "Cbix.Providers.Anthropic and nowhere else; everything else depends on AIAgent / IChatClient "
                + "plus a capability profile, so a provider swap stays a configuration change.");
    }

    [Fact]
    public void NeutralAssemblies_DoNotUseTheRawRepresentationEscapeHatch()
    {
        // Design 3 makes the adapter the sole location for raw-representation calls. Until now that
        // was a comment in a file that claimed its rules were enforced; this makes it checkable.
        //
        // Mechanism: read each assembly's IL metadata and look at the member and type references
        // the compiler emitted. That CATCHES the ordinary way of getting this wrong - setting
        // ChatOptions.RawRepresentationFactory or reading AIContent.RawRepresentation from a
        // non-adapter assembly, which emits a MemberReference by name. It does NOT catch access via
        // reflection on a string literal, dynamic dispatch, or a helper that wraps the property in a
        // third assembly. Those are not the accident this guards against; the accident is somebody
        // reaching for the escape hatch in the workflow because it was convenient.
        List<string> offenders = [];

        foreach (Assembly assembly in CbixAssemblies.Neutral())
        {
            if (string.IsNullOrEmpty(assembly.Location) || !File.Exists(assembly.Location))
            {
                continue;
            }

            foreach (string member in ReferencedMemberNames(assembly.Location))
            {
                if (member.Contains(EscapeHatchMember, StringComparison.Ordinal))
                {
                    offenders.Add($"{CbixAssemblies.Name(assembly)} -> {member}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"The raw-representation escape hatch is used outside the provider adapter: {string.Join(", ", offenders)}. "
                + "Provider-specific request shaping belongs inside Cbix.Providers.Anthropic (design 3).");
    }

    [Fact]
    public void MemberReferenceScan_ActuallyFindsMembersThatArePresent()
    {
        // Positive control for the test above. No assembly uses the escape hatch yet, so that
        // assertion currently passes over an empty offender list - which is indistinguishable from
        // a scanner that silently returns nothing (a bad metadata handle, a changed API, an
        // assembly whose location is empty). This proves the mechanism detects a member reference
        // by name, using one the adapter definitely emits.
        Assembly adapter = typeof(AnthropicAgentFactory).Assembly;

        List<string> members = [.. ReferencedMemberNames(adapter.Location)];

        Assert.NotEmpty(members);
        Assert.Contains("AsAIAgent", members, StringComparer.Ordinal);
    }

    [Fact]
    public void ProviderAdapter_ReferencesTheProviderAssemblies()
    {
        // Guards the guard. If the adapter stopped wrapping the Anthropic integration - deleted,
        // renamed, or rewritten against a different package - every assertion above would pass
        // while proving nothing at all.
        Assembly adapter = typeof(AnthropicAgentFactory).Assembly;

        List<string> referenced =
        [
            .. adapter.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty),
        ];

        List<string> missing =
        [
            .. ProviderAssemblies.Where(provider => !referenced.Contains(provider, StringComparer.Ordinal)),
        ];

        Assert.True(
            missing.Count == 0,
            $"'{adapter.GetName().Name}' does not reference {string.Join(", ", missing)}, so the containment "
                + "assertions are vacuous. Either the adapter no longer wraps the Anthropic integration, or "
                + "the package identity changed and these tests must change with it.");
    }

    [Fact]
    public void ProviderAdapter_ExposesOnlyFrameworkTypesOnItsPublicSurface()
    {
        // The reference-table checks prove no OTHER assembly names a provider type. This proves the
        // adapter does not force them to: a public method returning ChatClientAgent, or a property
        // typed as ClientOptions, would make the leak unavoidable for any caller - and the
        // containment tests above would still pass, because the leak would only appear once
        // somebody wrote that call site.
        Assembly adapter = typeof(AnthropicAgentFactory).Assembly;

        List<string> offenders = [];

        foreach (Type type in adapter.GetExportedTypes())
        {
            const BindingFlags Surface =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (MethodInfo method in type.GetMethods(Surface))
            {
                AddIfProviderType(method.ReturnType, $"{type.Name}.{method.Name} return", offenders);
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    AddIfProviderType(
                        parameter.ParameterType,
                        $"{type.Name}.{method.Name} parameter '{parameter.Name}'",
                        offenders);
                }
            }

            foreach (PropertyInfo property in type.GetProperties(Surface))
            {
                AddIfProviderType(property.PropertyType, $"{type.Name}.{property.Name}", offenders);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"The adapter's public surface exposes provider types: {string.Join(", ", offenders)}. "
                + "Provider payloads ride inside AIContent.RawRepresentation, never in a signature.");
    }

    [Fact]
    public void CoreAssembly_StillDoesNotKnowTheAdapterExists()
    {
        // Direction matters as much as containment: the adapter depends on Core (it will implement
        // IDocumentContentProvider there in S01-05), and Core must never depend back. A cycle would
        // be a build error, but a one-way reference from Core to the adapter would compile fine and
        // silently pull a provider package into the assembly every other project depends on.
        Assembly core = typeof(IDocumentContentProvider).Assembly;

        Assert.DoesNotContain(
            core.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Cbix.Providers.Anthropic", StringComparison.Ordinal));
    }

    [Fact]
    public void NoTrackedConfigurationFileCarriesAnApiKey()
    {
        // Turns the "never from a tracked configuration file" comment on AnthropicProviderOptions
        // into a control. It is NOT vacuous: src/Cbix.Worker ships tracked appsettings.json and
        // appsettings.Development.json, both of which this scan reads. The repo-root assertion
        // below additionally stops it passing because it searched nowhere, and the file-count
        // assertion stops a future reorganisation quietly emptying its input.
        string repositoryRoot = FindRepositoryRoot();

        List<string> configurationFiles = [.. EnumerateConfigurationAssets(repositoryRoot)];

        Assert.True(
            configurationFiles.Count > 0,
            "No tracked configuration assets were found; the scan would report clean without "
                + "having read anything.");

        List<string> offenders = [];
        foreach (string file in configurationFiles)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            FindSecretProperties(document.RootElement, Path.GetRelativePath(repositoryRoot, file), offenders);
        }

        Assert.True(
            offenders.Count == 0,
            $"Credential-bearing properties found in tracked configuration: {string.Join(", ", offenders)}. "
                + "The Anthropic API key comes from user-secrets, the environment, or the secrets manager.");
    }

    [Fact]
    public void ConfigurationScan_DiscoversAFileMatchingEveryPattern()
    {
        // The discovery half of the positive control. The matcher test below proves the JSON walk
        // fires; nothing proved that ENUMERATION does. A typo in one pattern - or a pattern that
        // matches nothing on a case-sensitive file system - would silently narrow the scan while
        // every assertion built on it kept passing, which is precisely how the
        // {ApplicationName}.settings.json convention went unnoticed.
        //
        // Planted under a temporary root rather than inside the repository: the walk is identical,
        // and scratch files in the working tree are exactly what .gitignore's blacklist makes
        // invisible.
        (string Pattern, string FileName)[] samples =
        [
            ("appsettings*.json", "appsettings.Production.json"),
            ("*.settings.json", "Cbix.Worker.settings.json"),
            ("launchSettings.json", "launchSettings.json"),
            ("secrets.json", "secrets.json"),
        ];

        Assert.Equal(
            ConfigurationAssetPatterns.Order(StringComparer.Ordinal),
            samples.Select(sample => sample.Pattern).Order(StringComparer.Ordinal));

        string root = Path.Combine(Path.GetTempPath(), $"cbix-asset-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            foreach ((_, string fileName) in samples)
            {
                File.WriteAllText(Path.Combine(root, fileName), "{}");
            }

            // Build output must stay excluded: it holds copies of tracked files, so including it
            // would report a stale leak long after the real file was cleaned up.
            string buildOutput = Path.Combine(root, "bin", "Debug");
            Directory.CreateDirectory(buildOutput);
            File.WriteAllText(Path.Combine(buildOutput, "appsettings.json"), "{}");

            List<string> discovered =
            [
                .. EnumerateConfigurationAssets(root).Select(path => Path.GetFileName(path)!),
            ];

            foreach ((string pattern, string fileName) in samples)
            {
                Assert.True(
                    discovered.Contains(fileName, StringComparer.Ordinal),
                    $"Pattern '{pattern}' did not discover '{fileName}', so that class of configuration "
                        + "asset is not being scanned at all.");
            }

            Assert.Equal(samples.Length, discovered.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConfigurationScan_ActuallyDetectsAPlantedKey()
    {
        // Matcher half of the positive control: the scan above passes over a repository that
        // legitimately carries no credential, which is indistinguishable from a matcher that found
        // nothing because it was broken. These are the two shapes a leaked key really takes: a bound
        // configuration section, and an environment block in a launch profile.
        const string PlantedSection = """
            { "Cbix": { "Providers": { "Anthropic": { "ApiKey": "leaked", "ModelId": "fine" } } } }
            """;
        const string PlantedLaunchProfile = """
            { "profiles": { "Cbix.Worker": { "environmentVariables": { "ANTHROPIC_API_KEY": "leaked" } } } }
            """;

        List<string> sectionOffenders = [];
        using (JsonDocument document = JsonDocument.Parse(PlantedSection))
        {
            FindSecretProperties(document.RootElement, "appsettings.json", sectionOffenders);
        }

        List<string> launchOffenders = [];
        using (JsonDocument document = JsonDocument.Parse(PlantedLaunchProfile))
        {
            FindSecretProperties(document.RootElement, "launchSettings.json", launchOffenders);
        }

        Assert.Equal(["appsettings.json:Cbix:Providers:Anthropic:ApiKey"], sectionOffenders);
        Assert.Equal(
            ["launchSettings.json:profiles:Cbix.Worker:environmentVariables:ANTHROPIC_API_KEY"],
            launchOffenders);
    }

    /// <summary>
    /// File-name patterns for the configuration assets that could carry a credential into a commit,
    /// a build output or an image layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kept in step with the BDD asset scan</b> in <c>Cbix.Bdd</c>'s <c>SecretSourcingSteps</c>
    /// (story S01-17), which walks the same repository with the same patterns plus the container
    /// assets. Neither supersedes the other and both are needed: this one parses JSON, so it reports
    /// the offending key path and cannot be fooled by formatting; that one is a text scan, so it
    /// covers Dockerfiles and <c>.env</c> files that are not JSON at all. Change one, change the
    /// other.
    /// </para>
    /// <para>
    /// <c>*.settings.json</c> is on the list because <c>Host.CreateApplicationBuilder</c> was
    /// measured loading <c>{ApplicationName}.settings.json</c> - and loading it at <em>higher</em>
    /// precedence than <c>appsettings.json</c> (the shape is pinned by
    /// <c>WorkerCompositionTests.DefaultHostConfiguration_LoadsThePinnedProviderShape</c>). A key
    /// planted in <c>Cbix.Worker.settings.json</c> was a real, working leak that neither scan saw.
    /// </para>
    /// <para>
    /// <c>secrets.json</c> is on the list for the mirror-image reason: the run-time provider
    /// allowlist <em>approves</em> a file provider reading a file of that name, because user-secrets
    /// lives outside the repository. A file with that name inside the repository would therefore be
    /// trusted at run time, so it has to be refused here instead.
    /// </para>
    /// </remarks>
    private static readonly string[] ConfigurationAssetPatterns =
    [
        "appsettings*.json",
        "*.settings.json",
        "launchSettings.json",
        "secrets.json",
    ];

    /// <summary>Configuration assets under <paramref name="root"/>, excluding build output.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="MatchCasing.CaseInsensitive"/> is explicit rather than inherited. The default
    /// follows the file system, which would make this scan case-sensitive on the Linux CI runner and
    /// case-insensitive on a Windows developer machine - so <c>AppSettings.json</c> would be caught
    /// locally and shipped by CI, or the reverse. The run-time provider allowlist compares file
    /// names with <see cref="StringComparison.OrdinalIgnoreCase"/>, and a control that classifies
    /// the same input differently per platform is not a control.
    /// </para>
    /// <para>
    /// <see cref="EnumerationOptions.IgnoreInaccessible"/> keeps one unreadable directory from
    /// aborting the whole walk, which would turn a partial scan into a green result.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> EnumerateConfigurationAssets(string root)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,

            // DO NOT REMOVE, even though no pattern above starts with a dot today. The default is
            // Hidden | System, and on Unix .NET reports every dot-prefixed name as
            // FileAttributes.Hidden - so a dotfile pattern added later would silently match nothing
            // on the Linux CI runner while working on Windows. The sibling scan in Cbix.Bdd's
            // SecretSourcingSteps already carries '.env' patterns and was measured hitting exactly
            // that; the two scans are kept in step, so this one carries the same setting rather than
            // waiting to rediscover the same defect.
            AttributesToSkip = FileAttributes.None,
        };

        return ConfigurationAssetPatterns
            .SelectMany(pattern => Directory.EnumerateFiles(root, pattern, options))
            .Where(IsNotBuildOutput)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Excludes build and VCS directories: they hold copies of whatever is tracked, so scanning them
    /// would double-report and, worse, report a stale copy long after the source was cleaned up.
    /// </summary>
    private static bool IsNotBuildOutput(string path)
    {
        char separator = Path.DirectorySeparatorChar;

        return !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
            && !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
            && !path.Contains($"{separator}.git{separator}", StringComparison.Ordinal);
    }

    /// <summary>Every member and type name referenced from an assembly's IL metadata.</summary>
    private static IEnumerable<string> ReferencedMemberNames(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader peReader = new(stream);

        if (!peReader.HasMetadata)
        {
            yield break;
        }

        MetadataReader metadata = peReader.GetMetadataReader();

        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            yield return metadata.GetString(metadata.GetMemberReference(handle).Name);
        }

        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            yield return metadata.GetString(metadata.GetTypeReference(handle).Name);
        }
    }

    /// <summary>
    /// Records any property that names a credential: an <c>ApiKey</c> anywhere under an
    /// <c>Anthropic</c> section, or any <c>ANTHROPIC_*</c> variable (the shape a leaked key takes in
    /// a <c>launchSettings.json</c> environment block).
    /// </summary>
    private static void FindSecretProperties(JsonElement element, string file, List<string> offenders, string path = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string childPath = path.Length == 0 ? property.Name : $"{path}:{property.Name}";

                    bool isAnthropicApiKey =
                        property.Name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase)
                        && childPath.Contains("Anthropic", StringComparison.OrdinalIgnoreCase);
                    bool isAnthropicVariable =
                        property.Name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase);

                    if (isAnthropicApiKey || isAnthropicVariable)
                    {
                        offenders.Add($"{file}:{childPath}");
                    }

                    FindSecretProperties(property.Value, file, offenders, childPath);
                }

                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    FindSecretProperties(item, file, offenders, $"{path}[{index++}]");
                }

                break;

            default:
                break;
        }
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
            "Could not locate the repository root (no Cbix.sln above the test binaries); the configuration "
                + "scan would have inspected nothing.");

        return directory!.FullName;
    }

    private static void AddIfProviderType(Type type, string location, List<string> offenders)
    {
        // Generic arguments and element types are walked so a provider type cannot hide inside a
        // Task<T>, an array, or an IReadOnlyList<T>.
        string assembly = type.Assembly.GetName().Name ?? string.Empty;
        if (Array.Exists(ProviderAssemblies, provider => string.Equals(provider, assembly, StringComparison.Ordinal)))
        {
            offenders.Add($"{location} ({type.FullName})");
        }

        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            AddIfProviderType(elementType, location, offenders);
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            AddIfProviderType(argument, location, offenders);
        }
    }
}
