namespace GenImageReviewer.Data;

public class ComparisonResult
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public ComparisonRun Run { get; set; } = null!;

    public int Round { get; set; } = 1;

    public string ProviderName { get; set; } = "";
    public string ProviderDisplayName { get; set; } = "";

    public byte[]? ImageData { get; set; }
    public string MimeType { get; set; } = "image/png";

    public bool GenerationSuccess { get; set; }
    public string? GenerationError { get; set; }
    public long GenerationDurationMs { get; set; }

    public bool ReviewCompleted { get; set; }
    public int? AccuracyScore { get; set; }
    public int? QualityScore { get; set; }
    public int? OverallScore { get; set; }
    public int? ReferenceAccuracyScore { get; set; }
    public string? DefectsJson { get; set; }
    public string? Commentary { get; set; }
    public string? ReviewError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
