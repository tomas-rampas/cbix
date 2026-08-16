using System.Reflection;

using Cbix.Bdd.Support;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/DocumentContentProviderPort.feature</c> (story S01-04).
/// <para>
/// Everything here goes through reflection on names resolved at run time. That is
/// deliberate: the BDD working agreement requires the red phase to be a failing
/// assertion, and a direct compile-time reference to a type that does not exist yet
/// would instead break the build of the whole test project.
/// </para>
/// <para>
/// The neutrality check is an <b>assembly allowlist</b>, not a search for the string
/// "Anthropic". A denylist only catches the provider we already thought of; the allowlist fails
/// closed, so adding a Groq, OpenAI or Bedrock type to a Core signature breaks this scenario
/// without anyone remembering to extend it. It walks the property graph of every type the
/// signatures mention, so a provider type cannot hide one level in as a property of a returned
/// object either.
/// </para>
/// </summary>
[Binding]
public sealed class DocumentContentProviderPortSteps(DocumentContentProviderPortState state)
{
    /// <summary>Maximum depth walked through the property graph, for both the neutrality and capability searches.</summary>
    private const int MaxGraphDepth = 4;

    /// <summary>
    /// Assemblies a type in the port's surface may come from: the BCL, the provider-neutral
    /// Microsoft.Extensions.AI abstractions, and CBIX's own domain types. Anything else - including
    /// any <c>Microsoft.Agents.AI.*</c> provider integration - fails the scenario.
    /// </summary>
    private static readonly string[] AllowedAssemblies =
    [
        "Microsoft.Extensions.AI.Abstractions",
        "Cbix.Core",
        "mscorlib",
        "netstandard",
    ];

    [Given("the {string} interface in {string}")]
    public void GivenTheInterfaceIn(string interfaceName, string assemblyName)
    {
        Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));

        Type[] candidates = Array.FindAll(
            assembly.GetExportedTypes(),
            type => type.IsInterface && string.Equals(type.Name, interfaceName, StringComparison.Ordinal));

        Assert.True(
            candidates.Length > 0,
            $"No public interface named '{interfaceName}' exists in assembly '{assemblyName}'.");
        Assert.True(
            candidates.Length == 1,
            $"Assembly '{assemblyName}' declares {candidates.Length} public interfaces named '{interfaceName}'; the port must be unambiguous.");

        state.PortInterface = candidates[0];
    }

    [When("I inspect its method signatures")]
    public void WhenIInspectItsMethodSignatures()
    {
        Type port = state.PortInterface
            ?? throw new InvalidOperationException("The Given step did not resolve the port interface.");

        // Base interfaces count: a method inherited from another interface is just as much part of
        // the port's surface, and reflecting only the declaring type would miss it entirely.
        List<MethodInfo> methods = [.. port.GetMethods()];
        foreach (Type baseInterface in port.GetInterfaces())
        {
            methods.AddRange(baseInterface.GetMethods());
        }

        Assert.True(methods.Count > 0, $"'{port.FullName}' declares no methods, so it cannot be a usable port.");

        // The capability assertion below inspects one returned payload. Pinning the method count
        // keeps that honest: growing the port must be a deliberate act that also extends this
        // scenario, not a silent way to add an unchecked signature.
        Assert.True(
            methods.Count == 1,
            $"'{port.FullName}' now exposes {methods.Count} methods; extend this scenario to inspect every returned payload.");

        List<Type> signatureTypes = [];
        foreach (MethodInfo method in methods)
        {
            CollectTypes(method.ReturnType, signatureTypes);
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                CollectTypes(parameter.ParameterType, signatureTypes);
            }
        }

        state.SignatureTypes = signatureTypes;
        state.SurfaceTypes = ExpandPropertyGraph(signatureTypes);
        state.ReturnPayloadType = UnwrapAwaitable(methods[0].ReturnType);
    }

    [Then("no method references any Anthropic-specific type")]
    public void ThenNoMethodReferencesAnyAnthropicSpecificType()
    {
        Assert.NotEmpty(state.SignatureTypes);
        Assert.NotEmpty(state.SurfaceTypes);

        List<string> offenders = [];
        foreach (Type type in state.SurfaceTypes)
        {
            string assembly = type.Assembly.GetName().Name ?? string.Empty;
            if (!IsAllowedAssembly(assembly))
            {
                offenders.Add($"{type.FullName} (assembly {assembly})");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"The port's surface reaches types outside the allowed assemblies: {string.Join(", ", offenders)}. "
                + "Provider types belong inside a profile implementation, carried in AIContent.RawRepresentation.");
    }

    // The parentheses are escaped: in a Cucumber Expression bare parentheses mark optional
    // text, so an unescaped "(image)" would fail to match the literal step text.
    [Then("the return type carries a capability flag indicating whether visual \\(image\\) content is included")]
    public void ThenTheReturnTypeCarriesAVisualContentCapabilityFlag()
    {
        Type payload = state.ReturnPayloadType
            ?? throw new InvalidOperationException("The When step did not resolve the returned payload type.");

        Assert.True(
            TryFindVisualContentFlag(payload, 0, [], out _),
            $"The payload '{payload.FullName}' exposes no boolean capability flag reporting whether visual "
                + "(image) content is included; a caller cannot tell a visual profile from a degraded one.");
    }

    /// <summary>
    /// Adds <paramref name="type"/> and everything structurally reachable from it through
    /// generic arguments, array element types and by-ref element types to <paramref name="sink"/>.
    /// </summary>
    private static void CollectTypes(Type type, List<Type> sink)
    {
        if (sink.Contains(type))
        {
            return;
        }

        sink.Add(type);

        if (type.HasElementType)
        {
            Type? elementType = type.GetElementType();
            if (elementType is not null)
            {
                CollectTypes(elementType, sink);
            }
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            CollectTypes(argument, sink);
        }
    }

    /// <summary>
    /// Expands the signature types into the full surface a caller can reach: the public instance
    /// properties of every non-BCL type, transitively, to <see cref="MaxGraphDepth"/>.
    /// </summary>
    /// <remarks>
    /// BCL types are not descended into - their own properties cannot introduce a provider type,
    /// and walking them would explode the graph. A provider type held <em>inside</em> a BCL
    /// container is still caught, because <see cref="CollectTypes"/> walks generic arguments: a
    /// <c>List&lt;SomeProviderBlock&gt;</c> property contributes <c>SomeProviderBlock</c>.
    /// </remarks>
    private static IReadOnlyList<Type> ExpandPropertyGraph(IReadOnlyList<Type> roots)
    {
        List<Type> surface = [.. roots];
        Queue<(Type Type, int Depth)> pending = new();
        foreach (Type root in roots)
        {
            pending.Enqueue((root, 0));
        }

        while (pending.Count > 0)
        {
            (Type current, int depth) = pending.Dequeue();
            if (depth >= MaxGraphDepth || IsBclType(current))
            {
                continue;
            }

            foreach (PropertyInfo property in current.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                List<Type> propertyTypes = [];
                CollectTypes(property.PropertyType, propertyTypes);

                foreach (Type propertyType in propertyTypes)
                {
                    if (!surface.Contains(propertyType))
                    {
                        surface.Add(propertyType);
                        pending.Enqueue((propertyType, depth + 1));
                    }
                }
            }
        }

        return surface;
    }

    /// <summary>Unwraps <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c> to <c>T</c>; other types are returned unchanged.</summary>
    private static Type UnwrapAwaitable(Type returnType)
    {
        if (!returnType.IsGenericType)
        {
            return returnType;
        }

        string definition = returnType.GetGenericTypeDefinition().FullName ?? string.Empty;
        bool isAwaitable = definition.StartsWith("System.Threading.Tasks.Task`1", StringComparison.Ordinal)
            || definition.StartsWith("System.Threading.Tasks.ValueTask`1", StringComparison.Ordinal);

        return isAwaitable ? returnType.GetGenericArguments()[0] : returnType;
    }

    /// <summary>Reports whether an assembly is on the allowlist of assemblies the port's surface may reach.</summary>
    private static bool IsAllowedAssembly(string assemblyName)
    {
        if (assemblyName.StartsWith("System.", StringComparison.Ordinal)
            || string.Equals(assemblyName, "System", StringComparison.Ordinal))
        {
            return true;
        }

        return Array.Exists(AllowedAssemblies, allowed => string.Equals(allowed, assemblyName, StringComparison.Ordinal));
    }

    /// <summary>Reports whether a type ships with the BCL, and so cannot itself introduce a provider dependency.</summary>
    private static bool IsBclType(Type type)
    {
        string assembly = type.Assembly.GetName().Name ?? string.Empty;

        return assembly.StartsWith("System.", StringComparison.Ordinal)
            || string.Equals(assembly, "System", StringComparison.Ordinal)
            || string.Equals(assembly, "mscorlib", StringComparison.Ordinal)
            || string.Equals(assembly, "netstandard", StringComparison.Ordinal);
    }

    /// <summary>
    /// Searches <paramref name="type"/> and the types of its public instance properties for a
    /// boolean flag reporting whether visual content is included, returning the property path
    /// that satisfied the search.
    /// </summary>
    private static bool TryFindVisualContentFlag(Type type, int depth, HashSet<Type> visited, out string? path)
    {
        path = null;

        if (depth > MaxGraphDepth || !visited.Add(type))
        {
            return false;
        }

        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (PropertyInfo property in properties)
        {
            if (property.PropertyType == typeof(bool)
                && property.Name.Contains("Visual", StringComparison.OrdinalIgnoreCase))
            {
                path = property.Name;
                return true;
            }
        }

        foreach (PropertyInfo property in properties)
        {
            Type candidate = property.PropertyType;
            if (candidate.IsPrimitive || candidate == typeof(string) || IsBclType(candidate))
            {
                continue;
            }

            if (TryFindVisualContentFlag(candidate, depth + 1, visited, out string? nested))
            {
                path = $"{property.Name}.{nested}";
                return true;
            }
        }

        return false;
    }
}
