using System.Text.Json.Nodes;

namespace GenImageReviewer.Providers;

// Black Forest Labs FLUX.3 via the native BFL API (https://docs.bfl.ai/flux_3/flux3_image_generate)
// Async flow: POST /v1/<endpoint> -> poll `polling_url` -> download result.sample (signed URL, no auth header).
// Used for both flux-3-image and the faster flux-3-image-klein (see `endpoint` / `extraBody`).
// Takes aspect_ratio + resolution rather than width/height. Reference images are ignored
// (flux-3-image only accepts 2-10 of them).
public class Flux3ImageProvider : IImageGenerationProvider
{
    private static readonly (string Label, double Ratio)[] AspectRatios =
        new[]
        {
            "21:9", "2:1", "16:9", "3:2", "7:5", "4:3", "5:4", "1:1",
            "4:5", "3:4", "5:7", "2:3", "9:16", "1:2", "9:21",
        }.Select(s => { var p = s.Split(':'); return (s, double.Parse(p[0]) / double.Parse(p[1])); }).ToArray();

    private readonly string _submitUrl;
    private readonly JsonObject? _extraBody;
    private readonly string _apiKey;
    private readonly ILogger<Flux3ImageProvider> _logger;

    public string Name { get; }
    public string DisplayName { get; }

    public Flux3ImageProvider(
        string name,
        string displayName,
        string baseUrl,
        string apiKey,
        ILogger<Flux3ImageProvider> logger,
        string endpoint = "v1/flux-3-image",
        JsonObject? extraBody = null)
    {
        _extraBody = extraBody;
        Name = name;
        DisplayName = displayName;
        _apiKey = apiKey;
        _logger = logger;
        _submitUrl = baseUrl.TrimEnd('/') + "/" + endpoint.TrimStart('/');
    }

    public async Task<GenerationResult> GenerateAsync(
        string prompt,
        GenerationSettings settings,
        byte[]? referenceImageData = null,
        string? referenceImageMimeType = null,
        CancellationToken ct = default)
    {
        if (referenceImageData is not null)
            _logger.LogInformation("[{Provider}] Single reference image not supported — generating from prompt only", DisplayName);

        var aspect     = NearestAspectRatio(settings.Width, settings.Height);
        var resolution = Math.Max(settings.Width, settings.Height) <= 768 ? "768sq" : "1k";

        _logger.LogDebug("[{Provider}] POST {Url} (aspect {Aspect}, resolution {Res})", DisplayName, _submitUrl, aspect, resolution);

        var body = new JsonObject
        {
            ["prompt"]       = prompt,
            ["aspect_ratio"] = aspect,
            ["resolution"]   = resolution,
        };

        // Variant-specific fields (e.g. Klein: output_format, steps)
        if (_extraBody is not null)
            foreach (var (key, value) in _extraBody)
                body[key] = value?.DeepClone();

        return await BflTaskRunner.RunAsync(_submitUrl, _apiKey, body, DisplayName, _logger, ct);
    }

    private static string NearestAspectRatio(int width, int height)
    {
        var target = (double)width / Math.Max(1, height);
        return AspectRatios.MinBy(a => Math.Abs(Math.Log(a.Ratio / target))).Label;
    }
}
