using Crawler.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Crawler.Infrastructure.Persistence;

public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    v => v.UtcTicks,
    v => new DateTimeOffset(v, TimeSpan.Zero));

/// <summary>
/// Three tables: jobs, pages, links. Indexes follow the three questions the system asks constantly:
/// "newest jobs first" (history), "any pages still queued for this job?" (completion), "links by parent"
/// (tree). Timestamps are UTC ticks so SQLite (tests) and Postgres (production) sort them identically.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<CrawlJob> Jobs => Set<CrawlJob>();
    public DbSet<CrawlPage> Pages => Set<CrawlPage>();
    public DbSet<PageLink> Links => Set<PageLink>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CrawlJob>(b =>
        {
            b.ToTable("jobs");
            b.HasKey(j => j.Id);
            b.Property(j => j.InputUrl).HasMaxLength(2048).IsRequired();
            b.Property(j => j.StartHost).HasMaxLength(255).IsRequired();
            b.Property(j => j.Status).HasConversion<string>().HasMaxLength(20).IsConcurrencyToken(); // a stale copy cannot overwrite a final status
            b.Property(j => j.FailureReason).HasMaxLength(CrawlJob.MaxReasonLength);
            // The cap counter is only ever changed by an atomic UPDATE … WHERE PagesDiscovered + n <= MaxPages (PageProcessor),
            // never by writing back an entity, so two workers can never both slip past the cap.
            b.Property(j => j.PagesDiscovered).ValueGeneratedOnUpdate();
            b.HasIndex(j => j.CreatedAt).IsDescending();
        });

        modelBuilder.Entity<CrawlPage>(b =>
        {
            b.ToTable("pages");
            b.HasKey(p => p.Id);
            b.Property(p => p.Url).HasMaxLength(2048).IsRequired();
            b.Property(p => p.FinalUrl).HasMaxLength(2048);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsConcurrencyToken(); // the second of two racing copies loses, never overwrites
            b.Property(p => p.Error).HasMaxLength(CrawlPage.MaxErrorLength);
            b.Property(p => p.DomainLinkRatio).HasPrecision(5, 4);
            b.HasIndex(p => new { p.JobId, p.Url }).IsUnique();   // idempotency: one row per page per job
            b.HasIndex(p => new { p.JobId, p.Status });           // "any pages still queued?"
            b.HasIndex(p => new { p.JobId, p.ParentPageId });     // tree
            b.HasOne<CrawlJob>().WithMany().HasForeignKey(p => p.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PageLink>(b =>
        {
            b.ToTable("links");
            b.HasKey(l => l.Id);
            b.Property(l => l.ChildUrl).HasMaxLength(2048).IsRequired();
            b.HasIndex(l => new { l.JobId, l.ParentPageId, l.ChildUrl }).IsUnique(); // idempotency for edges
            b.HasOne<CrawlPage>().WithMany().HasForeignKey(l => l.ParentPageId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
