using System.Text;
using System.Text.Json.Nodes;

namespace GenImageReviewer.Providers;

// Black Forest Labs FLUX.3 via the native BFL API (https://docs.bfl.ai/flux_3/flux3_image_generate)
// Async flow: POST /v1/flux-3-image -> poll `polling_url` -> download result.sample (signed URL, no auth header).
// Takes aspect_ratio + resolution rather than width/height. Single reference images are not supported
// (the endpoint only accepts 2-10 images), so references are ignored.
public class Flux3ImageProvider : IImageGenerationProvider
{
    private static readonly HttpClient Http = new();

    private static readonly (string Label, double Ratio)[] AspectRatios =
        new[]
        {
            "21:9", "2:1", "16:9", "3:2", "7:5", "4:3", "5:4", "1:1",
            "4:5", "3:4", "5:7", "2:3", "9:16", "1:2", "9:21",
        }.Select(s => { var p = s.Split(':'); return (s, double.Parse(p[0]) / double.Parse(p[1])); }).ToArray();

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(3);

    private readonly string _submitUrl;
    private readonly string _apiKey;
    private readonly ILogger<Flux3ImageProvider> _logger;

    public string Name { get; }
    public string DisplayName { get; }

    public Flux3ImageProvider(
        string name,
        string displayName,
        string baseUrl,
        string apiKey,
        ILogger<Flux3ImageProvider> logger)
    {
        Name = name;
        DisplayName = displayName;
        _apiKey = apiKey;
        _logger = logger;
        _submitUrl = baseUrl.TrimEnd('/') + "/v1/flux-3-image";
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

        using var submit = new HttpRequestMessage(HttpMethod.Post, _submitUrl);
        submit.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        submit.Headers.Add("x-key", _apiKey);

        using var submitResponse = await Http.SendAsync(submit, ct);
        var submitJson = await submitResponse.Content.ReadAsStringAsync(ct);

        if (!submitResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("[{Provider}] Submit HTTP {Status}: {Body}", DisplayName, (int)submitResponse.StatusCode, submitJson);
            return GenerationResult.Failure($"{DisplayName} error {(int)submitResponse.StatusCode}: {submitJson}");
        }

        var pollingUrl = JsonNode.Parse(submitJson)?["polling_url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(pollingUrl))
        {
            _logger.LogWarning("[{Provider}] No polling_url in response: {Body}", DisplayName, submitJson);
            return GenerationResult.Failure($"{DisplayName} returned no polling_url");
        }

        // Poll until the task leaves an in-progress state
        var started = DateTime.UtcNow;
        while (true)
        {
            await Task.Delay(PollInterval, ct);

            using var poll = new HttpRequestMessage(HttpMethod.Get, pollingUrl);
            poll.Headers.Add("x-key", _apiKey);
            using var pollResponse = await Http.SendAsync(poll, ct);
            var pollJson = await pollResponse.Content.ReadAsStringAsync(ct);

            JsonNode? root = null;
            try { root = JsonNode.Parse(pollJson); } catch (System.Text.Json.JsonException) { }
            var status = root?["status"]?.GetValue<string>();

            if (status is "Pending" or "Reasoning" or "Generating")
            {
                if (DateTime.UtcNow - started > MaxWait)
                    return GenerationResult.Failure($"{DisplayName} timed out after {MaxWait.TotalMinutes:0} minutes (status: {status})");
                continue;
            }

            if (status == "Ready")
            {
                var sampleUrl = root?["result"]?["sample"]?.GetValue<string>();
                if (string.IsNullOrEmpty(sampleUrl))
                    return GenerationResult.Failure($"{DisplayName} was Ready but returned no result.sample");

                // Signed URL: do NOT send the x-key header
                using var imageResponse = await Http.GetAsync(sampleUrl, ct);
                if (!imageResponse.IsSuccessStatusCode)
                    return GenerationResult.Failure($"{DisplayName} image download failed: HTTP {(int)imageResponse.StatusCode}");

                var bytes = await imageResponse.Content.ReadAsByteArrayAsync(ct);
                var mime  = imageResponse.Content.Headers.ContentType?.MediaType ?? "image/png";
                _logger.LogDebug("[{Provider}] Got {Bytes} bytes ({Mime})", DisplayName, bytes.Length, mime);
                return new GenerationResult(bytes, mime);
            }

            _logger.LogWarning("[{Provider}] Task ended with status {Status} (HTTP {Http}): {Body}",
                DisplayName, status, (int)pollResponse.StatusCode, pollJson);

            return GenerationResult.Failure(status switch
            {
                "Request Moderated" => $"{DisplayName} moderated the prompt (request blocked). Try rewording it.",
                "Content Moderated" => $"{DisplayName} moderated the generated image (content blocked). Try rewording the prompt.",
                null                => $"{DisplayName} error {(int)pollResponse.StatusCode}: {pollJson}",
                _                   => $"{DisplayName} failed with status '{status}': {pollJson}",
            });
        }
    }

    private static string NearestAspectRatio(int width, int height)
    {
        var target = (double)width / Math.Max(1, height);
        return AspectRatios.MinBy(a => Math.Abs(Math.Log(a.Ratio / target))).Label;
    }
}
