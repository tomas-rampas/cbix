using System.Globalization;
using System.Reflection;

using Cbix.Bdd.Support;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/AnthropicProviderAdapter.feature</c> (story S01-08).
/// <para>
/// Everything resolves by name at run time. That keeps the red phase a failing assertion rather
/// than a broken build, and it is what lets the second scenario assert on the compiler's own
/// reference tables - the one place where "no Anthropic type leaked" is a fact rather than a
/// claim.
/// </para>
/// <para>
/// The containment check is a <b>denylist of provider packages</b>, deliberately narrower than the
/// assembly allowlist in <c>CoreAssemblyNeutralityTests</c>. The allowlist there answers "may Core
/// depend on this at all"; this answers the story's question, "did an Anthropic type escape its
/// adapter". <c>Cbix.Worker</c> is legitimately allowed to reference the adapter <em>project</em> -
/// a composition root must be able to construct the provider - so an allowlist over Worker would
/// either be vacuous or forbid the wiring the design requires. The guard-the-guard step keeps this
/// from passing for the wrong reason.
/// </para>
/// </summary>
[Binding]
public sealed class AnthropicProviderAdapterSteps(AnthropicProviderAdapterState state)
{
    /// <summary>The adapter assembly - the only place an Anthropic type may appear.</summary>
    private const string ProviderAssemblyName = "Cbix.Providers.Anthropic";

    /// <summary>Prefix identifying the solution's own assemblies.</summary>
    private const string SolutionPrefix = "Cbix.";

    /// <summary>Prefix identifying provider adapters, which are exempt.</summary>
    private const string ProviderPrefix = "Cbix.Providers.";

    /// <summary>
    /// Assemblies that carry Anthropic-specific types: MAF's provider integration and the Anthropic
    /// SDK it wraps. Naming the SDK as well as the integration matters - a call that reached past
    /// the integration into <c>AnthropicClient</c> directly would otherwise slip through.
    /// </summary>
    private static readonly string[] ProviderAssemblyNames = ["Microsoft.Agents.AI.Anthropic", "Anthropic"];

    /// <summary>
    /// A self-labelling fictional key. It is a literal in a test binding on purpose: building an
    /// agent must be a pure, in-process operation, so a value that could never authenticate is
    /// sufficient - and if that assumption ever breaks, this scenario fails loudly rather than
    /// quietly reaching the network from CI. Deliberately carries no <c>sk-ant-</c> vendor prefix,
    /// which secret scanners pattern-match; a repository that trips its own scanner on test data
    /// teaches everyone to ignore the alerts.
    /// </summary>
    private const string DummyApiKey = "bdd-scenario-placeholder-key-not-a-real-credential";

    /// <summary>
    /// Environment variables the SDK reads that would otherwise decide this scenario's outcome.
    /// </summary>
    private static readonly string[] AmbientVariables =
    [
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_PROFILE",
        "ANTHROPIC_CONFIG_DIR",
    ];

    private readonly Dictionary<string, string?> _ambientBackup = new(StringComparer.Ordinal);

    /// <summary>Clears the ambient Anthropic environment so the scenario reports on the code.</summary>
    /// <remarks>
    /// The adapter deliberately refuses to start while <c>ANTHROPIC_AUTH_TOKEN</c> is set, so a
    /// developer who authenticates through a gateway or OAuth profile would see this scenario fail
    /// for a reason that has nothing to do with the adapter. A release-gate suite has to control
    /// the process state it reads.
    /// </remarks>
    [BeforeScenario]
    public void ClearAmbientAnthropicEnvironment()
    {
        foreach (string name in AmbientVariables)
        {
            _ambientBackup[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>Restores the developer's environment so a test run leaves no trace.</summary>
    [AfterScenario]
    public void RestoreAmbientAnthropicEnvironment()
    {
        foreach ((string name, string? value) in _ambientBackup)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _ambientBackup.Clear();
    }

    [Given("an Anthropic API key configured via the secrets-manager pattern")]
    public void GivenAnAnthropicApiKeyConfigured()
    {
        // S01-17 replaces this literal with a resolved secret. The adapter's contract is unchanged
        // either way: the key VALUE arrives from outside, already resolved.
        state.ApiKey = DummyApiKey;
    }

    [When("the provider adapter constructs an agent named {string} with a model and instructions")]
    public void WhenTheAdapterConstructsAnAgent(string agentName)
    {
        string apiKey = state.ApiKey
            ?? throw new InvalidOperationException("The Given step did not supply an API key.");

        Assembly provider = LoadOrFail(ProviderAssemblyName);

        Type optionsType = FindExportedType(provider, "AnthropicProviderOptions");
        Type factoryType = FindExportedType(provider, "AnthropicAgentFactory");

        object options = Activator.CreateInstance(optionsType)
            ?? throw new InvalidOperationException($"'{optionsType.FullName}' could not be constructed.");

        PropertyInfo apiKeyProperty = optionsType.GetProperty("ApiKey", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"'{optionsType.FullName}' exposes no settable 'ApiKey'; S01-17 has nowhere to bind a resolved secret.");
        apiKeyProperty.SetValue(options, apiKey);

        MethodInfo createAgent = factoryType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method => string.Equals(method.Name, "CreateAgent", StringComparison.Ordinal));

        state.CreateAgentMethod = createAgent;

        object factory = Activator.CreateInstance(factoryType, options)
            ?? throw new InvalidOperationException($"'{factoryType.FullName}' could not be constructed.");

        try
        {
            state.CreatedAgent = createAgent.Invoke(factory, BuildArguments(createAgent, agentName));
        }
        finally
        {
            (factory as IDisposable)?.Dispose();
        }
    }

    [Then("the returned object's static type is MAF's {string}")]
    public void ThenTheReturnedStaticTypeIsMafsAiAgent(string expectedTypeName)
    {
        MethodInfo createAgent = state.CreateAgentMethod
            ?? throw new InvalidOperationException("The When step did not resolve the factory method.");

        Type declaredReturnType = createAgent.ReturnType;

        // The STATIC type is the assertion. A ChatClientAgent instance would satisfy an
        // "is it an AIAgent" runtime check while still forcing every caller to name a
        // provider-flavoured type - which is precisely the leak this story exists to prevent.
        Assert.True(
            string.Equals(declaredReturnType.Name, expectedTypeName, StringComparison.Ordinal),
            $"'{createAgent.DeclaringType?.FullName}.{createAgent.Name}' declares a return type of "
                + $"'{declaredReturnType.FullName}'; the workflow must receive an ordinary '{expectedTypeName}'.");

        // Naming the assembly, not just the type: a provider could define its own `AIAgent` and
        // satisfy the name check above while coupling every caller to itself. MAF declares AIAgent
        // in its *Abstractions* assembly - the provider-neutral half of the framework, and the
        // right one for a type that crosses this boundary.
        string declaringAssembly = declaredReturnType.Assembly.GetName().Name ?? string.Empty;
        Assert.True(
            string.Equals(declaringAssembly, "Microsoft.Agents.AI.Abstractions", StringComparison.Ordinal),
            $"'{declaredReturnType.FullName}' comes from assembly '{declaringAssembly}', not MAF's "
                + "'Microsoft.Agents.AI.Abstractions'. The returned abstraction must be the framework's, "
                + "not a provider's.");

        object agent = state.CreatedAgent
            ?? throw new InvalidOperationException("The When step produced no agent instance.");

        Assert.True(
            declaredReturnType.IsInstanceOfType(agent),
            $"The factory returned a '{agent.GetType().FullName}', which is not a '{declaredReturnType.FullName}'.");
    }

    [Given("the compiled assemblies of the solution")]
    public void GivenTheCompiledAssembliesOfTheSolution()
    {
        // Discovered, not listed. A hardcoded pair went stale the moment a sprint added a project:
        // the new assembly could name Anthropic types freely while this scenario stayed green.
        Dictionary<string, Assembly> loaded = new(StringComparer.Ordinal);
        foreach (Assembly assembly in DiscoverNeutralAssemblies())
        {
            loaded[assembly.GetName().Name ?? string.Empty] = assembly;
        }

        // Fail closed: an empty set would make the containment assertion pass while inspecting
        // nothing at all.
        Assert.True(
            loaded.Count > 0,
            "No neutral Cbix assemblies were discovered next to the test binaries; the containment "
                + "assertion would be vacuous.");
        Assert.Contains("Cbix.Core", loaded.Keys, StringComparer.Ordinal);
        Assert.Contains("Cbix.Worker", loaded.Keys, StringComparer.Ordinal);

        state.NeutralAssemblies = loaded;
    }

    [When("I inspect every non-adapter assembly's referenced assemblies")]
    public void WhenIInspectTheReferencedAssemblies()
    {
        Dictionary<string, IReadOnlyList<string>> referenced = new(StringComparer.Ordinal);
        foreach ((string name, Assembly assembly) in state.NeutralAssemblies)
        {
            referenced[name] = ReferencedNames(assembly);
        }

        state.ReferencedAssemblies = referenced;
    }

    [Then("no code outside {string} references any type from the {string} package")]
    public void ThenNoCodeOutsideTheAdapterReferencesTheProviderPackage(string adapterAssembly, string providerPackage)
    {
        Assert.Equal(ProviderAssemblyName, adapterAssembly);
        Assert.Contains(providerPackage, ProviderAssemblyNames, StringComparer.Ordinal);
        Assert.NotEmpty(state.ReferencedAssemblies);

        List<string> offenders = [];
        foreach ((string assemblyName, IReadOnlyList<string> references) in state.ReferencedAssemblies)
        {
            foreach (string forbidden in ProviderAssemblyNames)
            {
                if (references.Contains(forbidden, StringComparer.Ordinal))
                {
                    offenders.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{assemblyName} -> {forbidden}"));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Provider types escaped '{adapterAssembly}': {string.Join(", ", offenders)}. "
                + "Anthropic types belong inside the provider adapter; everything else depends on "
                + "AIAgent / IChatClient plus a capability profile.");
    }

    [Then("{string} does reference it, so the check is not vacuous")]
    public void ThenTheAdapterDoesReferenceTheProviderPackage(string adapterAssembly)
    {
        // Guards the guard. Without this, deleting the adapter - or having it stop using the
        // integration at all - would make the assertion above pass while proving nothing.
        Assembly adapter = LoadOrFail(adapterAssembly);
        IReadOnlyList<string> references = ReferencedNames(adapter);

        List<string> missing =
        [
            .. ProviderAssemblyNames.Where(name => !references.Contains(name, StringComparer.Ordinal)),
        ];

        Assert.True(
            missing.Count == 0,
            $"'{adapterAssembly}' does not reference {string.Join(", ", missing)}, so the containment "
                + "assertion above is vacuous. Either the adapter no longer wraps the Anthropic "
                + "integration, or the package identity changed and this scenario must change with it.");
    }

    /// <summary>
    /// Loads an assembly by simple name, turning "not built yet" into a readable assertion failure
    /// instead of a <see cref="FileNotFoundException"/> mid-scenario.
    /// </summary>
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

    /// <summary>
    /// Every solution assembly deployed beside the tests that must stay free of provider types:
    /// all <c>Cbix.*</c> assemblies except the provider adapters and the test assemblies.
    /// </summary>
    /// <remarks>
    /// Scans the output directory rather than the loaded-assembly set, which the CLR populates
    /// lazily and therefore varies with test ordering. Test assemblies are identified by their
    /// xUnit reference rather than a name convention - the reference is what actually makes an
    /// assembly a test assembly, and test projects legitimately reference the adapter in order to
    /// test it. (The unit-test project carries the same logic in <c>CbixAssemblies</c>; the two
    /// suites are separate assemblies with no shared support project, and adding one for twenty
    /// lines would be a larger structural change than this story warrants.)
    /// </remarks>
    private static IEnumerable<Assembly> DiscoverNeutralAssemblies()
    {
        foreach (string path in Directory.EnumerateFiles(AppContext.BaseDirectory, $"{SolutionPrefix}*.dll"))
        {
            Assembly? assembly = null;
            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            catch (Exception error) when (error is BadImageFormatException or FileLoadException)
            {
                continue;
            }

            string name = assembly.GetName().Name ?? string.Empty;

            if (!name.StartsWith(SolutionPrefix, StringComparison.Ordinal)
                || name.StartsWith(ProviderPrefix, StringComparison.Ordinal)
                || IsTestAssembly(assembly))
            {
                continue;
            }

            yield return assembly;
        }
    }

    private static bool IsTestAssembly(Assembly assembly) =>
        Array.Exists(
            assembly.GetReferencedAssemblies(),
            reference => (reference.Name ?? string.Empty).StartsWith("xunit", StringComparison.OrdinalIgnoreCase));

    /// <summary>Simple names of every assembly the compiler emitted into <paramref name="assembly"/>'s reference table.</summary>
    private static IReadOnlyList<string> ReferencedNames(Assembly assembly) =>
    [
        .. assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Binds arguments for <c>CreateAgent</c> by parameter name, so the scenario keeps working when
    /// the factory grows an optional parameter but fails when it grows a required one it cannot
    /// supply - which is the point at which a human should look at this scenario again.
    /// </summary>
    private static object?[] BuildArguments(MethodInfo createAgent, string agentName)
    {
        ParameterInfo[] parameters = createAgent.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        for (int index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            arguments[index] = parameter.Name switch
            {
                "name" => agentName,
                "instructions" => "Extract, never interpret. Copy snippets verbatim.",
                _ when parameter.HasDefaultValue => parameter.DefaultValue,
                _ => throw new InvalidOperationException(
                    $"'CreateAgent' requires an unrecognised parameter '{parameter.Name}'; extend this scenario."),
            };
        }

        return arguments;
    }
}
