using GenImageReviewer.Components;
using GenImageReviewer.Data;
using GenImageReviewer.Providers;
using GenImageReviewer.Reviewers;
using GenImageReviewer.Services;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// EF Core with SQLite
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// --- Stability AI ---
var stabilityKey  = builder.Configuration["ImageProviders:StabilityAi:ApiKey"] ?? "";
var stabilityBase = builder.Configuration["ImageProviders:StabilityAi:BaseUrl"] ?? "https://api.stability.ai/";

if (!string.IsNullOrWhiteSpace(stabilityKey))
{
    builder.Services.AddSingleton<IImageGenerationProvider>(sp =>
    {
        var http = new HttpClient { BaseAddress = new Uri(stabilityBase) };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {stabilityKey}");
        return new StabilityAiProvider(http, "stability-core", "Stability AI Core", "core",
            sp.GetRequiredService<ILogger<StabilityAiProvider>>());
    });

    builder.Services.AddSingleton<IImageGenerationProvider>(sp =>
    {
        var http = new HttpClient { BaseAddress = new Uri(stabilityBase) };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {stabilityKey}");
        return new StabilityAiProvider(http, "stability-ultra", "Stability AI Ultra", "ultra",
            sp.GetRequiredService<ILogger<StabilityAiProvider>>());
    });
}

// --- Azure OpenAI image models ---
void RegisterAzureOpenAiProvider(string configSection, string name, string displayName, string lowQuality)
{
    var apiKey     = builder.Configuration[$"ImageProviders:{configSection}:ApiKey"] ?? "";
    var endpoint   = builder.Configuration[$"ImageProviders:{configSection}:Endpoint"] ?? "";
    var deployment = builder.Configuration[$"ImageProviders:{configSection}:Deployment"] ?? "";

    if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(endpoint))
    {
        builder.Services.AddSingleton<IImageGenerationProvider>(sp =>
            new AzureOpenAiImageProvider(name, displayName, endpoint, deployment, apiKey, lowQuality,
                sp.GetRequiredService<ILogger<AzureOpenAiImageProvider>>()));
    }
}

RegisterAzureOpenAiProvider("GptImage2",   "gpt-image-2",   "OpenAI gpt-image-2",   "low");
RegisterAzureOpenAiProvider("GptImage15",  "gpt-image-1.5", "OpenAI gpt-image-1.5", "standard");

// --- MAI models ---
void RegisterMaiProvider(string configSection, string name, string displayName)
{
    var apiKey     = builder.Configuration[$"ImageProviders:{configSection}:ApiKey"] ?? "";
    var endpoint   = builder.Configuration[$"ImageProviders:{configSection}:Endpoint"] ?? "";
    var deployment = builder.Configuration[$"ImageProviders:{configSection}:Deployment"] ?? "";

    if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(endpoint))
    {
        builder.Services.AddSingleton<IImageGenerationProvider>(sp =>
            new MaiImageProvider(name, displayName, endpoint, deployment, apiKey,
                sp.GetRequiredService<ILogger<MaiImageProvider>>()));
    }
}

RegisterMaiProvider("MaiImage2",  "mai-image-2",  "MAI-Image-2");
RegisterMaiProvider("MaiImage2e", "mai-image-2e", "MAI-Image-2e");
RegisterMaiProvider("MaiImage2dot6", "mai-image-2.6", "MAI-Image-2.6");

// --- FLUX.2 models (Black Forest Labs via Foundry) ---
void RegisterFluxProvider(string configSection, string name, string displayName)
{
    var apiKey     = builder.Configuration[$"ImageProviders:{configSection}:ApiKey"] ?? "";
    var endpoint   = builder.Configuration[$"ImageProviders:{configSection}:Endpoint"] ?? "";
    var deployment = builder.Configuration[$"ImageProviders:{configSection}:Deployment"] ?? "";

    if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(endpoint))
    {
        builder.Services.AddSingleton<IImageGenerationProvider>(sp =>
            new FluxImageProvider(name, displayName, endpoint, deployment, apiKey,
                sp.GetRequiredService<ILogger<FluxImageProvider>>()));
    }
}

RegisterFluxProvider("FLUX2Pro",  "flux-2-pro",  "FLUX.2 Pro");
RegisterFluxProvider("FLUX2Flex", "flux-2-flex", "FLUX.2 Flex");

// --- FLUX.3 (Black Forest Labs native API) ---
var bflKey  = builder.Configuration["ImageProviders:BlackForestLabs:ApiKey"] ?? "";
var bflBase = builder.Configuration["ImageProviders:BlackForestLabs:BaseUrl"] ?? "https://api.bfl.ai/";

if (!string.IsNullOrWhiteSpace(bflKey))
{
    builder.Services.AddSingleton<IImageGenerationProvider>(sp =>
        new Flux3ImageProvider("flux-3", "FLUX.3", bflBase, bflKey,
            sp.GetRequiredService<ILogger<Flux3ImageProvider>>()));
}

// --- Claude reviewer ---
var claudeKey   = builder.Configuration["Claude:ApiKey"] ?? "";
var claudeModel = builder.Configuration["Claude:Model"] ?? "claude-sonnet-4-6";
var claudeBase  = builder.Configuration["Claude:BaseUrl"] ?? "https://api.anthropic.com/";

builder.Services.AddSingleton<IImageReviewer>(sp =>
{
    var http = new HttpClient { BaseAddress = new Uri(claudeBase) };
    http.DefaultRequestHeaders.Add("x-api-key", claudeKey);
    http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    return new ClaudeReviewer(http, sp.GetRequiredService<ILogger<ClaudeReviewer>>(), claudeModel);
});

builder.Services.AddScoped<ComparisonService>();

// Claude prompt generation (shares same key/base as reviewer)
builder.Services.AddSingleton<PromptGenerationService>(sp =>
{
    var http = new HttpClient { BaseAddress = new Uri(claudeBase) };
    http.DefaultRequestHeaders.Add("x-api-key", claudeKey);
    http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    return new PromptGenerationService(http, sp.GetRequiredService<ILogger<PromptGenerationService>>(), claudeModel);
});

var app = builder.Build();

// Apply EF Core migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var ctx = await db.CreateDbContextAsync();
    await ctx.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
