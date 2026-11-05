using IndexInvestment.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndexInvestment.Tests;

/// <summary>
/// End-to-end tests for run / undo with a fake market and a temp DB
/// (port of the Python pipeline suite).
/// </summary>
public sealed class PipelineTests : IAsyncLifetime
{
    private TestEnv _env = null!;

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _env.DisposeAsync();

    public PipelineTests()
    {
        _env = TestEnv.Create();
    }

    [Fact]
    public async Task ApplyRunMath()
    {
        var res = await _env.Run(1000.0);
        Assert.False(res.DryRun);
        Assert.False(res.Carried);

        var pos = _env.Portfolio;
        var positions = await pos.GetPositionsAsync("u1");
        var aaa = positions.Single(p => p.Ticker == "AAA");
        var bbb = positions.Single(p => p.Ticker == "BBB");
        // floor [5, 1] lots + one greedy BBB lot: AAA 50 shares, BBB 20 shares
        Assert.Equal(50, aaa.Shares);
        Assert.Equal(20, bbb.Shares);
        // avg_price includes commission: price * (1 + 0.00045)
        Assert.Equal(10.0 * 1.00045, aaa.AvgPrice, 9);
        Assert.Equal(20.0 * 1.00045, bbb.AvgPrice, 9);
        Assert.Equal(900.0, res.StockBought, 9);
        Assert.Equal(900.0 * 0.00045, res.CommissionPaid, 9);
        Assert.Equal(1000.0 - 900.0 - 900.0 * 0.00045, res.Leftover, 9);

        // audit log: one BUY row per ticker (newest first)
        var txs = await pos.GetTransactionsAsync("u1");
        Assert.Equal(new[] { "BUY", "BUY" }, txs.Select(t => t.Action).ToArray());
        Assert.Equal(2, txs.Count);

        // report stored and readable
        var report = await pos.GetReportAsync("u1");
        Assert.Equal(2, report.Count);
        var r = report.Single(x => x.Ticker == "AAA");
        Assert.Equal(50, r.BuyShares);
        Assert.Equal("Aa Corp", r.Shortnames);
        Assert.Equal(10.0, r.PriceRub!.Value, 9);
        Assert.Equal(10, r.LotSize!.Value);
    }

    [Fact]
    public async Task ApplyThenUndoRestoresExactState()
    {
        var before = await _env.Portfolio.GetPositionsAsync("u1");
        var res = await _env.Run(1000.0);
        Assert.Equal(2, res.Buys.Count);
        Assert.Equal(new[] { "AAA", "BBB" },
            (await _env.Portfolio.GetPositionsAsync("u1")).Select(p => p.Ticker).ToArray());

        var undoRes = await _env.Engine.UndoAsync("u1");
        Assert.Equal(2, undoRes.NReversed);

        // positions identical to the pre-run state
        Assert.Equal(before, await _env.Portfolio.GetPositionsAsync("u1"));

        // audit log kept both the BUYs and the mirror UNDOs
        var actions = (await _env.Portfolio.GetTransactionsAsync("u1")).Select(t => t.Action);
        Assert.Equal(2, actions.Count(a => a == "BUY"));
        Assert.Equal(2, actions.Count(a => a == "UNDO"));

        // the run is marked undone and is no longer the undo target
        Assert.Null(await _env.Portfolio.LatestUndoableRunAsync("u1"));
        await Assert.ThrowsAsync<ConflictException>(() => _env.Engine.UndoAsync("u1"));
    }

    [Fact]
    public async Task UndoOfRunThatExtendedExistingPosition()
    {
        // pre-existing AAA position that the run adds to
        await _env.Portfolio.SetPositionAsync("u1", "AAA", 100, 8.0);
        var res = await _env.Run(1000.0);
        Assert.NotEmpty(res.Buys);
        var pos = (await _env.Portfolio.GetPositionsAsync("u1")).Single(p => p.Ticker == "AAA");
        Assert.True(pos.Shares > 100);

        await _env.Engine.UndoAsync("u1");
        var after = (await _env.Portfolio.GetPositionsAsync("u1")).Single(p => p.Ticker == "AAA");
        Assert.Equal(100, after.Shares);
        // average cost restored exactly (undo subtracts value + commission)
        Assert.Equal(8.0, after.AvgPrice, 9);
    }

    [Fact]
    public async Task DryRunWritesNothing()
    {
        var res = await _env.Run(1000.0, dry: true);
        Assert.True(res.DryRun);
        Assert.Equal(2, res.Buys.Count);
        Assert.Empty(await _env.Portfolio.GetPositionsAsync("u1"));
        Assert.Empty(await _env.Portfolio.GetTransactionsAsync("u1"));
        Assert.Null(await _env.Portfolio.LatestUndoableRunAsync("u1"));
    }

    [Fact]
    public async Task CarryUnderThreshold()
    {
        // 50 RUB buys less than one lot of the cheapest ticker (100 RUB)
        var res = await _env.Run(50.0, dry: true);
        Assert.True(res.Carried);
        Assert.Empty(res.Buys);
        Assert.Equal(50.0, res.Leftover, 9);
    }

    [Fact]
    public async Task StalePriceRejected()
    {
        _env.Fake.Composition = new("2026-11-02", new[]
        {
            new IndexConstituent("AAA", "Aa", 100.0),
        });
        _env.Fake.Board = new Dictionary<string, BoardSecurity>
        {
            ["AAA"] = new("AAA", 10, 10.0, "2026-09-01"),
        };
        var ex = await Assert.ThrowsAsync<MarketDataException>(
            () => _env.Market.GetSnapshotAsync("IMOEX", maxAgeSeconds: 0));
        Assert.Contains("last session", ex.Message);
    }

    [Fact]
    public async Task MissingBoardTickerRejected()
    {
        _env.Fake.Board = new Dictionary<string, BoardSecurity>();
        var ex = await Assert.ThrowsAsync<MarketDataException>(
            () => _env.Market.GetSnapshotAsync("IMOEX", maxAgeSeconds: 0));
        Assert.Contains("not found on TQBR board", ex.Message);
    }

    [Fact]
    public async Task OverlappingRunRejected()
    {
        // hold the run gate and verify run() refuses instead of waiting
        var gate = new SemaphoreSlim(1, 1);
        gate.Wait();
        _env.Engine.RunGateOverride = gate;
        try
        {
            await Assert.ThrowsAsync<ConflictException>(
                () => _env.Run(1000.0, dry: true));
        }
        finally
        {
            _env.Engine.RunGateOverride = null;
            gate.Dispose();
        }
    }

    [Fact]
    public async Task PortfolioViewMatchesHoldings()
    {
        await _env.Portfolio.SetPositionAsync("u1", "AAA", 10, 9.0);
        var view = await _env.Engine.GetPortfolioAsync("u1", "IMOEX", 0.0);
        Assert.Equal(100.0, view.PortfolioValue, 9);
        Assert.Equal(90.0, view.CostBasis, 9);
        Assert.Equal(1, view.NHeld);
        Assert.Equal(2, view.NIndex);
        // sorted by value desc
        Assert.Equal("AAA", view.Rows[0].Ticker);
        Assert.Equal(10, view.Rows[0].Shares);
    }
}
