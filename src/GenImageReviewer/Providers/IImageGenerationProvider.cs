namespace GenImageReviewer.Providers;

public interface IImageGenerationProvider
{
    string Name { get; }
    string DisplayName { get; }
    Task<GenerationResult> GenerateAsync(
        string prompt,
        GenerationSettings settings,
        byte[]? referenceImageData = null,
        string? referenceImageMimeType = null,
        CancellationToken ct = default);
}
