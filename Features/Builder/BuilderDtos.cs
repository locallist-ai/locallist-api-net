using System.ComponentModel.DataAnnotations;
using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.NET.Features.Builder;

public class BuilderChatRequest
{
    // Message opcional (Pablo 2026-04-23): el chat complementa al wizard pero no lo sustituye.
    // Si el user no escribe nada, el wizard debe tener mínimo 3/5 señales para generar plan.
    // Si el user escribe algo, se pasa tal cual al pipeline (Gemini + embedding query).
    [MaxLength(5000)]
    public string? Message { get; set; }
    public TripContextDto? TripContext { get; set; }
}
