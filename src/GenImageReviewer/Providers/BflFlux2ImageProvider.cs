using System.Text.Json.Nodes;

namespace GenImageReviewer.Providers;

// Black Forest Labs FLUX.2 models via the native BFL API (api.bfl.ai), as opposed to the
// Azure Foundry-hosted FluxImageProvider. Endpoints: flux-2-pro, flux-2-flex, flux-2-max,
// flux-2-klein-4b, flux-2-klein-9b. Takes exact width/height (multiples of 16) and
// output_format. Reference images are ignored.
public class BflFlux2ImageProvider : IImageGenerationProvider
{
    private readonly string _submitUrl;
    private readonly JsonObject? _extraBody;
    private readonly string _apiKey;
    private readonly ILogger<BflFlux2ImageProvider> _logger;

    public string Name { get; }
    public string DisplayName { get; }

    public BflFlux2ImageProvider(
        string name,
        string displayName,
        string baseUrl,
        string apiKey,
        string endpoint,
        ILogger<BflFlux2ImageProvider> logger,
        JsonObject? extraBody = null)
    {
        Name = name;
        DisplayName = displayName;
        _apiKey = apiKey;
        _logger = logger;
        _extraBody = extraBody;
        _submitUrl = baseUrl.TrimEnd('/') + "/" + endpoint.TrimStart('/');
    }

    public Task<GenerationResult> GenerateAsync(
        string prompt,
        GenerationSettings settings,
        byte[]? referenceImageData = null,
        string? referenceImageMimeType = null,
        CancellationToken ct = default)
    {
        if (referenceImageData is not null)
            _logger.LogInformation("[{Provider}] Reference image not supported — generating from prompt only", DisplayName);

        var width  = RoundTo16(settings.Width);
        var height = RoundTo16(settings.Height);

        _logger.LogDebug("[{Provider}] POST {Url} ({W}x{H})", DisplayName, _submitUrl, width, height);

        var body = new JsonObject
        {
            ["prompt"]        = prompt,
            ["width"]         = width,
            ["height"]        = height,
            ["output_format"] = "png",
        };

        if (_extraBody is not null)
            foreach (var (key, value) in _extraBody)
                body[key] = value?.DeepClone();

        return BflTaskRunner.RunAsync(_submitUrl, _apiKey, body, DisplayName, _logger, ct);
    }

    private static int RoundTo16(int value) => Math.Max(64, (int)Math.Round(value / 16.0) * 16);
}
