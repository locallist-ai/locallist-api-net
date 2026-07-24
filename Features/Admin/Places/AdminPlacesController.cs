using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using LocalList.API.NET.Features.Places.Photos;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.AI.Services;
using ITaxonomySvc = LocalList.API.NET.Shared.Taxonomy.ITaxonomyService;

namespace LocalList.API.NET.Features.Admin.Places;

// AdminPlacesController is split across several partial files by responsibility. This split is
// purely structural (same class, same members, same behavior); it does NOT change any logic:
//   • AdminPlacesController.cs             — construction + shared field declarations
//   • AdminPlacesController.Reads.cs       — cities lookup + place list/detail reads
//   • AdminPlacesController.Google.cs      — Google Places search + admin-authed photo preview redirect
//   • AdminPlacesController.Crud.cs        — create/bulk-import/update/review/postpone/delete
//   • AdminPlacesController.Backfill.cs    — embeddings reindex + opening-hours/description backfills
//   • AdminPlacesController.Translation.cs — ES translation draft + batch translation + description suggestion
[ApiController]
[Route("admin/places")]
[AdminAuthorize]
[EnableRateLimiting("AdminLimit")]
public partial class AdminPlacesController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<AdminPlacesController> _logger;
    private readonly TimeProvider _clock;
    private readonly EmbeddingService _embeddings;
    private readonly IPlaceTranslatorService _translator;
    private readonly IDescriptionGeneratorService _descGen;
    private readonly IGooglePlacesService _googlePlaces;
    private readonly ITaxonomySvc _taxonomy;
    private readonly PlaceImportService _importSvc;
    private readonly IPlacePhotoService _photos;

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "draft", "in_review", "published", "rejected"
    };

    public AdminPlacesController(
        LocalListDbContext db,
        ILogger<AdminPlacesController> logger,
        TimeProvider clock,
        EmbeddingService embeddings,
        IPlaceTranslatorService translator,
        IDescriptionGeneratorService descGen,
        IGooglePlacesService googlePlaces,
        ITaxonomySvc taxonomy,
        PlaceImportService importSvc,
        IPlacePhotoService photos)
    {
        _db = db;
        _logger = logger;
        _clock = clock;
        _embeddings = embeddings;
        _translator = translator;
        _descGen = descGen;
        _googlePlaces = googlePlaces;
        _taxonomy = taxonomy;
        _importSvc = importSvc;
        _photos = photos;
    }
}
