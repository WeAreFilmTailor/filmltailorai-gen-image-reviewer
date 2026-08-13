namespace GenImageReviewer.Providers;

public record GenerationSettings(
    int Width = 1024,
    int Height = 1024,
    string? NegativePrompt = null,
    ImageQuality Quality = ImageQuality.Medium
);

public enum ImageQuality { Low, Medium, High }
