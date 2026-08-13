using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenImageReviewer.Services;

// Calls Claude to turn a synopsis/scene description into a detailed
// Stability AI / text-to-image prompt using the same cinematographic
// approach as the FilmTailorAI moodboard generation pipeline.
public class PromptGenerationService(
    HttpClient http,
    ILogger<PromptGenerationService> logger,
    string model = "claude-sonnet-4-6")
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string SystemPrompt = """
        You are a world-class film production visual director with mastery of cinematography, production design, art direction, wardrobe, and visual storytelling. You are creating an image prompt for Stability AI Ultra to generate a cinematic moodboard still. The prompt will render as a photorealistic still frame indistinguishable from a production photograph of a high-budget film.

        STORY VISUAL ANALYSIS (do this first, silently):
        Before writing the prompt, analyse the synopsis to identify:
        - Genre DNA: What visual language does this story demand? (Thriller = high contrast, cold tones; Drama = natural light, warm interiors; Horror = deep shadows, desaturated; Sci-Fi = sleek surfaces, artificial light; etc.)
        - The single most visually powerful and narratively significant moment.
        - Recurring visual motifs that could anchor the frame.

        OUTPUT: One combined image generation prompt using the 7-Layer Method below.

        THE 7-LAYER METHOD — weave ALL layers into flowing cinematic prose (50–100 words total):

        1. SUBJECT: Focal person or object. Describe people ONLY by visible physical traits: build, age range, skin tone, hair (colour, length, texture), clothing (fabric, fit, colour, condition). NEVER use character names.
        2. ACTION STATE: The frozen peak instant as visible physical state — NOT motion. "Mid-stride on rain-slicked tarmac" not "she runs".
        3. KEY PROPS & WARDROBE: Material-specific — "a battered leather journal with dog-eared pages" not "a book".
        4. SPATIAL COMPOSITION: Three layers of depth — foreground element, main subject in midground, background detail.
        5. ENVIRONMENT: Art-department specificity — wall material, floor surface, furniture era, period-accurate details, cultural authenticity.
        6. LIGHTING: Named light source and its visible effect — direction, colour temperature, shadow quality. "Harsh overhead fluorescent" or "golden hour streaming through floor-to-ceiling windows, long shadows on polished concrete".
        7. ATMOSPHERE: Air quality (dust, smoke, mist, rain, steam) and surface texture (condensation, wet stone, cracked earth). The viewer should FEEL the temperature.

        RESPOND with valid JSON only — no other text:
        {
          "mainPrompt": "<overall visual universe of the story — production design, cinematographic tone, colour palette, atmosphere, 40–60 words>",
          "scenePrompt": "<specific frozen frame using the 7-layer method, 40–60 words>"
        }

        The final image prompt will be mainPrompt + " " + scenePrompt. Both must combine seamlessly.
        """;

    public async Task<string> GenerateAsync(string synopsis, CancellationToken ct = default)
    {
        logger.LogDebug("Generating image prompt from synopsis ({Chars} chars)", synopsis.Length);

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 1024,
            ["system"] = SystemPrompt,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = $"Generate an image prompt for the following synopsis:\n\n{synopsis}"
                }
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

        logger.LogDebug("Claude raw prompt response: {Text}", text);

        var response = JsonSerializer.Deserialize<PromptResponse>(text, JsonOpts)
                       ?? throw new InvalidOperationException("Failed to parse Claude prompt response");

        if (string.IsNullOrWhiteSpace(response.MainPrompt) || string.IsNullOrWhiteSpace(response.ScenePrompt))
            throw new InvalidOperationException("Claude returned an empty prompt");

        return $"{response.MainPrompt.Trim()} {response.ScenePrompt.Trim()}";
    }

    private async Task<(HttpResponseMessage response, string json)> SendWithRetryAsync(string bodyJson, CancellationToken ct)
    {
        const int maxAttempts = 4;
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            var response = await http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode) return (response, json);

            if ((status == 429 || status == 529) && attempt < maxAttempts)
            {
                logger.LogWarning("Claude API {Status} on attempt {Attempt}/{Max} — retrying in {Delay}s",
                    status, attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay *= 2;
                continue;
            }

            logger.LogError("Claude API error {Status}: {Body}", status, json);
            throw new InvalidOperationException($"Claude API error {status}: {json}");
        }

        throw new InvalidOperationException("Claude API retry limit exceeded");
    }

    private record PromptResponse(string MainPrompt, string ScenePrompt);
}
