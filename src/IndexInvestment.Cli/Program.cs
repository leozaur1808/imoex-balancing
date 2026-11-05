using IndexInvestment.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

// ============================================================
// imoex — small console wrapper around the same core as the webapp.
//
//   dotnet run --project src/IndexInvestment.Cli -- run  --user <email> --amount 10000 [--dry-run]
//   dotnet run --project src/IndexInvestment.Cli -- undo --user <email>
//   dotnet run --project src/IndexInvestment.Cli -- list --user <email>
//
// Same database as the webapp (data/app.db by default, --db to
// override). Overlapping webapp + CLI writers are serialized by the
// database.
// ============================================================

var argv = Environment.GetCommandLineArgs().Skip(1).ToList();
if (argv.Count == 0) return Help();
var command = argv[0];

string? userArg = null;
string? amountArg = null;
string? dbArg = null;
bool dryRun = false;

for (var i = 1; i < argv.Count; i++)
{
    switch (argv[i])
    {
        case "--user": userArg = Take(++i); break;
        case "--amount": amountArg = Take(++i); break;
        case "--db": dbArg = Take(++i); break;
        case "--dry-run": dryRun = true; break;
        case "--help": return Help();
        default:
            Console.Error.WriteLine($"Unknown argument: {argv[i]}");
            return Help();
    }
}

string Take(int idx)
{
    if (idx >= argv.Count) { Console.Error.WriteLine("Missing value for flag"); Environment.Exit(2); }
    return argv[idx];
}

if (userArg is null)
{
    Console.Error.WriteLine("--user <email> is required");
    return Help();
}

var connStr = dbArg ?? DbOptions.DefaultSqliteConnectionString;
var options = DbOptions.Sqlite(connStr);

// Resolve the user by email (or user id).
string userId;
var indexId = "IMOEX";
double commission = 0.00045;
{
    await using var db = new AppDbContext(options);
    var user = await db.Users.FirstOrDefaultAsync(u =>
        u.Email == userArg || u.UserName == userArg || u.Id == userArg);
    if (user is null)
    {
        Console.Error.WriteLine($"No user '{userArg}' (email or id).");
        return 1;
    }
    userId = user.Id;
    var s = await db.UserSettings.FindAsync(userId);
    if (s is not null)
    {
        indexId = s.IndexId;
        commission = (double)(s.CommissionPercent / 100m);
    }
}

var market = new MarketDataService(
    new MoexClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
        NullLogger<MoexClient>.Instance),
    new SnapshotCache(),
    NullLogger<MarketDataService>.Instance);
var portfolio = new PortfolioService(new SimpleFactory(options), NullLogger<PortfolioService>.Instance);
var engine = new DcaEngine(market, portfolio, NullLogger<DcaEngine>.Instance);

switch (command)
{
    case "run":
        if (amountArg is null && !dryRun)
        {
            Console.Write("Amount to invest this period (RUB): ");
            amountArg = Console.ReadLine();
        }
        var amount = amountArg is null ? 10_000 : double.Parse(amountArg);
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"  DCA run — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"  User  : {userArg}");
        Console.WriteLine($"  Index : {indexId}");
        Console.WriteLine($"  Cash  : {amount:0.##} RUB");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        RunResult res;
        try
        {
            res = await engine.RunAsync(userId, indexId, commission, amount, dryRun, 0.0);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"✗ Run failed: {e.Message}");
            return 1;
        }

        Console.WriteLine($"Current portfolio value : {res.PortfolioValue:0.##} RUB");
        Console.WriteLine($"After contribution      : {res.TotalAfter:0.##} RUB");
        Console.WriteLine();
        if (res.Carried && res.CarryReason is not null)
            Console.WriteLine($"⚠ {res.CarryReason}");
        Console.WriteLine("─── BUY ORDERS ───────────────────────────────────────");
        if (res.Buys.Count == 0)
            Console.WriteLine("(none)");
        else
        {
            Console.WriteLine("{0,-8} {1,-20} {2,8} {3,8} {4,10} {5,14} {6,10}",
                "ticker", "name", "idx_w%", "shares", "price", "value_rub", "comm_rub");
            foreach (var b in res.Buys)
                Console.WriteLine("{0,-8} {1,-20} {2,8:0.00} {3,8} {4,10:0.00} {5,14:0.00} {6,10:0.0000}",
                    b.Ticker, Truncate(b.Name, 20), b.Weight, b.Shares, b.Price, b.Value, b.Commission);
        }
        Console.WriteLine();
        Console.WriteLine($"Stock value bought : {res.StockBought,15:0.##} RUB");
        Console.WriteLine($"Commission paid    : {res.CommissionPaid,15:0.####} RUB");
        Console.WriteLine($"Total spent        : {res.TotalSpent,15:0.##} RUB");
        Console.WriteLine($"Leftover carried   : {res.Leftover,15:0.##} RUB ({res.Leftover / amount * 100:0.000}%)");
        if (res.L1Error is { } l1)
            Console.WriteLine($"Post-trade L1 weight error : {l1:0.0000}% (half-sum, 0 = perfect)");
        if (dryRun)
        {
            Console.WriteLine("\n[dry-run] nothing written.");
            return 0;
        }
        Console.WriteLine($"\n✓ Run #{res.RunId} applied   (undo: {AppName()} undo --user {userArg})");
        return 0;

    case "undo":
        try
        {
            var undoRes = await engine.UndoAsync(userId);
            Console.WriteLine($"✓ Run of {undoRes.Date} ({undoRes.Amount:0.##} RUB) undone —");
            Console.WriteLine($"  positions restored to their pre-run state ({undoRes.NReversed} buys reversed).");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"✗ {e.Message}");
            return 1;
        }

    case "list":
        var positions = await portfolio.GetPositionsAsync(userId);
        if (positions.Count == 0)
        {
            Console.WriteLine("(no positions)");
            return 0;
        }
        Console.WriteLine("{0,-8} {1,10} {2,12}", "ticker", "shares", "avg_price");
        foreach (var p in positions)
            Console.WriteLine($"{p.Ticker,-8} {p.Shares,10} {p.AvgPrice,12:0.####}");
        return 0;

    default:
        return Help();
}

static string AppName()
{
    var exe = Environment.GetCommandLineArgs().FirstOrDefault() ?? "";
    return Path.GetFileNameWithoutExtension(exe);
}

static int Help()
{
    Console.WriteLine(
        "imoex — DCA CLI (same engine and database as the webapp)\n" +
        "\n" +
        "  imoex run  --user <email> --amount <rub> [--dry-run]   one DCA cycle\n" +
        "  imoex undo --user <email>                              reverse the last applied run\n" +
        "  imoex list --user <email>                              show current positions\n" +
        "\n" +
        "  [--db <connection-string>]   default: data/app.db (run from the repo root)\n");
    return 2;
}

static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

/// <summary>Minimal IDbContextFactory so the CLI avoids a full DI container.</summary>
sealed class SimpleFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);
}
