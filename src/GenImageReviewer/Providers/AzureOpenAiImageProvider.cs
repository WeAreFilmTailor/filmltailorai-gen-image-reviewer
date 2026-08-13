using System.ClientModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.AI.OpenAI;
using OpenAI.Images;

namespace GenImageReviewer.Providers;

// Handles Azure-hosted OpenAI image models (gpt-image-2, gpt-image-1.5, etc.)
// Each model is registered as a separate instance with its own deployment/quality config.
public class AzureOpenAiImageProvider : IImageGenerationProvider
{
    private static readonly HttpClient Http = new();

    private readonly ImageClient _client;
    private readonly string _endpoint;
    private readonly string _deployment;
    private readonly string _apiKey;
    private readonly string _lowQualityValue;   // "low" for gpt-image-2, "standard" for gpt-image-1.5
    private readonly ILogger<AzureOpenAiImageProvider> _logger;

    public string Name { get; }
    public string DisplayName { get; }

    public AzureOpenAiImageProvider(
        string name,
        string displayName,
        string endpoint,
        string deployment,
        string apiKey,
        string lowQualityValue,
        ILogger<AzureOpenAiImageProvider> logger)
    {
        Name = name;
        DisplayName = displayName;
        _endpoint = endpoint;
        _deployment = deployment;
        _apiKey = apiKey;
        _lowQualityValue = lowQualityValue;
        _logger = logger;

        var openAiClient = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey));
        _client = openAiClient.GetImageClient(deployment);
    }

    public async Task<GenerationResult> GenerateAsync(
        string prompt,
        GenerationSettings settings,
        byte[]? referenceImageData = null,
        string? referenceImageMimeType = null,
        CancellationToken ct = default)
    {
        if (referenceImageData is not null)
            return await EditWithReferenceAsync(prompt, settings, referenceImageData, referenceImageMimeType, ct);

        return await GenerateTextToImageAsync(prompt, settings, ct);
    }

    private async Task<GenerationResult> GenerateTextToImageAsync(
        string prompt, GenerationSettings settings, CancellationToken ct)
    {
        GeneratedImageQuality quality = settings.Quality switch
        {
            ImageQuality.Low  => _lowQualityValue,
            ImageQuality.High => "high",
            _                 => "medium",
        };

        _logger.LogDebug("[{Provider}] GenerateImageAsync (quality={Quality})", DisplayName, settings.Quality);

        var options = new ImageGenerationOptions
        {
            Size    = GeneratedImageSize.W1024xH1024,
            Quality = quality,
        };

        var result = await _client.GenerateImageAsync(prompt, options, ct);
        return await ImageToResultAsync(result.Value, ct);
    }

    // Calls the edit endpoint directly with api-version=2025-04-01-preview — the SDK uses
    // an older default API version that doesn't support gpt-image model edits.
    private async Task<GenerationResult> EditWithReferenceAsync(
        string prompt, GenerationSettings settings,
        byte[] referenceImageData, string? referenceImageMimeType,
        CancellationToken ct)
    {
        var quality = settings.Quality switch
        {
            ImageQuality.Low  => _lowQualityValue,
            ImageQuality.High => "high",
            _                 => "medium",
        };

        _logger.LogDebug("[{Provider}] images/edits (quality={Quality})", DisplayName, settings.Quality);

        var mime     = referenceImageMimeType ?? "image/png";
        var ext      = mime.Contains("jpeg") || mime.Contains("jpg") ? "jpg" : "png";
        var boundary = "FormBoundary" + Guid.NewGuid().ToString("N");
        var body     = BuildEditMultipart(boundary, prompt, quality, mime, ext, referenceImageData);

        var url = $"{_endpoint}openai/deployments/{_deployment}/images/edits?api-version=2025-04-01-preview";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
        request.Headers.Add("api-key", _apiKey);

        var response = await Http.SendAsync(request, ct);
        var json     = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[{Provider}] Edit HTTP {Status}: {Body}", DisplayName, (int)response.StatusCode, json);
            throw new InvalidOperationException($"{DisplayName} edit error {(int)response.StatusCode}: {json}");
        }

        var root    = JsonNode.Parse(json)!;
        var b64     = root["data"]?[0]?["b64_json"]?.GetValue<string>();
        if (b64 is not null) return new GenerationResult(Convert.FromBase64String(b64), "image/png");

        var imageUrl = root["data"]?[0]?["url"]?.GetValue<string>();
        if (imageUrl is not null)
        {
            var bytes = await Http.GetByteArrayAsync(imageUrl, ct);
            return new GenerationResult(bytes, "image/png");
        }

        return GenerationResult.Failure($"No image data in {DisplayName} edit response");
    }

    private static byte[] BuildEditMultipart(
        string boundary, string prompt, string quality,
        string mime, string ext, byte[] imageData)
    {
        var nl = "\r\n"u8.ToArray();
        using var ms = new MemoryStream();

        void TextField(string name, string value)
        {
            ms.Write(Encoding.UTF8.GetBytes($"--{boundary}\r\n"));
            ms.Write(Encoding.UTF8.GetBytes($"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n"));
            ms.Write(Encoding.UTF8.GetBytes(value));
            ms.Write(nl);
        }

        void FileField(string name, string filename, string contentType, byte[] data)
        {
            ms.Write(Encoding.UTF8.GetBytes($"--{boundary}\r\n"));
            ms.Write(Encoding.UTF8.GetBytes($"Content-Disposition: form-data; name=\"{name}\"; filename=\"{filename}\"\r\n"));
            ms.Write(Encoding.UTF8.GetBytes($"Content-Type: {contentType}\r\n\r\n"));
            ms.Write(data);
            ms.Write(nl);
        }

        FileField("image[]", $"reference.{ext}", mime, imageData);
        TextField("prompt", prompt);
        TextField("size", "1024x1024");
        TextField("n", "1");
        TextField("quality", quality);

        ms.Write(Encoding.UTF8.GetBytes($"--{boundary}--\r\n"));
        return ms.ToArray();
    }

    private async Task<GenerationResult> ImageToResultAsync(GeneratedImage image, CancellationToken ct)
    {
        if (image.ImageBytes is not null && image.ImageBytes.ToMemory().Length > 0)
        {
            _logger.LogDebug("[{Provider}] Got {Bytes} bytes from ImageBytes", DisplayName, image.ImageBytes.ToMemory().Length);
            return new GenerationResult(image.ImageBytes.ToArray(), "image/png");
        }

        if (image.ImageUri is not null)
        {
            _logger.LogDebug("[{Provider}] Downloading from URI {Uri}", DisplayName, image.ImageUri);
            var bytes = await Http.GetByteArrayAsync(image.ImageUri, ct);
            return new GenerationResult(bytes, "image/png");
        }

        _logger.LogWarning("[{Provider}] No image data in response", DisplayName);
        return GenerationResult.Failure($"No image data returned from {DisplayName}");
    }
}
