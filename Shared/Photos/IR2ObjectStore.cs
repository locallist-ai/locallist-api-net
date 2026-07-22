namespace LocalList.API.NET.Shared.Photos;

/// <summary>
/// Abstracción mínima sobre el bucket R2 (S3 API). Existe para que los tests puedan
/// sustituir el cliente S3 por un fake in-memory — la política del repo permite mockear
/// el cliente S3/R2 (la DB nunca).
/// </summary>
public interface IR2ObjectStore
{
    /// <summary>False cuando faltan credenciales R2 en config — los callers degradan graceful.</summary>
    bool IsConfigured { get; }

    /// <summary>Sube un objeto al bucket configurado (sobrescribe si ya existe — los keys son deterministas).</summary>
    Task UploadAsync(string key, byte[] content, string contentType, CancellationToken ct);
}
