using System.Net.Http.Headers;
using System.Text;

namespace GenImageReviewer.Providers;

public class StabilityAiProvider : IImageGenerationProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<StabilityAiProvider> _logger;

    public string Name { get; }
    public string DisplayName { get; }

    // model: "core" | "ultra" | "sd3" — maps to v2beta/stable-image/generate/{model}
    public StabilityAiProvider(HttpClient http, string name, string displayName, string model,
        ILogger<StabilityAiProvider> logger)
    {
        _http = http;
        _model = model;
        _logger = logger;
        Name = name;
        DisplayName = displayName;
    }

    public async Task<GenerationResult> GenerateAsync(
        string prompt,
        GenerationSettings settings,
        byte[]? referenceImageData = null,
        string? referenceImageMimeType = null,
        CancellationToken ct = default)
    {
        var endpoint = $"v2beta/stable-image/generate/{_model}";
        _logger.LogDebug("[{Provider}] POST {Endpoint} (reference: {HasRef})", DisplayName, endpoint, referenceImageData is not null);

        var boundary = "FormBoundary" + Guid.NewGuid().ToString("N");
        var bodyBytes = BuildMultipart(boundary, prompt, settings, referenceImageData, referenceImageMimeType);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));

        var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("[{Provider}] HTTP {Status}: {Error}", DisplayName, (int)response.StatusCode, error);
            return GenerationResult.Failure($"Stability AI error {(int)response.StatusCode}: {error}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var mime = response.Content.Headers.ContentType?.MediaType ?? "image/png";
        _logger.LogDebug("[{Provider}] Response {Status}, {Bytes} bytes", DisplayName, (int)response.StatusCode, bytes.Length);
        return new GenerationResult(bytes, mime);
    }

    private static byte[] BuildMultipart(string boundary, string prompt, GenerationSettings settings,
        byte[]? referenceImage, string? referenceMime)
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

        TextField("prompt", prompt);
        TextField("output_format", "png");

        if (!string.IsNullOrWhiteSpace(settings.NegativePrompt))
            TextField("negative_prompt", settings.NegativePrompt);

        if (referenceImage is not null)
        {
            var mime = referenceMime ?? "image/png";
            var ext = mime.Contains("jpeg") || mime.Contains("jpg") ? "jpg" : "png";
            FileField("image", $"reference.{ext}", mime, referenceImage);
            // strength: how much the reference guides the output (lower = closer to reference)
            TextField("strength", "0.35");
        }

        ms.Write(Encoding.UTF8.GetBytes($"--{boundary}--\r\n"));
        return ms.ToArray();
    }
}
