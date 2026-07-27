namespace LocalList.API.NET.Features.Account;

/// <summary>
/// Configuración de utilidades SOLO-DEV/TEST. Bind desde la sección de config <c>Dev</c>.
///
/// SEGURIDAD: <see cref="TierOverrideEnabled"/> gobierna los endpoints <c>POST /account/dev/tier</c>
/// y <c>POST /account/dev/reset-quota</c>, que flipan el tier de FACTURACIÓN real / borran las cuotas
/// del propio usuario autenticado. El default es <b>false</b>. El gate NO depende ya del entorno: los
/// endpoints funcionan contra el backend de PRODUCCIÓN (Pablo testea contra Railway prod, donde están
/// los datos), pero SOLO para las cuentas cuyo email EXACTO esté en <see cref="AllowedEmails"/>. Doble
/// fail-closed: con el flag apagado O con el allowlist vacío el endpoint responde 404 opaco a TODOS
/// (como si no existiera). En Railway prod se abre explícitamente poniendo
/// <c>Dev__TierOverrideEnabled=true</c> + <c>Dev__AllowedEmails__0=&lt;email exacto&gt;</c>.
/// </summary>
public sealed class DevOptions
{
    public const string SectionName = "Dev";

    /// <summary>
    /// Gate primario (fail-closed) del override de tier / reset de cuota. Default <b>false</b>. Con
    /// false los endpoints <c>POST /account/dev/*</c> son 404 para cualquiera, sea cual sea el email.
    /// </summary>
    public bool TierOverrideEnabled { get; set; } = false;

    /// <summary>
    /// Allowlist de emails EXACTOS (case-insensitive, con trim) autorizados a usar los endpoints dev.
    /// Default VACÍO → segundo fail-closed: aunque el flag esté ON, un allowlist vacío deja el endpoint
    /// en 404 para todos. Match EXACTO (no por dominio): el email allowlisteado pertenece a una cuenta
    /// interna REAL — está tomado en <c>users.email</c> (índice único) y no hay endpoint de cambio de
    /// email sin verificar, así que un atacante no puede apropiárselo. En Railway se rellena por índice:
    /// <c>Dev__AllowedEmails__0=pablo@locallist.ai</c>.
    /// </summary>
    public string[] AllowedEmails { get; set; } = Array.Empty<string>();
}
