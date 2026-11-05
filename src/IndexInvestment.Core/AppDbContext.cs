using IndexInvestment.Core.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IndexInvestment.Core;

/// <summary>
/// Single database for all users (SQLite by default): portfolio state
/// (positions / runs / transactions / run reports), per-user settings,
/// and the ASP.NET Core Identity user tables.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<RunReport> RunReports => Set<RunReport>();
    public DbSet<IdentityUser> Users => Set<IdentityUser>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Identity tables. Mapped explicitly (rather than subclassing
        // IdentityDbContext) so this library stays free of an ASP.NET Core
        // shared-framework reference — the console CLI references this
        // project and would otherwise inherit that dependency.
        b.Entity<IdentityUser>(e =>
        {
            e.ToTable("AspNetUsers");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.NormalizedUserName).HasMaxLength(256);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.NormalizedEmail).HasMaxLength(256);
        });

        b.Entity<IdentityRole>(e =>
        {
            e.ToTable("AspNetRoles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.NormalizedName).HasMaxLength(256);
        });

        b.Entity<IdentityUserRole<string>>(e =>
        {
            e.ToTable("AspNetUserRoles");
            e.HasKey(x => new { x.UserId, x.RoleId });
        });

        // The remaining standard Identity tables (unused by this app, but
        // the EF store queries them when a session is created).
        b.Entity<IdentityUserClaim<string>>(e =>
        {
            e.ToTable("AspNetUserClaims");
            e.HasKey(x => x.Id);
        });
        b.Entity<IdentityUserLogin<string>>(e =>
        {
            e.ToTable("AspNetUserLogins");
            e.HasKey(x => new { x.LoginProvider, x.ProviderKey });
        });
        b.Entity<IdentityUserToken<string>>(e =>
        {
            e.ToTable("AspNetUserTokens");
            e.HasKey(x => new { x.UserId, x.LoginProvider, x.Name });
        });
        b.Entity<IdentityRoleClaim<string>>(e =>
        {
            e.ToTable("AspNetRoleClaims");
            e.HasKey(x => x.Id);
        });

        b.Entity<UserSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.CommissionPercent).HasPrecision(10, 6);
            e.Property(x => x.IndexId).HasMaxLength(32);
        });

        b.Entity<Position>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Ticker });
            e.Property(x => x.Ticker).HasMaxLength(32);
        });

        b.Entity<Run>(e =>
        {
            e.Property(x => x.Date).HasMaxLength(32);
            e.Property(x => x.TradeDate).HasMaxLength(32);
            e.Property(x => x.IndexId).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasIndex(x => new { x.UserId, x.Id });
        });

        b.Entity<Transaction>(e =>
        {
            e.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunId)
                .OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Date).HasMaxLength(32);
            e.Property(x => x.Ticker).HasMaxLength(32);
            e.Property(x => x.Action).HasMaxLength(8);
            e.HasIndex(x => new { x.UserId, x.Id });
            e.HasIndex(x => new { x.RunId, x.Id });
        });

        b.Entity<RunReport>(e =>
        {
            e.Property(x => x.Ticker).HasMaxLength(32);
            e.HasIndex(x => new { x.RunId, x.Id });
        });
    }
}

/// <summary>
/// Design-time factory so `dotnet ef migrations` can build the model
/// without a running app.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new AppDbContext(options);
    }
}

/// <summary>Connection-string helpers shared by webapp, CLI and tests.</summary>
public static class DbOptions
{
    public const string DefaultSqliteConnectionString = "Data Source=data/app.db";

    public static DbContextOptions<AppDbContext> Sqlite(string connectionString = DefaultSqliteConnectionString) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
}
