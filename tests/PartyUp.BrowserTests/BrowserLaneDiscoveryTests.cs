using System.Reflection;

namespace PartyUp.BrowserTests;

/// <summary>
/// A-082's own two lane-shape assertions (PHASE5_DESIGN.md §11 "Browser lane (S1)"). Neither needs
/// <see cref="PlaywrightFixture"/> or a running stack — both are pure reflection over this compiled
/// assembly, which is why they carry no <c>[Trait("Category","Integration")]</c>: unlike every other
/// class here, they are meant to run and prove something even when the lane is excluded from
/// discovery (a solution-wide `dotnet test` still compiles and could run this assembly's own tests
/// under `-p:PartyUpBrowserTests=true` without CLAUDE.md's `Category!=Integration` filter narrowing
/// it further — see the second fact below for why that trait belongs on this class too, exactly
/// like every other one here: the `browser` CI job's two steps both filter *on*
/// `Category=Integration` (never against it), so a class lacking the trait would silently never run
/// in CI at all.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BrowserLaneDiscoveryTests
{
    /// <summary>
    /// A-082: "an assembly cannot observe an exclusion applied elsewhere" — this only proves the
    /// lane, once explicitly opted into, actually contains tests. The complementary half (that the
    /// solution-wide `dotnet` job discovers zero of them, and that `-p:PartyUpBrowserTests=true`
    /// discovers a non-zero count from *outside* this assembly) is `P5-BROWSER-01`'s CI-job
    /// assertion and this ticket's gate 2-4 evidence, not something this test can see from inside
    /// its own process.
    /// </summary>
    [Fact]
    public void LaneDiscoversANonZeroTestCount_A082()
    {
        Assembly assembly = typeof(BrowserLaneDiscoveryTests).Assembly;
        int testMethodCount = assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Count(method => method.GetCustomAttributes()
                .Any(attribute => attribute.GetType().FullName is "Xunit.FactAttribute" or "Xunit.TheoryAttribute"));

        Assert.True(testMethodCount > 0, "PartyUp.BrowserTests compiled with zero discoverable [Fact]/[Theory] methods.");
    }

    /// <summary>
    /// A-082: the trait and the `PartyUpBrowserTests` MSBuild property are two exclusions serving
    /// two lanes — the property keeps the assembly out of unfiltered solution-wide discovery
    /// (`ci.yml`'s `dotnet` job), the trait keeps it out of CLAUDE.md's
    /// `--filter "Category!=Integration"` on every OS (the property alone would not, since a caller
    /// could still opt the assembly in without also excluding it from that filter). Neither
    /// substitutes for the other, so every test class in this assembly must carry the trait
    /// regardless of which browser step (Chromium or WebKit) discovers it.
    /// </summary>
    [Fact]
    public void EveryBrowserTestClassCarriesTheIntegrationCategoryTrait_A082()
    {
        Assembly assembly = typeof(BrowserLaneDiscoveryTests).Assembly;
        IEnumerable<Type> testClasses = assembly.GetTypes()
            .Where(type => type.IsClass &&
                type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(method => method.GetCustomAttributes()
                        .Any(attribute => attribute.GetType().FullName is "Xunit.FactAttribute" or "Xunit.TheoryAttribute")));

        foreach (Type testClass in testClasses)
        {
            bool carriesTrait = testClass.GetCustomAttributesData()
                .Any(data => data.AttributeType.FullName == "Xunit.TraitAttribute" &&
                    data.ConstructorArguments.Count == 2 &&
                    (string?)data.ConstructorArguments[0].Value == "Category" &&
                    (string?)data.ConstructorArguments[1].Value == "Integration");

            Assert.True(carriesTrait, $"{testClass.FullName} is missing [Trait(\"Category\", \"Integration\")].");
        }
    }
}
