using System.Text.Json.Nodes;

namespace GenImageReviewer.Providers;

// Black Forest Labs FLUX.3 via the native BFL API (https://docs.bfl.ai/flux_3/flux3_image_generate)
// Async flow: POST /v1/<endpoint> -> poll `polling_url` -> download result.sample (signed URL, no auth header).
// Used for both flux-3-image and the faster flux-3-image-klein (see `endpoint` / `extraBody`).
// Takes aspect_ratio + resolution rather than width/height. A reference image is sent as base64 in
// `images`; the output then keeps the reference's own framing/size instead of the UI width/height.
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
    private readonly bool _isKlein;
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
        _isKlein = endpoint.Contains("klein", StringComparison.OrdinalIgnoreCase);
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
        JsonObject body;
        if (referenceImageData is not null)
        {
            // Edit/reference mode: let the reference set the output size and aspect.
            // flux-3-image takes resolution "match_input"; Klein defaults to the reference's size when omitted.
            _logger.LogDebug("[{Provider}] POST {Url} (reference image, {Bytes} bytes)", DisplayName, _submitUrl, referenceImageData.Length);
            body = new JsonObject
            {
                ["prompt"] = prompt,
                ["images"] = new JsonArray(Convert.ToBase64String(referenceImageData)),
            };
            if (!_isKlein) body["resolution"] = "match_input";
        }
        else
        {
            var aspect     = NearestAspectRatio(settings.Width, settings.Height);
            var resolution = Math.Max(settings.Width, settings.Height) <= 768 ? "768sq" : "1k";

            _logger.LogDebug("[{Provider}] POST {Url} (aspect {Aspect}, resolution {Res})", DisplayName, _submitUrl, aspect, resolution);

            body = new JsonObject
            {
                ["prompt"]       = prompt,
                ["aspect_ratio"] = aspect,
                ["resolution"]   = resolution,
            };
        }

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
