using IndexInvestment.Core.Entities;
using Microsoft.Extensions.Logging;

namespace IndexInvestment.Core;

/// <summary>
/// One DCA cycle: market snapshot → current positions → index-
/// proportional targets → greedy allocation → (dry run | atomic
/// persist). DCA mode never sells; if fewer than CarryThreshold
/// tickers fit in the budget the whole contribution is carried over.
///
/// Ported 1:1 from the Python dca.py pipeline.
/// </summary>
public sealed class DcaEngine
{
    public const int CarryThreshold = 1;

    // Serializes overlapping runs inside this process. Across processes
    // (webapp + CLI) the database write transaction is the hard
    // guarantee; this gate just turns the wait into a friendly error.
    private readonly SemaphoreSlim _runGate = new(1, 1);
    internal SemaphoreSlim? RunGateOverride;   // test hook

    private readonly MarketDataService _market;
    private readonly PortfolioService _portfolio;
    private readonly ILogger<DcaEngine> _logger;

    public DcaEngine(MarketDataService market, PortfolioService portfolio, ILogger<DcaEngine> logger)
    {
        _market = market;
        _portfolio = portfolio;
        _logger = logger;
    }

    public async Task<RunResult> RunAsync(
        string userId,
        string indexId,
        double commission,        // fraction, e.g. 0.00045 for 0.045%
        double newCash,
        bool dryRun,
        double maxAgeSeconds,     // 0 → force a fresh fetch
        CancellationToken ct = default)
    {
        if (newCash <= 0)
            throw new ValidationError("Amount must be positive");
        var gate = RunGateOverride ?? _runGate;
        if (!gate.Wait(0))
            throw new ConflictException("Another run is already in progress");
        try
        {
            return await RunLockedAsync(userId, indexId, commission, newCash, dryRun, maxAgeSeconds, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RunResult> RunLockedAsync(
        string userId, string indexId, double commission, double newCash,
        bool dryRun, double maxAgeSeconds, CancellationToken ct)
    {
        // --- 1. Market snapshot (composition + prices + lot sizes) ---
        var snapshot = await _market.GetSnapshotAsync(indexId, maxAgeSeconds, ct);
        var rows = snapshot.Rows;
        var n = rows.Count;

        // --- 2. Load current positions ---
        var positions = await _portfolio.GetPositionMapAsync(userId, ct);
        var currentShares = new long[n];
        var currentValue = new double[n];
        var costBasis = new double[n];
        for (var i = 0; i < n; i++)
        {
            if (positions.TryGetValue(rows[i].Ticker, out var p))
            {
                currentShares[i] = p.Shares;
                currentValue[i] = p.Shares * rows[i].Price;
                costBasis[i] = p.Shares * p.AvgPrice;
            }
        }

        var portfolioValue = currentValue.Sum();
        var totalAfter = portfolioValue + newCash;

        // --- 3. Target end-state stock value per ticker ---
        var weightSum = 0.0;
        foreach (var r in rows) weightSum += r.Weight;
        if (weightSum <= 0)
            throw new MarketDataException($"Index {indexId}: weights sum to zero");
        var targetEnd = new double[n];
        for (var i = 0; i < n; i++)
            targetEnd[i] = (rows[i].Weight / weightSum) * (totalAfter / (1 + commission));

        // --- 4. Positive gaps only (never sell in DCA mode) ---
        var gaps = new double[n];
        var gapSum = 0.0;
        for (var i = 0; i < n; i++)
        {
            gaps[i] = Math.Max(targetEnd[i] - currentValue[i], 0);
            gapSum += gaps[i];
        }

        var investable = newCash / (1 + commission);

        var carried = false;
        string? carryReason = null;
        var buyShares = new long[n];

        if (gapSum <= 0)
        {
            carried = true;
            carryReason = "Portfolio already at or above target — carrying cash.";
        }
        else
        {
            // scale gaps so they sum to investable
            var scaledTargets = new double[n];
            for (var i = 0; i < n; i++) scaledTargets[i] = gaps[i] * (investable / gapSum);

            var prices = rows.Select(r => r.Price).ToArray();
            var lots = rows.Select(r => r.LotSize).ToArray();

            // --- 5. Allocate ---
            var alloc = Allocator.Allocate(prices, lots, scaledTargets, investable, commission);
            for (var i = 0; i < n; i++) buyShares[i] = alloc.BuyShares[i];

            var nBuys = buyShares.Count(s => s > 0);

            // --- 6. Carry-forward check ---
            if (nBuys < CarryThreshold)
            {
                carried = true;
                carryReason = $"Only {nBuys} ticker(s) fit in budget (threshold {CarryThreshold}). " +
                              "Carrying cash to next period.";
                for (var i = 0; i < n; i++) buyShares[i] = 0;
            }
        }

        // --- 7. Summary ---
        var stockBought = 0.0;
        for (var i = 0; i < n; i++) stockBought += buyShares[i] * rows[i].Price;
        var commPaid = stockBought * commission;
        var totalSpent = stockBought + commPaid;
        var leftover = newCash - totalSpent;

        var buyRows = new List<BuyRow>();
        for (var i = 0; i < n; i++)
        {
            if (buyShares[i] <= 0) continue;
            buyRows.Add(new BuyRow(
                rows[i].Ticker, rows[i].Name, rows[i].Weight,
                buyShares[i], rows[i].Price, buyShares[i] * rows[i].Price,
                buyShares[i] * rows[i].Price * commission));
        }
        buyRows.Sort((a, b) => b.Value.CompareTo(a.Value));

        // --- 8. Tracking metrics ---
        // post_value is kept for the report in both branches (with no
        // buys it equals the current value)
        var postValue = new double[n];
        var postTotal = 0.0;
        for (var i = 0; i < n; i++)
        {
            postValue[i] = (currentShares[i] + buyShares[i]) * rows[i].Price;
            postTotal += postValue[i];
        }

        double? l1Error = null;
        var reportRows = new List<ReportRow>(n);
        if (buyRows.Count > 0)
        {
            var l1 = 0.0;
            for (var i = 0; i < n; i++)
            {
                var postWeight = postValue[i] / postTotal * 100;
                var targetW = rows[i].Weight / weightSum * 100;
                l1 += Math.Abs(postWeight - targetW);
                reportRows.Add(MakeReportRow(rows[i], i, commission, currentShares, currentValue,
                    costBasis, buyShares, postValue, postWeight, targetW, postWeight - targetW));
            }
            l1Error = l1 / 2;   // half-sum convention
        }
        else
        {
            // no buys: post_weight / weight_diff stay zero, like the
            // original implementation
            for (var i = 0; i < n; i++)
            {
                var targetW = rows[i].Weight / weightSum * 100;
                reportRows.Add(MakeReportRow(rows[i], i, commission, currentShares, currentValue,
                    costBasis, buyShares, postValue, postWeight: 0.0, targetW, weightDiff: 0.0));
            }
        }

        // --- 9. Persist ---
        if (dryRun)
        {
            return new RunResult
            {
                TradeDate = snapshot.TradeDate,
                PortfolioValue = portfolioValue,
                TotalAfter = totalAfter,
                Buys = buyRows,
                StockBought = stockBought,
                CommissionPaid = commPaid,
                TotalSpent = totalSpent,
                Leftover = leftover,
                L1Error = l1Error,
                Carried = carried,
                CarryReason = carryReason,
                Rows = n,
                DryRun = true,
            };
        }

        var runId = await _portfolio.ApplyRunAsync(
            userId,
            Dates.Today(),
            indexId,
            newCash,
            snapshot.TradeDate,
            buyRows,
            reportRows,
            ct);
        _logger.LogInformation(
            "User {User}: run {RunId} applied: {Cash:F2} RUB, {Buys} buys, {Leftover:F2} RUB leftover",
            userId, runId, newCash, buyRows.Count, leftover);
        if (carried)
            _logger.LogWarning("User {User}: run {RunId} carried cash: {Reason}", userId, runId, carryReason);

        return new RunResult
        {
            TradeDate = snapshot.TradeDate,
            PortfolioValue = portfolioValue,
            TotalAfter = totalAfter,
            Buys = buyRows,
            StockBought = stockBought,
            CommissionPaid = commPaid,
            TotalSpent = totalSpent,
            Leftover = leftover,
            L1Error = l1Error,
            Carried = carried,
            CarryReason = carryReason,
            Rows = n,
            DryRun = false,
            RunId = runId,
        };
    }

    private static ReportRow MakeReportRow(
        Constituent r, int i, double commission,
        long[] currentShares, double[] currentValue, double[] costBasis,
        long[] buyShares, double[] postValue,
        double postWeight, double targetW, double weightDiff)
    {
        var buyValue = buyShares[i] * r.Price;
        return new ReportRow(
            r.Ticker, r.Name, r.Weight, r.Price, (int)r.LotSize,
            (int)currentShares[i], currentValue[i], costBasis[i],
            (int)buyShares[i], buyValue, buyValue * commission,
            postValue[i], postWeight, targetW, weightDiff);
    }

    /// <summary>
    /// Reverse the most recent applied run for a user: restore the
    /// pre-run positions, mark the run undone, and append UNDO rows to
    /// the audit log. Only the latest applied run can be undone at a
    /// time; after an undo the previous run becomes the target.
    /// </summary>
    /// <summary>
    /// Current portfolio state: the index composition enriched with
    /// prices and the current holdings. No buys are made. Market data
    /// comes from the cached snapshot; positions are always read fresh.
    /// </summary>
    public async Task<PortfolioView> GetPortfolioAsync(
        string userId, string indexId, double maxAgeSeconds, CancellationToken ct = default)
    {
        var snapshot = await _market.GetSnapshotAsync(indexId, maxAgeSeconds, ct);
        var positions = await _portfolio.GetPositionMapAsync(userId, ct);

        var rows = new List<PortfolioRow>(snapshot.Rows.Count);
        var portfolioValue = 0.0;
        var costBasis = 0.0;
        var nHeld = 0;
        foreach (var r in snapshot.Rows)
        {
            long shares = 0;
            double avg = 0;
            if (positions.TryGetValue(r.Ticker, out var p))
            {
                shares = p.Shares;
                avg = p.AvgPrice;
                if (shares > 0) nHeld++;
            }
            var value = shares * r.Price;
            var cb = shares * avg;
            portfolioValue += value;
            costBasis += cb;
            rows.Add(new PortfolioRow
            {
                Ticker = r.Ticker,
                Name = r.Name,
                Weight = r.Weight,
                Price = r.Price,
                Shares = shares,
                Value = value,
                CostBasis = cb,
            });
        }
        rows = rows.OrderByDescending(r => r.Value).ToList();
        return new PortfolioView
        {
            TradeDate = snapshot.TradeDate,
            Rows = rows,
            PortfolioValue = portfolioValue,
            CostBasis = costBasis,
            NHeld = nHeld,
            NIndex = snapshot.Rows.Count,
        };
    }

    public async Task<UndoResult> UndoAsync(string userId, CancellationToken ct = default)
    {
        var gate = RunGateOverride ?? _runGate;
        if (!gate.Wait(0))
            throw new ConflictException("Another run is already in progress");
        try
        {
            var target = await _portfolio.LatestUndoableRunAsync(userId, ct);
            if (target is null)
                throw new ConflictException("Nothing to undo — no applied runs");
            var res = await _portfolio.UndoRunAsync(userId, target.Id, Dates.Today(), ct);
            _logger.LogInformation(
                "User {User}: run {RunId} undone ({Date}, {Amount} RUB), {Count} buys reversed",
                userId, res.Id, res.Date, res.Amount ?? 0, res.NReversed);
            return res;
        }
        finally
        {
            gate.Release();
        }
    }
}
