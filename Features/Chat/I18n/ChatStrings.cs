namespace LocalList.API.NET.Features.Chat.I18n;

/// <summary>
/// All user-facing canned strings for the chat flow, keyed by ISO language code.
/// Add new languages by extending each switch. Gemini-generated replies are localized
/// via a prompt directive in SlotExtractorService — only these backend-originated
/// strings need manual translations.
/// </summary>
public static class ChatStrings
{
    public static string ParseFallback(string lang) => lang switch
    {
        "es" => "Perdona, no te he entendido. ¿Puedes reformularlo?",
        _    => "Sorry, I didn't catch that. Could you rephrase?"
    };

    public static string OffTopicRedirect(string lang, string nextQuestion) => lang switch
    {
        "es" => $"Centrémonos en tu viaje — {nextQuestion}",
        _    => $"Let's focus on your trip — {nextQuestion}"
    };

    public static string InjectionRedirect(string lang) => lang switch
    {
        "es" => "Solo puedo ayudarte a planificar tu viaje. ¿A dónde vas?",
        _    => "I can only help plan your trip. Where are you headed?"
    };

    public static string ChipForgeryReject(string lang) => lang switch
    {
        "es" => "Por favor, selecciona una de las opciones disponibles.",
        _    => "Please select one of the available options."
    };

    public static string ReadyToBuild(string lang) => lang switch
    {
        "es" => "¡Listo para crear tu plan!",
        _    => "Ready to build your plan!"
    };

    public static string GotItPrefix(string lang, string nextQuestion) => lang switch
    {
        "es" => $"¡Entendido! {nextQuestion}",
        _    => $"Got it! {nextQuestion}"
    };

    public static string GreetingNoCity(string lang) => lang switch
    {
        "es" => "¿A qué ciudad vas?",
        _    => "What city are you visiting?"
    };

    public static string GreetingWithCity(string lang, string city) => lang switch
    {
        "es" => $"¡Perfecto, planificamos tu viaje a {city}! ¿Cuántos días?",
        _    => $"Great — let's plan your {city} trip! How many days?"
    };

    public static string Quarantine(string lang) => lang switch
    {
        "es" => "Esta conversación ha sido reiniciada por seguridad. Prueba el asistente paso a paso.",
        _    => "This conversation was reset for safety. Please try the wizard instead."
    };

    public static string SwitchedCity(string lang, string city) => lang switch
    {
        "es" => $"Cambiado a {city}.",
        _    => $"Switched city to {city}."
    };

    public static string SwitchedDays(string lang, int days) => lang switch
    {
        "es" => $"Cambiado a {days} días.",
        _    => $"Switched to {days} days."
    };

    public static string SwitchedGroupType(string lang, string value) => lang switch
    {
        "es" => $"Cambiado a {value}.",
        _    => $"Switched to {value}."
    };

    public static string SwitchedBudget(string lang, string value) => lang switch
    {
        "es" => $"Cambiado el presupuesto a {value}.",
        _    => $"Switched budget to {value}."
    };

    public static string NextSlotQuestion(string slotKey, string lang) => (slotKey, lang) switch
    {
        ("city",       "es") => "¿A dónde vas?",
        ("city",        _  ) => "Where are you headed?",
        ("days",       "es") => "¿Cuántos días?",
        ("days",        _  ) => "How many days?",
        ("groupType",  "es") => "¿Con quién viajas?",
        ("groupType",   _  ) => "Who are you travelling with?",
        ("categories", "es") => "¿Qué te gusta? (gastronomía, cultura, aire libre...)",
        ("categories",  _  ) => "What do you enjoy? (food, culture, outdoors...)",
        ("budget",     "es") => "¿Cuál es tu nivel de presupuesto?",
        ("budget",      _  ) => "What's your budget vibe?",
        (_,            "es") => "¡Listo para construir!",
        _                    => "Ready to build!"
    };

    public static string Tier2Question(string lang) => lang switch
    {
        "es" => "Un último detalle — ¿tienes alguna restricción alimentaria o preferencia de ritmo?",
        _    => "One last touch — any dietary restrictions, and what pace?"
    };

    public static List<ChatQuickReply> Tier2Chips(string lang) => lang switch
    {
        "es" => new()
        {
            new() { Id = "diet_vegetarian", Label = "🥗 Vegetariano" },
            new() { Id = "diet_none",        Label = "✅ Sin restricciones" },
            new() { Id = "pace_slow",        Label = "🐢 Ritmo tranquilo" },
            new() { Id = "skip_refinements", Label = "⏭️ Omitir" },
        },
        _ => new()
        {
            new() { Id = "diet_vegetarian", Label = "🥗 Vegetarian" },
            new() { Id = "diet_none",        Label = "✅ No restrictions" },
            new() { Id = "pace_slow",        Label = "🐢 Slow pace" },
            new() { Id = "skip_refinements", Label = "⏭️ Skip" },
        }
    };

    public static List<ChatQuickReply> GreetingDayChips(string lang) => lang switch
    {
        "es" => new()
        {
            new() { Id = "days_1", Label = "1 día" },
            new() { Id = "days_2", Label = "2 días" },
            new() { Id = "days_3", Label = "3 días" },
            new() { Id = "days_4", Label = "4 días" },
            new() { Id = "days_7", Label = "1 semana" },
        },
        _ => new()
        {
            new() { Id = "days_1", Label = "1 day" },
            new() { Id = "days_2", Label = "2 days" },
            new() { Id = "days_3", Label = "3 days" },
            new() { Id = "days_4", Label = "4 days" },
            new() { Id = "days_7", Label = "1 week" },
        }
    };

    public static List<ChatQuickReply> QuickRepliesForSlot(string slot, string lang) => slot switch
    {
        "budget" => new()
        {
            new() { Id = "budget_budget",   Label = lang == "es" ? "💸 Ajustado"    : "💸 Tight" },
            new() { Id = "budget_moderate", Label = lang == "es" ? "🍽️ Cómodo"      : "🍽️ Comfortable" },
            new() { Id = "budget_premium",  Label = lang == "es" ? "✨ Premium"     : "✨ Splurge" },
        },
        "groupType" => new()
        {
            new() { Id = "group_solo",    Label = lang == "es" ? "🧍 Solo/a"       : "🧍 Solo" },
            new() { Id = "group_couple",  Label = lang == "es" ? "👫 Pareja"       : "👫 Couple" },
            new() { Id = "group_friends", Label = lang == "es" ? "👯 Amigos"       : "👯 Friends" },
            new() { Id = "group_family",  Label = lang == "es" ? "👨‍👩‍👧 Familia"     : "👨‍👩‍👧 Family" },
        },
        "days" => new()
        {
            new() { Id = "days_1", Label = lang == "es" ? "1 día"    : "1 day" },
            new() { Id = "days_2", Label = lang == "es" ? "2 días"   : "2 days" },
            new() { Id = "days_3", Label = lang == "es" ? "3 días"   : "3 days" },
            new() { Id = "days_4", Label = lang == "es" ? "4+ días"  : "4+ days" },
        },
        _ => new()
    };
}
