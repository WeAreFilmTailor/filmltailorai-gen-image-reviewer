using System.Text;
using System.Text.Json.Nodes;

namespace GenImageReviewer.Providers;

// Shared client for the Black Forest Labs native API async flow:
// POST body to a model endpoint -> poll `polling_url` -> download result.sample
// (a signed URL: the x-key header must NOT be sent to it).
internal static class BflTaskRunner
{
    private static readonly HttpClient Http = new();
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(3);

    public static async Task<GenerationResult> RunAsync(
        string submitUrl,
        string apiKey,
        JsonObject body,
        string displayName,
        ILogger logger,
        CancellationToken ct)
    {
        var (submitOk, submitStatus, submitJson) = await SubmitAsync(submitUrl, apiKey, body, ct);

        // Each BFL route has its own approved safety_tolerance ceiling (e.g. FLUX.2 may allow 5 while
        // FLUX.3 allows 4). If we asked for too much, retry once at the highest approved level.
        if (!submitOk && submitStatus == 422 && body["safety_tolerance"] is JsonNode requested
            && TryGetApprovedMax(submitJson) is int approvedMax && approvedMax < requested.GetValue<int>())
        {
            logger.LogWarning("[{Provider}] safety_tolerance {Requested} not approved for this model; retrying with {Max}",
                displayName, requested.GetValue<int>(), approvedMax);
            body["safety_tolerance"] = approvedMax;
            (submitOk, submitStatus, submitJson) = await SubmitAsync(submitUrl, apiKey, body, ct);
        }

        if (!submitOk)
        {
            logger.LogWarning("[{Provider}] Submit HTTP {Status}: {Body}", displayName, submitStatus, submitJson);
            return GenerationResult.Failure($"{displayName} error {submitStatus}: {submitJson}");
        }

        var pollingUrl = JsonNode.Parse(submitJson)?["polling_url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(pollingUrl))
        {
            logger.LogWarning("[{Provider}] No polling_url in response: {Body}", displayName, submitJson);
            return GenerationResult.Failure($"{displayName} returned no polling_url");
        }

        // Poll until the task leaves an in-progress state
        var started = DateTime.UtcNow;
        while (true)
        {
            await Task.Delay(PollInterval, ct);

            using var poll = new HttpRequestMessage(HttpMethod.Get, pollingUrl);
            poll.Headers.Add("x-key", apiKey);
            using var pollResponse = await Http.SendAsync(poll, ct);
            var pollJson = await pollResponse.Content.ReadAsStringAsync(ct);

            JsonNode? root = null;
            try { root = JsonNode.Parse(pollJson); } catch (System.Text.Json.JsonException) { }
            var status = root?["status"]?.GetValue<string>();

            if (status is "Pending" or "Reasoning" or "Generating")
            {
                if (DateTime.UtcNow - started > MaxWait)
                    return GenerationResult.Failure($"{displayName} timed out after {MaxWait.TotalMinutes:0} minutes (status: {status})");
                continue;
            }

            if (status == "Ready")
            {
                var sampleUrl = root?["result"]?["sample"]?.GetValue<string>();
                if (string.IsNullOrEmpty(sampleUrl))
                    return GenerationResult.Failure($"{displayName} was Ready but returned no result.sample");

                // Signed URL: do NOT send the x-key header
                using var imageResponse = await Http.GetAsync(sampleUrl, ct);
                if (!imageResponse.IsSuccessStatusCode)
                    return GenerationResult.Failure($"{displayName} image download failed: HTTP {(int)imageResponse.StatusCode}");

                var bytes = await imageResponse.Content.ReadAsByteArrayAsync(ct);
                var mime  = imageResponse.Content.Headers.ContentType?.MediaType ?? "image/png";
                logger.LogDebug("[{Provider}] Got {Bytes} bytes ({Mime})", displayName, bytes.Length, mime);
                return new GenerationResult(bytes, mime);
            }

            logger.LogWarning("[{Provider}] Task ended with status {Status} (HTTP {Http}): {Body}",
                displayName, status, (int)pollResponse.StatusCode, pollJson);

            return GenerationResult.Failure(status switch
            {
                "Request Moderated" => $"{displayName} moderated the prompt (request blocked). Try rewording it.",
                "Content Moderated" => $"{displayName} moderated the generated image (content blocked). Try rewording the prompt.",
                null                => $"{displayName} error {(int)pollResponse.StatusCode}: {pollJson}",
                _                   => $"{displayName} failed with status '{status}': {pollJson}",
            });
        }
    }

    private static async Task<(bool Ok, int Status, string Json)> SubmitAsync(
        string submitUrl, string apiKey, JsonObject body, CancellationToken ct)
    {
        using var submit = new HttpRequestMessage(HttpMethod.Post, submitUrl);
        submit.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        submit.Headers.Add("x-key", apiKey);

        using var response = await Http.SendAsync(submit, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, (int)response.StatusCode, json);
    }

    private static int? TryGetApprovedMax(string json)
    {
        try
        {
            var detail = JsonNode.Parse(json)?["detail"];
            return detail?["code"]?.GetValue<string>() == "safety_tolerance_not_approved"
                ? detail["maximum_approved_safety_tolerance"]?.GetValue<int>()
                : null;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
