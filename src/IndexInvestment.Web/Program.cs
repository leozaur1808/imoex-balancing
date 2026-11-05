using System.Text.Json;
using IndexInvestment.Core;
using IndexInvestment.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

// Content root = the app's own directory, so static files (wwwroot) and
// appsettings are found no matter where the process is started from.
// The working directory stays free for the (relative) data/ path.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// ------------------------------------------------------------
// Storage: one SQLite database for all users (data/app.db).
// The relative path is resolved against the process CWD — run
// the app from the repo root so data/ is shared with the CLI.
// ------------------------------------------------------------
var connStr = builder.Configuration.GetConnectionString("Default")
              ?? DbOptions.DefaultSqliteConnectionString;
EnsureSqliteDirectory(connStr);

builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connStr));

// ------------------------------------------------------------
// Identity: email + password, cookie sessions.
// First user created on the setup screen becomes the admin;
// further accounts are created by the admin only.
// ------------------------------------------------------------
builder.Services
    .AddIdentity<IdentityUser, IdentityRole>(o =>
    {
        o.Password.RequiredLength = 4;
        o.Password.RequireDigit = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.User.RequireUniqueEmail = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromSeconds(30);
    })
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.SlidingExpiration = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
});

// ------------------------------------------------------------
// Core services
// ------------------------------------------------------------
builder.Services.AddHttpClient("moex", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<IMoexClient>(sp => new MoexClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("moex"),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MoexClient>>()));
builder.Services.AddSingleton<SnapshotCache>();
builder.Services.AddSingleton<MarketDataService>();
builder.Services.AddSingleton<PortfolioService>();
builder.Services.AddSingleton<DcaEngine>();

builder.Services.AddControllers(options => options.Filters.Add<DomainExceptionFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

var app = builder.Build();

// Apply migrations (idempotent) and make sure the admin role exists.
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var factory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.Database.MigrateAsync();
    }
    var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync(Roles.Admin))
        await roles.CreateAsync(new IdentityRole(Roles.Admin));
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Unauthenticated — used by local monitoring/polling.
app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.Run();

// ============================================================

static void EnsureSqliteDirectory(string connectionString)
{
    var i = connectionString.IndexOf("Data Source=", StringComparison.OrdinalIgnoreCase);
    if (i < 0) return;
    var rest = connectionString[(i + "Data Source=".Length)..];
    var path = rest.Split(';', '"')[0].Trim();
    if (path.Length == 0 || Path.IsPathRooted(path)) return;
    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
}
