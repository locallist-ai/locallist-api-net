using LocalList.API.NET.Features.Builder;
using LocalList.API.NET.Shared.Dtos;

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

    // ── Fallback bilingue (lang="es"): el nombre/descripcion sintetizados se persisten bajo
    // ── NameI18n["es"]. Con el template siempre-EN, un titulo ingles quedaba etiquetado como
    // ── espanol. Los inputs son los tokens CANONICOS EN que produce el pipeline de verdad
    // ── (vibes del prompt/slot extractor, PlaceTaxonomy en lowercase, AllowedGroupTypes) —
    // ── no espanol pre-cocinado que el extractor jamas emite. Se aserta la traduccion.

    [Fact]
    public void BuildPlanName_LangEs_CanonicalVibe_TranslatedAndPostponed()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 2,
            Vibes = new List<string> { "romantic" }, // token canonico del pipeline
            PlanName = "Hola", // se rechaza -> fallback sintetizado
        };

        var name = BuilderController.BuildPlanName(prefs, "Miami", "Hola", "es");

        // Adjetivo traducido y pospuesto — ni "romantic" verbatim ni "de romántico".
        Assert.Equal("Plan de 2 días romántico en Miami", name);
        Assert.DoesNotContain("romantic ", name);
        Assert.DoesNotContain("-day", name);
        Assert.DoesNotContain("plan in", name);
    }

    [Fact]
    public void BuildPlanName_LangEs_CanonicalCategory_TranslatedWithSingularDay()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 1,
            Categories = new List<string> { "food" }, // categoria canonica lowercase
            PlanName = "hi",
        };

        var name = BuilderController.BuildPlanName(prefs, "Sevilla", "hi", "es");

        Assert.Equal("Plan de 1 día de gastronomía en Sevilla", name);
        Assert.DoesNotContain("food", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPlanName_LangEs_CategoryTokenInsideVibes_StillTranslated()
    {
        // El keyword-fallback del extractor copia categorias dentro de Vibes
        // (PreferenceExtractorService.ExtractWithKeywords) — el diccionario del
        // nombre debe resolverlas igualmente.
        var prefs = new ExtractedPreferences
        {
            Days = 2,
            Vibes = new List<string> { "outdoors" },
            PlanName = "hola",
        };

        var name = BuilderController.BuildPlanName(prefs, "Miami", "hola", "es");

        Assert.Equal("Plan de 2 días al aire libre en Miami", name);
    }

    [Fact]
    public void BuildPlanName_LangEs_UnknownToken_OmittedNeverEnglish()
    {
        // Regla: token fuera del set canonico se OMITE con gracia — nunca se
        // interpola ingles dentro de la frase espanola.
        var prefs = new ExtractedPreferences
        {
            Days = 2,
            Vibes = new List<string> { "zen-vibes" },
            Categories = new List<string> { "speakeasy" },
            PlanName = "Hola",
        };

        var name = BuilderController.BuildPlanName(prefs, "Miami", "Hola", "es");

        Assert.Equal("Plan a medida de 2 días en Miami", name);
        Assert.DoesNotContain("zen-vibes", name);
        Assert.DoesNotContain("speakeasy", name);
    }

    [Fact]
    public void BuildPlanName_LangEs_NoSignals_FallsBackToSpanishGeneric()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 3,
            PlanName = "hi there",
        };

        var name = BuilderController.BuildPlanName(prefs, "", "hi", "es");

        Assert.Equal("Plan a medida de 3 días en Miami", name);
        Assert.DoesNotContain("curated", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPlanName_LangEn_Unchanged()
    {
        // Regresion: el camino EN no debe alterarse con el nuevo parametro lang.
        var prefs = new ExtractedPreferences
        {
            Days = 2,
            Vibes = new List<string> { "food" },
            PlanName = "Hola",
        };

        Assert.Equal("2-day food plan in Miami", BuilderController.BuildPlanName(prefs, "Miami", "Hola", "en"));
        // Default lang == "en".
        Assert.Equal("2-day food plan in Miami", BuilderController.BuildPlanName(prefs, "Miami", "Hola"));
    }

    [Fact]
    public void BuildPlanDescription_LangEs_CanonicalTokens_FullyTranslated()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 2,
            GroupType = "couple", // token canonico de AllowedGroupTypes
            Categories = new List<string> { "food", "culture", "outdoors", "coffee" },
        };

        var desc = BuilderController.BuildPlanDescription(prefs, "es");

        Assert.Equal("Un plan de 2 días en pareja con gastronomía, cultura, aire libre.", desc);
        Assert.DoesNotContain("couple", desc);
        Assert.DoesNotContain("food", desc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cafés", desc); // cuarta categoria queda fuera del top-3
        Assert.DoesNotContain("friendly", desc);
        Assert.DoesNotContain("featuring", desc);
    }

    [Fact]
    public void BuildPlanDescription_LangEs_EmptyCategories_ShortForm()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 1,
            GroupType = "solo",
            Categories = new List<string>(),
        };

        var desc = BuilderController.BuildPlanDescription(prefs, "es");

        Assert.Equal("Un plan de 1 día en solitario.", desc);
    }

    [Fact]
    public void BuildPlanDescription_LangEs_UnknownTokens_OmittedNeverEnglish()
    {
        // GroupType fuera del set cerrado -> clausula omitida; categoria no mapeada
        // -> descartada de la lista. Nada de ingles verbatim en la frase.
        var prefs = new ExtractedPreferences
        {
            Days = 2,
            GroupType = "squad",
            Categories = new List<string> { "food", "bizarre-stuff" },
        };

        var desc = BuilderController.BuildPlanDescription(prefs, "es");

        Assert.Equal("Un plan de 2 días con gastronomía.", desc);
        Assert.DoesNotContain("squad", desc);
        Assert.DoesNotContain("bizarre", desc);
    }

    [Fact]
    public void BuildPlanDescription_LangEs_AllTokensUnknown_GenericSentence()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 3,
            GroupType = "crew",
            Categories = new List<string> { "mystery" },
        };

        var desc = BuilderController.BuildPlanDescription(prefs, "es");

        Assert.Equal("Un plan de 3 días.", desc);
    }

    [Fact]
    public void BuildPlanDescription_LangEn_Unchanged()
    {
        var prefs = new ExtractedPreferences
        {
            Days = 2,
            GroupType = "family-kids",
            Categories = new List<string> { "outdoors", "culture" },
        };

        Assert.Equal(
            "A family-kids-friendly 2-day plan featuring outdoors, culture.",
            BuilderController.BuildPlanDescription(prefs, "en"));
        Assert.Equal(
            "A family-kids-friendly 2-day plan featuring outdoors, culture.",
            BuilderController.BuildPlanDescription(prefs));
    }
}
