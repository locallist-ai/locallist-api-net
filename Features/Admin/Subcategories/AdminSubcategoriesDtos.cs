using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Features.Admin.Subcategories;

public class CreateSubcategoryRequest
{
    [Required]
    [RegularExpression("^(Food|Nightlife|Coffee|Outdoors|Wellness|Culture|Shopping)$",
        ErrorMessage = "CategoryKey must be one of the 7 canonical categories.")]
    public required string CategoryKey { get; set; }

    [Required]
    [StringLength(80, MinimumLength = 1)]
    [RegularExpression("^[a-z0-9-]+$", ErrorMessage = "Key must be lowercase letters, digits, or hyphens.")]
    public required string Key { get; set; }

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public required string LabelEn { get; set; }

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public required string LabelEs { get; set; }
}

public class UpdateSubcategoryRequest
{
    [StringLength(120, MinimumLength = 1)]
    public string? LabelEn { get; set; }

    [StringLength(120, MinimumLength = 1)]
    public string? LabelEs { get; set; }
}
