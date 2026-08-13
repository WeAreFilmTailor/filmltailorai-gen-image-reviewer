using Microsoft.EntityFrameworkCore;

namespace GenImageReviewer.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ComparisonRun> ComparisonRuns => Set<ComparisonRun>();
    public DbSet<ComparisonResult> ComparisonResults => Set<ComparisonResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ComparisonResult>()
            .HasOne(r => r.Run)
            .WithMany(run => run.Results)
            .HasForeignKey(r => r.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
