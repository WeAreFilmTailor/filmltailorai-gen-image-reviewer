using System.Diagnostics;
using System.Text.Json;
using GenImageReviewer.Data;
using GenImageReviewer.Providers;
using GenImageReviewer.Reviewers;
using Microsoft.EntityFrameworkCore;

namespace GenImageReviewer.Services;

public class ComparisonService(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<IImageGenerationProvider> providers,
    IImageReviewer reviewer,
    IConfiguration config,
    ILogger<ComparisonService> logger)
{
    public IReadOnlyList<IImageGenerationProvider> AvailableProviders { get; } = providers.ToList();

    private int GenerationTimeoutSeconds =>
        config.GetValue<int?>("GenerationTimeoutSeconds") ?? 60;

    public async Task<List<ComparisonRun>> GetRunsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ComparisonRuns
            .Include(r => r.Results)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<ComparisonRun?> GetRunAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ComparisonRuns
            .Include(r => r.Results)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<int> RunComparisonAsync(
        string prompt,
        GenerationSettings settings,
        IEnumerable<string> providerNames,
        byte[]? referenceImageData = null,
        string? referenceImageMimeType = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var selectedProviders = AvailableProviders.Where(p => providerNames.Contains(p.Name)).ToList();

        logger.LogInformation("Starting comparison run. Prompt: {Prompt}. Providers: {Providers}",
            prompt, string.Join(", ", selectedProviders.Select(p => p.DisplayName)));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = new ComparisonRun
        {
            Prompt = prompt,
            NegativePrompt = settings.NegativePrompt,
            Width = settings.Width,
            Height = settings.Height,
            Quality = settings.Quality.ToString(),
            ReferenceImageData = referenceImageData,
            ReferenceImageMimeType = referenceImageMimeType,
            CreatedAt = DateTime.UtcNow
        };
        db.ComparisonRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var results = await GenerateImagesAsync(run.Id, 1, prompt, settings, selectedProviders, referenceImageData, referenceImageMimeType, progress, ct);

        await using var db2 = await dbFactory.CreateDbContextAsync(ct);
        db2.ComparisonResults.AddRange(results);
        await db2.SaveChangesAsync(ct);

        await ReviewResultsAsync(results, prompt, referenceImageData, referenceImageMimeType, progress, ct);

        progress?.Report("Done.");
        logger.LogInformation("Comparison run {RunId} complete", run.Id);
        return run.Id;
    }

    public async Task RegenerateAsync(
        int runId,
        IEnumerable<string> providerNames,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.ComparisonRuns.Include(r => r.Results).FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;

        var nextRound = (run.Results.Max(r => (int?)r.Round) ?? 0) + 1;
        var quality = Enum.TryParse<ImageQuality>(run.Quality, out var q) ? q : ImageQuality.Medium;
        var settings = new GenerationSettings(run.Width, run.Height, run.NegativePrompt, quality);
        var selectedProviders = AvailableProviders.Where(p => providerNames.Contains(p.Name)).ToList();

        logger.LogInformation("Regenerating run {RunId} round {Round} with {Providers}",
            runId, nextRound, string.Join(", ", selectedProviders.Select(p => p.DisplayName)));

        var results = await GenerateImagesAsync(runId, nextRound, run.Prompt, settings, selectedProviders, run.ReferenceImageData, run.ReferenceImageMimeType, progress, ct);

        await using var db2 = await dbFactory.CreateDbContextAsync(ct);
        db2.ComparisonResults.AddRange(results);
        await db2.SaveChangesAsync(ct);

        await ReviewResultsAsync(results, run.Prompt, run.ReferenceImageData, run.ReferenceImageMimeType, progress, ct);
        progress?.Report("Done.");
    }

    public async Task RetryReviewAsync(int resultId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var result = await db.ComparisonResults.Include(r => r.Run)
            .FirstOrDefaultAsync(r => r.Id == resultId, ct);

        if (result?.ImageData is null) return;

        progress?.Report($"Retrying review for {result.ProviderDisplayName}…");
        logger.LogInformation("[{Provider}] Retrying review for result {Id}", result.ProviderDisplayName, resultId);

        try
        {
            var score = await reviewer.ReviewAsync(
                result.Run.Prompt, result.ImageData, result.MimeType,
                result.Run.ReferenceImageData, result.Run.ReferenceImageMimeType, ct);

            result.ReviewCompleted = true;
            result.AccuracyScore = score.AccuracyScore;
            result.QualityScore = score.QualityScore;
            result.OverallScore = score.OverallScore;
            result.ReferenceAccuracyScore = score.ReferenceAccuracyScore;
            result.DefectsJson = JsonSerializer.Serialize(score.Defects);
            result.Commentary = score.Commentary;
            result.ReviewError = null;

            logger.LogInformation("[{Provider}] Retry review complete — overall {Overall}/10",
                result.ProviderDisplayName, score.OverallScore);
        }
        catch (Exception ex)
        {
            result.ReviewCompleted = false;
            result.ReviewError = ex.Message;
            logger.LogError(ex, "[{Provider}] Retry review failed", result.ProviderDisplayName);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRunAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.ComparisonRuns.FindAsync([id], ct);
        if (run is not null)
        {
            db.ComparisonRuns.Remove(run);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Deleted run {RunId}", id);
        }
    }

    // --- private helpers ---

    private async Task<ComparisonResult[]> GenerateImagesAsync(
        int runId, int round, string prompt, GenerationSettings settings,
        IReadOnlyList<IImageGenerationProvider> selectedProviders,
        byte[]? referenceImageData, string? referenceImageMimeType,
        IProgress<string>? progress, CancellationToken ct)
    {
        var tasks = selectedProviders.Select(async provider =>
        {
            progress?.Report($"Generating with {provider.DisplayName}…");
            logger.LogInformation("[{Provider}] Generation started (round {Round})", provider.DisplayName, round);
            var sw = Stopwatch.StartNew();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(GenerationTimeoutSeconds));

            GenerationResult result;
            try
            {
                result = await provider.GenerateAsync(prompt, settings, referenceImageData, referenceImageMimeType, cts.Token);
                sw.Stop();
                if (result.Success)
                    logger.LogInformation("[{Provider}] Generation succeeded in {Ms}ms ({Bytes} bytes)",
                        provider.DisplayName, sw.ElapsedMilliseconds, result.ImageData?.Length ?? 0);
                else
                    logger.LogWarning("[{Provider}] Generation failed: {Error}", provider.DisplayName, result.ErrorMessage);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sw.Stop();
                logger.LogWarning("[{Provider}] Generation timed out after {Seconds}s",
                    provider.DisplayName, GenerationTimeoutSeconds);
                result = GenerationResult.Failure($"Timed out after {GenerationTimeoutSeconds}s");
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogError(ex, "[{Provider}] Generation threw an exception", provider.DisplayName);
                result = GenerationResult.Failure(ex.Message);
            }

            progress?.Report(result.Success
                ? $"{provider.DisplayName} done ({sw.ElapsedMilliseconds / 1000.0:F1}s)"
                : $"{provider.DisplayName} failed: {result.ErrorMessage}");

            return new ComparisonResult
            {
                RunId = runId,
                Round = round,
                ProviderName = provider.Name,
                ProviderDisplayName = provider.DisplayName,
                ImageData = result.ImageData,
                MimeType = result.MimeType,
                GenerationSuccess = result.Success,
                GenerationError = result.ErrorMessage,
                GenerationDurationMs = sw.ElapsedMilliseconds,
                CreatedAt = DateTime.UtcNow
            };
        });

        return await Task.WhenAll(tasks);
    }

    private async Task ReviewResultsAsync(
        ComparisonResult[] results, string prompt,
        byte[]? referenceImageData, string? referenceImageMimeType,
        IProgress<string>? progress, CancellationToken ct)
    {
        foreach (var result in results.Where(r => r.GenerationSuccess && r.ImageData is not null))
        {
            progress?.Report($"Reviewing {result.ProviderDisplayName} result…");
            logger.LogInformation("[{Provider}] Review started", result.ProviderDisplayName);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var tracked = await db.ComparisonResults.FindAsync([result.Id], ct);
            if (tracked is null) continue;

            try
            {
                var score = await reviewer.ReviewAsync(
                    prompt, result.ImageData!, result.MimeType,
                    referenceImageData, referenceImageMimeType, ct);

                tracked.ReviewCompleted = true;
                tracked.AccuracyScore = score.AccuracyScore;
                tracked.QualityScore = score.QualityScore;
                tracked.OverallScore = score.OverallScore;
                tracked.ReferenceAccuracyScore = score.ReferenceAccuracyScore;
                tracked.DefectsJson = JsonSerializer.Serialize(score.Defects);
                tracked.Commentary = score.Commentary;

                logger.LogInformation(
                    "[{Provider}] Review complete — overall {Overall}/10, accuracy {Accuracy}/10, {DefectCount} defect(s)",
                    result.ProviderDisplayName, score.OverallScore, score.AccuracyScore, score.Defects.Count);
            }
            catch (Exception ex)
            {
                tracked.ReviewCompleted = false;
                tracked.ReviewError = ex.Message;
                logger.LogError(ex, "[{Provider}] Review threw an exception", result.ProviderDisplayName);
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
