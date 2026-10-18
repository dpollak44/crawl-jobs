using Crawler.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Crawler.Api.Data;

/// <summary>A stored import run: the summary plus every rejected line, so nothing is silently dropped.</summary>
public sealed class ImportRun
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public int RowsRead { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public int Duplicates { get; set; }
    public List<ImportRejection> Rejections { get; set; } = [];
}

public sealed class ImportRejection
{
    public int Id { get; set; }
    public Guid ImportRunId { get; set; }
    public int LineNumber { get; set; }
    public string RawLine { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    v => v.UtcTicks,
    v => new DateTimeOffset(v, TimeSpan.Zero));

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<OutboundCall> OutboundCalls => Set<OutboundCall>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no date type, so it cannot sort or compare DateTimeOffset text. Every timestamp in this service
        // is UTC, so store the UTC ticks as a sortable integer with full precision.
        // Delete this override when moving to SQL Server / Azure SQL, which have datetimeoffset natively.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Claim>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasIndex(c => c.ExternalRef).IsUnique();
            b.Property(c => c.ExternalRef).HasMaxLength(100).IsRequired();
            b.Property(c => c.PatientName).HasMaxLength(200).IsRequired();
            b.Property(c => c.Amount).HasPrecision(18, 2);
            b.Property(c => c.Status).HasConversion<string>();
            b.Property(c => c.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<ImportRun>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasMany(r => r.Rejections).WithOne().HasForeignKey(x => x.ImportRunId);
        });

        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.HasKey(a => a.Id);
            b.HasIndex(a => new { a.Entity, a.EntityId });
            b.Property(a => a.Entity).HasMaxLength(100);
            b.Property(a => a.Action).HasMaxLength(100);
            b.Property(a => a.Actor).HasMaxLength(200);
        });

        modelBuilder.Entity<OutboundCall>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Target).HasMaxLength(200);
            b.Property(c => c.Status).HasConversion<string>();
            b.HasIndex(c => c.Status);
        });
    }
}
