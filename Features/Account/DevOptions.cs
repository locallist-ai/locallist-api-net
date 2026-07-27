namespace LocalList.API.NET.Features.Account;

/// <summary>
/// Configuración de utilidades SOLO-DEV/TEST. Bind desde la sección de config <c>Dev</c>.
///
/// SEGURIDAD: <see cref="TierOverrideEnabled"/> gobierna el endpoint <c>POST /account/dev/tier</c>,
/// que flipa el tier de FACTURACIÓN real en la DB del propio usuario autenticado. El default es
/// <b>false</b> y así debe permanecer en Railway PROD — con el flag apagado el endpoint responde
/// 404 opaco a TODOS (incluido un @locallist.ai), como si no existiera. Solo se pone a true en
/// entornos de test/dev locales para poder ejercitar los flujos Plus contra los gates reales del
/// servidor (el "modo pro" de DevTools de la app es solo cliente y no engaña al backend).
/// NUNCA activar <c>Dev__TierOverrideEnabled=true</c> en el entorno de producción.
/// </summary>
public sealed class DevOptions
{
    public const string SectionName = "Dev";

    /// <summary>
    /// Gate primario (fail-closed) del override de tier. Default <b>false</b>. Con false el
    /// endpoint <c>POST /account/dev/tier</c> es 404 para cualquiera. NUNCA true en prod.
    /// </summary>
    public bool TierOverrideEnabled { get; set; } = false;
}
