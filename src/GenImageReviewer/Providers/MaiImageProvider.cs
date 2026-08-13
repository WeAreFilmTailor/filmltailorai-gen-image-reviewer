using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenImageReviewer.Providers;

// Microsoft AI Foundry image models: MAI-Image-2, MAI-Image-2e
// Endpoint: https://<resource>.services.ai.azure.com/mai/v1/images/generations
// JSON request, always returns b64_json PNG. No image editing support.
public class MaiImageProvider : IImageGenerationProvider
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _url;
    private readonly string _deployment;
    private readonly string _apiKey;
    private readonly ILogger<MaiImageProvider> _logger;

    public string Name { get; }
    public string DisplayName { get; }

    public MaiImageProvider(
        string name,
        string displayName,
        string baseEndpoint,
        string deployment,
        string apiKey,
        ILogger<MaiImageProvider> logger)
    {
        Name = name;
        DisplayName = displayName;
        _deployment = deployment;
        _apiKey = apiKey;
        _logger = logger;

        // Normalise trailing slash
        _url = baseEndpoint.TrimEnd('/') + "/mai/v1/images/generations";
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

        // Width and height must each be >= 768; total pixels <= 1,048,576
        var width  = Math.Max(768, settings.Width);
        var height = Math.Max(768, settings.Height);
        while ((long)width * height > 1_048_576)
        {
            width  = (int)(width  * 0.95);
            height = (int)(height * 0.95);
        }

        _logger.LogDebug("[{Provider}] POST {Url} ({W}x{H})", DisplayName, _url, width, height);

        var body = new JsonObject
        {
            ["model"]  = _deployment,
            ["prompt"] = prompt,
            ["width"]  = width,
            ["height"] = height,
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
        var b64  = root["data"]?[0]?["b64_json"]?.GetValue<string>();

        if (string.IsNullOrEmpty(b64))
        {
            _logger.LogWarning("[{Provider}] No b64_json in response: {Body}", DisplayName, json);
            return GenerationResult.Failure($"No image data returned from {DisplayName}");
        }

        var imageBytes = Convert.FromBase64String(b64);
        _logger.LogDebug("[{Provider}] Got {Bytes} bytes", DisplayName, imageBytes.Length);
        return new GenerationResult(imageBytes, "image/png");
    }
}
