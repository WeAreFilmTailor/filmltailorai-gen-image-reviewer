namespace GenImageReviewer.Data;

public class ComparisonRun
{
    public int Id { get; set; }
    public string Prompt { get; set; } = "";
    public string? NegativePrompt { get; set; }
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public string Quality { get; set; } = "Medium";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public byte[]? ReferenceImageData { get; set; }
    public string? ReferenceImageMimeType { get; set; }

    public List<ComparisonResult> Results { get; set; } = [];
}
