using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;

namespace Promptly.Application.Data;

public class PromptlyDbContext : IdentityDbContext<User>
{
    public PromptlyDbContext(DbContextOptions<PromptlyDbContext> options)
        : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Domain.Entities.Environment> Environments => Set<Domain.Entities.Environment>();
    public DbSet<Endpoint> Endpoints => Set<Endpoint>();
    public DbSet<MappingSpec> MappingSpecs => Set<MappingSpec>();
    public DbSet<TestSuite> TestSuites => Set<TestSuite>();
    public DbSet<TestCase> TestCases => Set<TestCase>();
    public DbSet<TestRun> TestRuns => Set<TestRun>();
    public DbSet<TestRunResult> TestRunResults => Set<TestRunResult>();
    public DbSet<ProjectSettings> ProjectSettings => Set<ProjectSettings>();
    public DbSet<ProjectApiKey> ProjectApiKeys => Set<ProjectApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure User entity
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(e => e.CreatedAt);
            entity.HasMany(e => e.Projects)
                .WithOne(e => e.Owner)
                .HasForeignKey(e => e.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure Project entity
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OwnerUserId, e.Name });
            entity.HasIndex(e => e.CreatedAt);

            entity.HasMany(e => e.Environments)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.TestSuites)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.ApiKeys)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Settings)
                .WithOne(e => e.Project)
                .HasForeignKey<ProjectSettings>(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure Environment entity
        modelBuilder.Entity<Domain.Entities.Environment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Name });
            entity.HasIndex(e => e.CreatedAt);

            entity.Property(e => e.DefaultHeadersEncryptedJson)
                .HasColumnType("text");

            entity.HasMany(e => e.Endpoints)
                .WithOne(e => e.Environment)
                .HasForeignKey(e => e.EnvironmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure Endpoint entity
        modelBuilder.Entity<Endpoint>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.EnvironmentId, e.Name });

            entity.HasMany(e => e.MappingSpecs)
                .WithOne(e => e.Endpoint)
                .HasForeignKey(e => e.EndpointId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure MappingSpec entity
        modelBuilder.Entity<MappingSpec>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.EndpointId, e.IsDefault });
            entity.HasIndex(e => e.CreatedAt);

            entity.Property(e => e.SpecJson)
                .HasColumnType("text")
                .IsRequired();
        });

        // Configure TestSuite entity
        modelBuilder.Entity<TestSuite>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Name });
            entity.HasIndex(e => e.CreatedAt);

            entity.HasMany(e => e.TestCases)
                .WithOne(e => e.Suite)
                .HasForeignKey(e => e.SuiteId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.TestRuns)
                .WithOne(e => e.Suite)
                .HasForeignKey(e => e.SuiteId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Configure TestCase entity
        modelBuilder.Entity<TestCase>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SuiteId, e.ExternalId })
                .IsUnique();
            entity.HasIndex(e => e.CreatedAt);

            entity.Property(e => e.InputSpecJson)
                .HasColumnType("text")
                .IsRequired();

            entity.Property(e => e.ExpectationsJson)
                .HasColumnType("text")
                .IsRequired();

            entity.HasMany(e => e.TestRunResults)
                .WithOne(e => e.TestCase)
                .HasForeignKey(e => e.TestCaseId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Configure TestRun entity
        modelBuilder.Entity<TestRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.SuiteId, e.CreatedAt });

            entity.Property(e => e.Status)
                .HasConversion<string>();

            entity.Property(e => e.ConfigSnapshotJson)
                .HasColumnType("text");

            entity.Property(e => e.SummaryJson)
                .HasColumnType("text");

            entity.HasOne(e => e.Environment)
                .WithMany()
                .HasForeignKey(e => e.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Endpoint)
                .WithMany()
                .HasForeignKey(e => e.EndpointId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.MappingSpec)
                .WithMany()
                .HasForeignKey(e => e.MappingSpecId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.CreatedBy)
                .WithMany(u => u.CreatedTestRuns)
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(e => e.Results)
                .WithOne(e => e.TestRun)
                .HasForeignKey(e => e.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure TestRunResult entity
        modelBuilder.Entity<TestRunResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.RunId, e.Status });
            entity.HasIndex(e => e.CreatedAt);

            entity.Property(e => e.Status)
                .HasConversion<string>();

            entity.Property(e => e.MetricsJson)
                .HasColumnType("text");

            entity.Property(e => e.FailureReasonsJson)
                .HasColumnType("text");

            entity.Property(e => e.TraceJson)
                .HasColumnType("text")
                .IsRequired();
        });

        // Configure ProjectSettings entity
        modelBuilder.Entity<ProjectSettings>(entity =>
        {
            entity.HasKey(e => e.ProjectId);

            entity.Property(e => e.ModelPricingJson)
                .HasColumnType("text");
        });

        // Configure ProjectApiKey entity
        modelBuilder.Entity<ProjectApiKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.KeyHashSha256)
                .IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            entity.HasIndex(e => e.ExpiresAt);
        });
    }
}
