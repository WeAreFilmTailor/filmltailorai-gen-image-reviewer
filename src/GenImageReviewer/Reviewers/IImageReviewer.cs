namespace GenImageReviewer.Reviewers;

public interface IImageReviewer
{
    Task<ReviewScore> ReviewAsync(
        string prompt,
        byte[] imageData,
        string mimeType,
        byte[]? referenceImageData = null,
        string? referenceImageMimeType = null,
        CancellationToken ct = default);
}
