using System.Text;
using System.Text.Json.Nodes;

namespace GenImageReviewer.Providers;

// Black Forest Labs FLUX.2 models hosted in Microsoft AI Foundry: FLUX.2-pro, FLUX.2-flex
// Endpoint is the full model URL, e.g.
//   https://<resource>.services.ai.azure.com/providers/blackforestlabs/v1/flux-2-pro?api-version=preview
// JSON request, returns b64_json. Reference images are not supported here.
public class FluxImageProvider : IImageGenerationProvider
{
    private static readonly HttpClient Http = new();

    private readonly string _url;
    private readonly string _deployment;
    private readonly string _apiKey;
    private readonly ILogger<FluxImageProvider> _logger;

    public string Name { get; }
    public string DisplayName { get; }

    public FluxImageProvider(
        string name,
        string displayName,
        string endpointUrl,
        string deployment,
        string apiKey,
        ILogger<FluxImageProvider> logger)
    {
        Name = name;
        DisplayName = displayName;
        _url = endpointUrl;
        _deployment = deployment;
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task<GenerationResult> GenerateAsync(
        string prompt,
        GenerationSettings settings,
        byte[]? referenceImageData = null,
        string? referenceImageMimeType = null,
        CancellationToken ct = default)
    {
        if (referenceImageData is not null)
            _logger.LogInformation("[{Provider}] Reference image not supported — generating from prompt only", DisplayName);

        // FLUX.2 requires dimensions that are multiples of 16
        var width  = RoundTo16(settings.Width);
        var height = RoundTo16(settings.Height);

        _logger.LogDebug("[{Provider}] POST {Url} ({W}x{H})", DisplayName, _url, width, height);

        var body = new JsonObject
        {
            ["model"]         = _deployment,
            ["prompt"]        = prompt,
            ["width"]         = width,
            ["height"]        = height,
            ["n"]             = 1,
            ["output_format"] = "png",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _url);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Add("api-key", _apiKey);

        var response = await Http.SendAsync(request, ct);
        var json     = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[{Provider}] HTTP {Status}: {Body}", DisplayName, (int)response.StatusCode, json);
            return GenerationResult.Failure($"{DisplayName} error {(int)response.StatusCode}: {json}");
        }

        var root = JsonNode.Parse(json)!;
        // "data" can be an empty array (e.g. stop_reason "refusal"), so don't index blindly
        var b64 = (root["data"] as JsonArray)?.FirstOrDefault()?["b64_json"]?.GetValue<string>();

        if (string.IsNullOrEmpty(b64))
        {
            var stopReason = root["stop_reason"]?.GetValue<string>();
            _logger.LogWarning("[{Provider}] No b64_json in response (stop_reason: {Stop}): {Body}",
                DisplayName, stopReason, json);
            return GenerationResult.Failure(stopReason == "refusal"
                ? $"{DisplayName} refused this prompt (content filter). Try rewording it."
                : $"No image data returned from {DisplayName}" + (stopReason is null ? "" : $" (stop_reason: {stopReason})"));
        }

        var imageBytes = Convert.FromBase64String(b64);
        _logger.LogDebug("[{Provider}] Got {Bytes} bytes", DisplayName, imageBytes.Length);
        return new GenerationResult(imageBytes, "image/png");
    }

    private static int RoundTo16(int value) => Math.Max(64, (int)Math.Round(value / 16.0) * 16);
}
