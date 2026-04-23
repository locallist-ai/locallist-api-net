using LocalList.API.NET.Features.Builder;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// Unit tests del helper puro <see cref="BuilderController.BuildPlanName"/>.
///
/// Problema que se resuelve: Gemini (o el fallback keyword) a veces pone el mensaje
/// crudo del usuario como <c>PlanName</c>. Resultado: "Hola" como nombre del plan,
/// o "make me a plan" — ruido. El helper detecta esos casos y sintetiza un nombre
/// descriptivo a partir de ciudad + duración + vibe.
/// </summary>
public class BuilderPlanNameTests
{
    [Fact]
    public void BuildPlanName_GreetingMessage_SynthesizesDescriptive()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 2,
            Vibes = new List<string> { "adventure" },
            Categories = new List<string> { "outdoors", "culture" },
            PlanName = "Hola",  // Gemini lo copió literal
            GroupType = "family-kids"
        };

        var name = BuilderController.BuildPlanName(prefs, "Miami", "Hola");

        Assert.DoesNotContain("Hola", name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Miami", name);
        Assert.Contains("2-day", name);
    }

    [Fact]
    public void BuildPlanName_EmptyPlanName_SynthesizesDescriptive()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 1,
            Vibes = new List<string> { "relax" },
            PlanName = "",
        };

        var name = BuilderController.BuildPlanName(prefs, "Miami", "quiero algo relajado");

        Assert.Contains("Miami", name);
        Assert.Contains("1-day", name);
        Assert.Contains("relax", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPlanName_ContainsRawMessage_SynthesizesDescriptive()
    {
        // Caso típico: Gemini regurgita el mensaje dentro de PlanName.
        var prefs = new ExtractedPreferences
        {
            Days = 3,
            Categories = new List<string> { "food" },
            PlanName = "make me a plan please",
        };

        var name = BuilderController.BuildPlanName(prefs, "Miami", "make me a plan");

        Assert.DoesNotContain("make me a plan", name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3-day", name);
    }

    [Fact]
    public void BuildPlanName_LiteralMyPlan_SynthesizesDescriptive()
    {
        // Caso real observado en prod 2026-04-23: Gemini devuelve planName="My Plan"
        // (default de ExtractedPreferences). Sin el fix, pasaba IsUsableName y ese
        // nombre ruin llegaba al cliente.
        var prefs = new ExtractedPreferences
        {
            Days = 2,
            Vibes = new List<string> { "cultural" },
            PlanName = "My Plan",
        };

        var name = BuilderController.BuildPlanName(prefs, "Miami", "some message");

        Assert.NotEqual("My Plan", name);
        Assert.Contains("Miami", name);
        Assert.Contains("2-day", name);
    }

    [Fact]
    public void BuildPlanName_UsableDescriptive_PassesThrough()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 2,
            Vibes = new List<string> { "romantic" },
            PlanName = "Romantic Miami Weekend",
        };

        var name = BuilderController.BuildPlanName(prefs, "Miami", "romantic dinner ideas");

        Assert.Equal("Romantic Miami Weekend", name);
    }

    [Fact]
    public void BuildPlanName_NoCityNoVibes_FallsBackToCurated()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 1,
            PlanName = "hi there",
        };

        var name = BuilderController.BuildPlanName(prefs, "", "hi");

        // Default a Miami + "curated" cuando no hay señales.
        Assert.Contains("Miami", name);
        Assert.Contains("curated", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPlanDescription_WithCategories_ListstsTopThree()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 2,
            GroupType = "family-kids",
            Categories = new List<string> { "outdoors", "culture", "food", "coffee" },
        };

        var desc = BuilderController.BuildPlanDescription(prefs);

        Assert.Contains("family-kids-friendly", desc);
        Assert.Contains("2-day", desc);
        Assert.Contains("outdoors", desc);
        Assert.Contains("culture", desc);
        Assert.Contains("food", desc);
        Assert.DoesNotContain("coffee", desc); // cuarta queda fuera
    }

    [Fact]
    public void BuildPlanDescription_EmptyCategories_ShortForm()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 1,
            GroupType = "solo",
            Categories = new List<string>(),
        };

        var desc = BuilderController.BuildPlanDescription(prefs);

        Assert.Contains("solo-friendly", desc);
        Assert.Contains("1-day", desc);
        Assert.DoesNotContain("featuring", desc);
    }
}
