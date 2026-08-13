namespace GenImageReviewer.Providers;

public record GenerationResult(
    byte[]? ImageData,
    string MimeType = "image/png",
    bool Success = true,
    string? ErrorMessage = null
)
{
    public static GenerationResult Failure(string error) =>
        new(null, "image/png", false, error);
}
