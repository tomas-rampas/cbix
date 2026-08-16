using Cbix.Bdd.Support;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/BddHarness.feature</c> (story S01-02).
/// <para>
/// This class is the template every later CBIX story copies. The layout it
/// establishes: feature files under <c>Features/</c>, bindings under <c>Steps/</c>,
/// scenario-scoped state POCOs under <c>Support/</c>.
/// </para>
/// <para>
/// Scenario state is passed between steps by <b>context injection</b>: declare a
/// plain class (here <see cref="BddHarnessState"/>) and constructor-inject it.
/// Reqnroll builds one instance per scenario and shares it across every binding class
/// in that scenario. This is the sanctioned pattern for typed scenario state in this
/// repository; do not reach for string-keyed <c>ScenarioContext</c> entries, which
/// defeat compile-time checking and rename refactoring.
/// </para>
/// </summary>
[Binding]
public sealed class BddHarnessSteps(BddHarnessState state)
{
    [Given("the BDD harness is wired to xUnit")]
    public void GivenTheBddHarnessIsWiredToXUnit()
    {
        state.HarnessReady = true;
    }

    [When("the sample scenario runs")]
    public void WhenTheSampleScenarioRuns()
    {
        Assert.True(state.HarnessReady, "The Given step did not run before the When step.");

        state.ScenarioRan = true;
    }

    [Then("every step resolves to a binding")]
    public void ThenEveryStepResolvesToABinding()
    {
        Assert.True(state.ScenarioRan, "The When step did not run before the Then step.");
    }
}
