namespace Cbix.Core.Documents;

/// <summary>
/// Supplies the <see cref="IDocumentContentProvider"/> profile that implements one presentation
/// capability, for one workflow run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an indirection rather than registering the providers directly.</b> The profiles are not
/// interchangeable to construct: the text-only and generic-vision profiles are Core types built from
/// Core services, while the native-document profile can only be created by a provider adapter that
/// holds a key-bearing client. Registering all three eagerly would mean Core naming an adapter, and
/// registering only the configured one would push the selection decision back into the host - which
/// is exactly the "no caller names a profile type" rule this abstraction exists to keep. A source is
/// the smallest thing that lets each side contribute what only it can build while the selection stays
/// data-driven.
/// </para>
/// <para>
/// <b>One instance per workflow run.</b> <see cref="Create"/> is invoked once per run scope, because
/// <see cref="IDocumentContentProvider"/> makes that lifetime part of its contract - the instance
/// carries the "upload or render each document once" memo, and a process-lifetime instance would
/// retain every run's uploaded files and rendered pixels for as long as the process lives. The
/// registration in <c>AddCbixWorkflow</c> is scoped for that reason, and a test asserts the scope.
/// </para>
/// </remarks>
public interface IDocumentContentProfileSource
{
    /// <summary>Gets the presentation capability this source's profile implements.</summary>
    /// <remarks>
    /// Two sources claiming the same capability is a composition error, not a precedence question:
    /// <see cref="DocumentContentProfileSelector"/> refuses rather than picking one, because either
    /// choice would be silent and one of them would be wrong.
    /// </remarks>
    DocumentPresentationCapability Capability { get; }

    /// <summary>Creates the profile for one workflow run.</summary>
    /// <param name="services">The run scope's service provider, for whatever the profile is built from.</param>
    /// <returns>
    /// The profile as the neutral port. Returning the port rather than a concrete profile is what
    /// stops a caller branching on which one is in play.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    IDocumentContentProvider Create(IServiceProvider services);
}
