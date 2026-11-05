using IndexInvestment.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndexInvestment.Tests;

/// <summary>
/// A throw-away environment per test: temp SQLite database + a fake
/// MOEX market (no real HTTP) + the full engine stack.
///
/// Default market (same as the old Python conftest): AAA 60% @ 10.0
/// (lot 10) and BBB 40% @ 20.0 (lot 10); lot costs 100 / 200 RUB plus
/// commission headroom.
/// </summary>
public sealed class TestEnv : IAsyncDisposable
{
    private readonly string _dir;

    public AppDbContextFactory Factory { get; }
    public PortfolioService Portfolio { get; }
    public FakeMoexClient Fake { get; }
    public MarketDataService Market { get; }
    public DcaEngine Engine { get; }

    public const double Commission = 0.00045;   // 0.045% as a fraction, like the old app

    public static TestEnv Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "idx-inv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var connStr = $"Data Source={Path.Combine(dir, "test.db")}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connStr).Options;
        var factory = new AppDbContextFactory(options);

        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();

        var fake = new FakeMoexClient();
        var market = new MarketDataService(fake, new SnapshotCache(), NullLogger<MarketDataService>.Instance);
        var portfolio = new PortfolioService(factory, NullLogger<PortfolioService>.Instance);
        var engine = new DcaEngine(market, portfolio, NullLogger<DcaEngine>.Instance);
        return new TestEnv(factory, portfolio, fake, market, engine, dir);
    }

    private TestEnv(
        AppDbContextFactory factory, PortfolioService portfolio, FakeMoexClient fake,
        MarketDataService market, DcaEngine engine, string dir)
    {
        Factory = factory;
        Portfolio = portfolio;
        Fake = fake;
        Market = market;
        Engine = engine;
        _dir = dir;
    }

    /// <summary>Convenience: an applied run with the default commission.</summary>
    public Task<RunResult> Run(double cash, string user = "u1", bool dry = false) =>
        Engine.RunAsync(user, "IMOEX", Commission, cash, dry, 0.0);

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

public sealed class AppDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>, IAsyncDisposable
{
    public AppDbContext CreateDbContext() => new(options);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>In-memory stand-in for MOEX — no real HTTP in tests.</summary>
public sealed class FakeMoexClient : IMoexClient
{
    public IndexComposition Composition { get; set; } = DefaultComposition;
    public Dictionary<string, BoardSecurity> Board { get; set; } = DefaultBoard;

    public Task<IndexComposition> GetIndexCompositionAsync(
        string indexId, int pageSize = 300, CancellationToken ct = default) =>
        Task.FromResult(Composition);

    public Task<IReadOnlyDictionary<string, BoardSecurity>> GetTqbrBoardAsync(
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, BoardSecurity>>(Board);

    public static IndexComposition DefaultComposition => new("2026-11-02", new[]
    {
        new IndexConstituent("AAA", "Aa Corp", 60.0),
        new IndexConstituent("BBB", "Bb Corp", 40.0),
    });

    public static Dictionary<string, BoardSecurity> DefaultBoard => new()
    {
        ["AAA"] = new("AAA", 10, 10.0, "2026-11-02"),
        ["BBB"] = new("BBB", 10, 20.0, "2026-11-02"),
    };
}
