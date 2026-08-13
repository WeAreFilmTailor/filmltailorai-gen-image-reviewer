using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace GenImageReviewer.Reviewers;

// Uses the Anthropic Messages API directly via HttpClient so the reviewer
// is not coupled to any specific version of the Anthropic.SDK NuGet package.
public class ClaudeReviewer : IImageReviewer
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<ClaudeReviewer> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public ClaudeReviewer(HttpClient http, ILogger<ClaudeReviewer> logger, string model = "claude-sonnet-4-6")
    {
        _http = http;
        _logger = logger;
        _model = model;
    }

    public async Task<ReviewScore> ReviewAsync(
        string prompt,
        byte[] imageData,
        string mimeType,
        byte[]? referenceImageData = null,
        string? referenceImageMimeType = null,
        CancellationToken ct = default)
    {
        (imageData, mimeType) = ShrinkIfNeeded(imageData, mimeType);
        _logger.LogDebug("Sending {Bytes}-byte image to Claude ({Model}) for review (reference: {HasRef})",
            imageData.Length, _model, referenceImageData is not null);

        var hasReference = referenceImageData is not null;

        var systemPrompt = hasReference
            ? """
              You are an expert image quality assessor for AI-generated images.
              You will be given a reference image followed by an AI-generated image.
              Evaluate the generated image on three dimensions:
              1. Accuracy — how faithfully the image matches the text prompt
              2. Quality — technical quality, anatomy, coherence, absence of artefacts
              3. ReferenceAccuracy — how closely the generated image matches the reference image in style, composition, and content

              You MUST respond with valid JSON matching this exact schema and nothing else:
              {
                "accuracyScore": <integer 1-10>,
                "qualityScore": <integer 1-10>,
                "overallScore": <integer 1-10>,
                "referenceAccuracyScore": <integer 1-10>,
                "defects": ["<defect description>"],
                "commentary": "<concise paragraph>"
              }

              Defects include: missing limbs, extra fingers, distorted faces, text errors,
              wrong object counts, incoherent backgrounds, anatomical errors, blurriness,
              compression artefacts, prompt elements not rendered, etc.
              If there are no defects, return an empty array.
              """
            : """
              You are an expert image quality assessor for AI-generated images.
              You evaluate images on two dimensions:
              1. Accuracy — how faithfully the image matches the text prompt
              2. Quality — technical quality, anatomy, coherence, absence of artefacts

              You MUST respond with valid JSON matching this exact schema and nothing else:
              {
                "accuracyScore": <integer 1-10>,
                "qualityScore": <integer 1-10>,
                "overallScore": <integer 1-10>,
                "defects": ["<defect description>"],
                "commentary": "<concise paragraph>"
              }

              Defects include: missing limbs, extra fingers, distorted faces, text errors,
              wrong object counts, incoherent backgrounds, anatomical errors, blurriness,
              compression artefacts, prompt elements not rendered, etc.
              If there are no defects, return an empty array.
              """;

        var userContent = new JsonArray();

        if (hasReference)
        {
            var (refData, refMime) = ShrinkIfNeeded(referenceImageData!, referenceImageMimeType ?? "image/png");
            userContent.Add(new JsonObject { ["type"] = "text", ["text"] = "Reference image:" });
            userContent.Add(ImageBlock(refData, refMime));
            userContent.Add(new JsonObject { ["type"] = "text", ["text"] = "Generated image:" });
        }

        userContent.Add(ImageBlock(imageData, mimeType));
        userContent.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = $"The prompt used to generate this image was:\n\n\"{prompt}\"\n\nPlease review the image and respond with JSON only."
        });

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 1024,
            ["system"] = systemPrompt,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = userContent }
            }
        };

        var (_, json) = await SendWithRetryAsync(body.ToJsonString(), ct);

        var root = JsonNode.Parse(json)!;
        var text = root["content"]![0]!["text"]!.GetValue<string>().Trim();

        if (text.StartsWith("```"))
        {
            text = text[(text.IndexOf('\n') + 1)..];
            if (text.EndsWith("```")) text = text[..^3].TrimEnd();
        }

        _logger.LogDebug("Claude raw response: {Text}", text);

        var score = JsonSerializer.Deserialize<ClaudeScoreResponse>(text, JsonOpts)
                    ?? throw new InvalidOperationException("Failed to parse Claude review response");

        return new ReviewScore(
            AccuracyScore: score.AccuracyScore,
            QualityScore: score.QualityScore,
            Defects: score.Defects,
            Commentary: score.Commentary,
            OverallScore: score.OverallScore,
            ReferenceAccuracyScore: score.ReferenceAccuracyScore
        );
    }

    private static JsonObject ImageBlock(byte[] data, string mime) => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject
        {
            ["type"] = "base64",
            ["media_type"] = mime,
            ["data"] = Convert.ToBase64String(data)
        }
    };

    private async Task<(HttpResponseMessage response, string json)> SendWithRetryAsync(string bodyJson, CancellationToken ct)
    {
        const int maxAttempts = 4;
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode) return (response, json);

            if ((status == 429 || status == 529) && attempt < maxAttempts)
            {
                _logger.LogWarning("Claude API {Status} on attempt {Attempt}/{Max} — retrying in {Delay}s",
                    status, attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay *= 2;
                continue;
            }

            _logger.LogError("Claude API error {Status}: {Body}", status, json);
            throw new InvalidOperationException($"Claude API error {status}: {json}");
        }

        throw new InvalidOperationException("Claude API retry limit exceeded");
    }

    private (byte[] data, string mime) ShrinkIfNeeded(byte[] data, string mime)
    {
        const int maxBytes = 4 * 1024 * 1024;
        if (data.Length <= maxBytes) return (data, mime);

        _logger.LogInformation("Image is {Kb} KB — compressing before sending to Claude", data.Length / 1024);

        using var bitmap = SKBitmap.Decode(data);
        var compressed = EncodeBitmap(bitmap, 85);

        if (compressed.Length <= maxBytes)
        {
            _logger.LogDebug("Compressed to {Kb} KB as JPEG q85", compressed.Length / 1024);
            return (compressed, "image/jpeg");
        }

        var scale = Math.Sqrt((double)maxBytes / compressed.Length);
        var newWidth  = Math.Max(1, (int)(bitmap.Width  * scale));
        var newHeight = Math.Max(1, (int)(bitmap.Height * scale));

        using var scaled = bitmap.Resize(new SKImageInfo(newWidth, newHeight),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        var final = EncodeBitmap(scaled, 80);

        _logger.LogDebug("Scaled to {W}×{H} and compressed to {Kb} KB", newWidth, newHeight, final.Length / 1024);
        return (final, "image/jpeg");

        static byte[] EncodeBitmap(SKBitmap bmp, int quality)
        {
            using var skImage = SKImage.FromBitmap(bmp);
            using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, quality);
            return encoded.ToArray();
        }
    }

    private record ClaudeScoreResponse(
        int AccuracyScore,
        int QualityScore,
        int OverallScore,
        List<string> Defects,
        string Commentary,
        int? ReferenceAccuracyScore = null
    );
}
