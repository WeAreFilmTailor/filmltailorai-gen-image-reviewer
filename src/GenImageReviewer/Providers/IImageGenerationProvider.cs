namespace GenImageReviewer.Providers;

public interface IImageGenerationProvider
{
    string Name { get; }
    string DisplayName { get; }

    // False for providers that ignore the reference image and generate from the prompt only
    bool SupportsReferenceImage => true;

    Task<GenerationResult> GenerateAsync(
        string prompt,
        GenerationSettings settings,
        byte[]? referenceImageData = null,
        string? referenceImageMimeType = null,
        CancellationToken ct = default);
}
