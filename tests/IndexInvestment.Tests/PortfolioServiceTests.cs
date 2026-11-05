using IndexInvestment.Core;
using IndexInvestment.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndexInvestment.Tests;

/// <summary>
/// Data layer: undo edge cases, CSV exports, position/transaction
/// editing, imports, and per-user isolation.
/// </summary>
public sealed class PortfolioServiceTests : IAsyncLifetime
{
    private TestEnv _env = null!;

    public PortfolioServiceTests() => _env = TestEnv.Create();
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _env.DisposeAsync();

    // ============================================================
    // Undo edge cases
    // ============================================================

    [Fact]
    public async Task UndoRequiresMatchingPosition()
    {
        var runId = await _env.Portfolio.ApplyRunAsync(
            "u1", "2026-11-02", "IMOEX", 1000, "2026-11-02",
            new List<BuyRow> { new("AAA", "Aa", 60, 10, 10.0, 100.0, 0.045) },
            new List<ReportRow>(),
            CancellationToken.None);
        // delete the position the run created
        await using var db = _env.Factory.CreateDbContext();
        db.Positions.Remove(db.Positions.Find(new object?[] { "u1", "AAA" })!);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => _env.Portfolio.UndoRunAsync("u1", runId, "2026-11-03", CancellationToken.None));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public async Task UndoOversoldPositionFails()
    {
        var runId = await _env.Portfolio.ApplyRunAsync(
            "u1", "2026-11-02", "IMOEX", 1000, "2026-11-02",
            new List<BuyRow> { new("AAA", "Aa", 60, 10, 10.0, 100.0, 0.045) },
            new List<ReportRow>(),
            CancellationToken.None);
        // shrink the position below what the run bought
        await _env.Portfolio.SetPositionAsync("u1", "AAA", 5, 10.0);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => _env.Portfolio.UndoRunAsync("u1", runId, "2026-11-03", CancellationToken.None));
        Assert.Contains("has 5 shares", ex.Message);
    }

    [Fact]
    public async Task ImportedRunCannotBeUndone()
    {
        await _env.Portfolio.ImportTransactionsAsync("u1", new List<ImportTransactionRow>
        {
            new("2026-11-01", "AAA", "BUY", 10, 10.0, 100.0, 0.045),
        }, CancellationToken.None);
        Assert.Null(await _env.Portfolio.LatestUndoableRunAsync("u1"));
    }

    [Fact]
    public async Task AppliedRunOfOtherUserIsNotUndoable()
    {
        await _env.Run(1000.0, user: "u2");
        Assert.Null(await _env.Portfolio.LatestUndoableRunAsync("u1"));
        Assert.NotNull(await _env.Portfolio.LatestUndoableRunAsync("u2"));
    }

    // ============================================================
    // Position management
    // ============================================================

    [Fact]
    public async Task SetPositionUpsertsAndDeletes()
    {
        await _env.Portfolio.SetPositionAsync("u1", "AAA", 100, 5.125);
        Assert.Equal(new[] { new PositionRow("AAA", 100, 5.125) },
            await _env.Portfolio.GetPositionsAsync("u1"));

        await _env.Portfolio.SetPositionAsync("u1", "AAA", 300, 6.0);
        Assert.Equal(300, (await _env.Portfolio.GetPositionsAsync("u1")).Single().Shares);

        await _env.Portfolio.SetPositionAsync("u1", "AAA", 0, 0);
        Assert.Empty(await _env.Portfolio.GetPositionsAsync("u1"));

        await Assert.ThrowsAsync<ValidationError>(
            () => _env.Portfolio.SetPositionAsync("u1", "AAA", -1, 1.0));
        await Assert.ThrowsAsync<ValidationError>(
            () => _env.Portfolio.SetPositionAsync("u1", "", 1, 1.0));
    }

    [Fact]
    public async Task DeleteMissingPositionReportsFalse()
    {
        Assert.False(await _env.Portfolio.DeletePositionAsync("u1", "ZZZ"));
    }

    // ============================================================
    // Transaction editing (actual price correction, t-1 → real fill)
    // ============================================================

    [Fact]
    public async Task EditTransactionRecomputesAvgPrice()
    {
        var res = await _env.Run(1000.0);
        var aaa = (await _env.Portfolio.GetPositionsAsync("u1")).Single(p => p.Ticker == "AAA");
        // AAA: 50 shares @ 10, value 500, comm 0.225 → avg 10.0045
        Assert.Equal(50, aaa.Shares);
        Assert.Equal(10.0 * 1.00045, aaa.AvgPrice, 9);

        var buy = (await _env.Portfolio.GetTransactionsAsync("u1"))
            .Single(t => t.Action == "BUY" && t.Ticker == "AAA");
        // correct to the actual fill: 10.50, commission 0.30
        await _env.Portfolio.UpdateTransactionAsync("u1", buy.Id, 10.50, 0.30);

        var after = (await _env.Portfolio.GetPositionsAsync("u1")).Single(p => p.Ticker == "AAA");
        // (50·10.0045 − 500 − 0.225 + 525 + 0.30) / 50 = 525.30/50
        Assert.Equal(10.506, after.AvgPrice, 9);

        var edited = (await _env.Portfolio.GetTransactionsAsync("u1"))
            .Single(t => t.Id == buy.Id);
        Assert.Equal(10.50, edited.Price, 9);
        Assert.Equal(525.0, edited.Value, 9);
    }

    [Fact]
    public async Task EditUndoMirrorRowRejected()
    {
        await _env.Run(1000.0);
        await _env.Engine.UndoAsync("u1");
        var undoRow = (await _env.Portfolio.GetTransactionsAsync("u1"))
            .Single(t => t.Action == "UNDO" && t.Ticker == "AAA");
        await Assert.ThrowsAsync<ValidationError>(
            () => _env.Portfolio.UpdateTransactionAsync("u1", undoRow.Id, 11.0, 0.0));
    }

    [Fact]
    public async Task UndoAfterPriceEditIsConsistent()
    {
        // buy, correct the price, then undo: positions must come back to
        // exactly the pre-run state (undo reads the corrected values)
        await _env.Portfolio.SetPositionAsync("u1", "AAA", 10, 5.0);
        await _env.Run(1000.0);
        var buy = (await _env.Portfolio.GetTransactionsAsync("u1"))
            .Single(t => t.Action == "BUY" && t.Ticker == "AAA");
        await _env.Portfolio.UpdateTransactionAsync("u1", buy.Id, 9.5, 0.1);
        await _env.Engine.UndoAsync("u1");
        var after = (await _env.Portfolio.GetPositionsAsync("u1")).Single(p => p.Ticker == "AAA");
        Assert.Equal(10, after.Shares);
        Assert.Equal(5.0, after.AvgPrice, 9);
    }

    [Fact]
    public async Task EditTransactionOfOtherUserRejected()
    {
        await _env.Run(1000.0, user: "u1");
        var buy = (await _env.Portfolio.GetTransactionsAsync("u1"))
            .Single(t => t.Action == "BUY" && t.Ticker == "AAA");
        // u2 does not own the row → not found
        Assert.False(await _env.Portfolio.UpdateTransactionAsync("u2", buy.Id, 11.0, 0.0));
    }

    // ============================================================
    // Imports
    // ============================================================

    [Fact]
    public async Task ImportPositionsReplaces()
    {
        await _env.Portfolio.SetPositionAsync("u1", "AAA", 10, 5.0);
        var res = await _env.Portfolio.ImportPositionsAsync("u1", new List<ImportPositionRow>
        {
            new("AAA", 200, 7.4037),
            new("BBB", 50, 10.0),
        }, CancellationToken.None);
        Assert.Equal(2, res.RowsImported);
        Assert.Equal(
            new[]
            {
                new PositionRow("AAA", 200, 7.4037),
                new PositionRow("BBB", 50, 10.0),
            },
            await _env.Portfolio.GetPositionsAsync("u1"));
    }

    [Fact]
    public async Task ImportTransactionsReplaysBuyAndUndo()
    {
        var res = await _env.Portfolio.ImportTransactionsAsync("u1", new List<ImportTransactionRow>
        {
            new("2026-09-24", "AAA", "BUY", 100, 5.0, 500.0, 0.225),
            new("2026-10-01", "AAA", "UNDO", 50, 5.0, 250.0, 0.1125),
        }, CancellationToken.None);
        Assert.NotNull(res.RunId);
        var pos = (await _env.Portfolio.GetPositionsAsync("u1")).Single();
        Assert.Equal(50, pos.Shares);
        // (500 + 0.225 − 250 − 0.1125) / 50
        Assert.Equal(5.00225, pos.AvgPrice, 9);

        // the import is stored as a non-undoable 'imported' run
        Assert.Null(await _env.Portfolio.LatestUndoableRunAsync("u1"));
        var txs = await _env.Portfolio.GetTransactionsAsync("u1");
        Assert.Equal(2, txs.Count);
    }

    [Fact]
    public async Task ImportOversoldRollsBack()
    {
        await _env.Portfolio.SetPositionAsync("u1", "AAA", 10, 5.0);
        var ex = await Assert.ThrowsAsync<ValidationError>(() =>
            _env.Portfolio.ImportTransactionsAsync("u1", new List<ImportTransactionRow>
            {
                new("2026-10-01", "AAA", "UNDO", 20, 5.0, 100.0, 0.0),
            }, CancellationToken.None));
        Assert.Contains("Import failed", ex.Message);
        // rolled back: the pre-import position is intact
        Assert.Equal(10, (await _env.Portfolio.GetPositionsAsync("u1")).Single().Shares);
        Assert.Empty(await _env.Portfolio.GetTransactionsAsync("u1"));
    }

    [Fact]
    public async Task ImportDoesNotTouchOtherUsers()
    {
        await _env.Portfolio.ImportPositionsAsync("u1", new List<ImportPositionRow>
        {
            new("AAA", 100, 5.0),
        }, CancellationToken.None);
        Assert.Empty(await _env.Portfolio.GetPositionsAsync("u2"));
    }

    // ============================================================
    // User isolation (the whole point of the multi-user rewrite)
    // ============================================================

    [Fact]
    public async Task UsersNeverSeeEachOtherData()
    {
        await _env.Run(1000.0, user: "u1");
        await _env.Run(2000.0, user: "u2");

        Assert.Equal(2, (await _env.Portfolio.GetPositionsAsync("u1")).Count);
        Assert.Equal(2, (await _env.Portfolio.GetPositionsAsync("u2")).Count);
        Assert.Equal(2, (await _env.Portfolio.GetTransactionsAsync("u1")).Count);
        Assert.Equal(2, (await _env.Portfolio.GetTransactionsAsync("u2")).Count);
        Assert.Equal(2, (await _env.Portfolio.GetReportAsync("u1")).Count);
        Assert.Equal(2, (await _env.Portfolio.GetReportAsync("u2")).Count);

        // undo for u1 only touches u1
        await _env.Engine.UndoAsync("u1");
        Assert.Empty(await _env.Portfolio.GetPositionsAsync("u1"));
        Assert.Equal(2, (await _env.Portfolio.GetPositionsAsync("u2")).Count);
        Assert.NotNull(await _env.Portfolio.LatestUndoableRunAsync("u2"));
        Assert.Null(await _env.Portfolio.LatestUndoableRunAsync("u1"));
    }

    [Fact]
    public async Task SettingsArePerUser()
    {
        await _env.Portfolio.SaveSettingsAsync("u1", 0.05m, "MOEXBMI");
        await _env.Portfolio.SaveSettingsAsync("u2", 0.1m, "MOEXRUS");
        var s1 = await _env.Portfolio.GetSettingsAsync("u1");
        var s2 = await _env.Portfolio.GetSettingsAsync("u2");
        Assert.Equal(0.05m, s1!.CommissionPercent);
        Assert.Equal("MOEXBMI", s1.IndexId);
        Assert.Equal(0.1m, s2!.CommissionPercent);
        Assert.Equal("MOEXRUS", s2.IndexId);

        await Assert.ThrowsAsync<ValidationError>(
            () => _env.Portfolio.SaveSettingsAsync("u1", 0.05m, "BAD INDEX!"));
        await Assert.ThrowsAsync<ValidationError>(
            () => _env.Portfolio.SaveSettingsAsync("u1", -1m, "IMOEX"));
    }

    // ============================================================
    // CSV exports
    // ============================================================

    [Fact]
    public async Task PositionsCsvRoundtrip()
    {
        await _env.Portfolio.SetPositionAsync("u1", "AAA", 100, 5.125);
        var csv = await _env.Portfolio.PositionsCsvAsync("u1");
        Assert.Equal("ticker,shares,avg_price\nAAA,100,5.125\n", csv);
    }

    [Fact]
    public async Task EmptyExportsStillHaveHeaders()
    {
        Assert.StartsWith("ticker,shares,avg_price", await _env.Portfolio.PositionsCsvAsync("u1"));
        Assert.StartsWith("date,ticker,action", await _env.Portfolio.TransactionsCsvAsync("u1"));
        Assert.StartsWith("ticker,shortnames", await _env.Portfolio.CompositionCsvAsync("u1"));
    }

    [Fact]
    public async Task TransactionsCsvIsChronological()
    {
        await _env.Run(1000.0);
        var csv = (await _env.Portfolio.TransactionsCsvAsync("u1")).Split('\n');
        Assert.Equal("date,ticker,action,shares,price,value,commission", csv[0]);
        // AAA bought first (applied run inserts in buy order)
        Assert.Equal(3, csv.Length - 1);
    }
}
