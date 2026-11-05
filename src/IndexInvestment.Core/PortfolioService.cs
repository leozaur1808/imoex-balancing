using IndexInvestment.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IndexInvestment.Core;

/// <summary>
/// All portfolio state for all users, in one SQLite database:
/// positions, runs, transactions (audit log), run reports.
///
/// An applied run is committed as a single database transaction, so
/// two overlapping writers (webapp + CLI, separate processes) can
/// never both apply — the second one waits on the database lock.
/// </summary>
public sealed class PortfolioService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<PortfolioService> _logger;

    public PortfolioService(IDbContextFactory<AppDbContext> factory, ILogger<PortfolioService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // ============================================================
    // Readers
    // ============================================================

    public async Task<Dictionary<string, (long Shares, double AvgPrice)>> GetPositionMapAsync(
        string userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Positions
            .Where(p => p.UserId == userId)
            .Select(p => new { p.Ticker, p.Shares, p.AvgPrice })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Ticker, r => (r.Shares, r.AvgPrice));
    }

    public async Task<List<PositionRow>> GetPositionsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Positions
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.Ticker)
            .Select(p => new PositionRow(p.Ticker, p.Shares, p.AvgPrice))
            .ToListAsync(ct);
    }

    public async Task<List<TransactionRow>> GetTransactionsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Transactions
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.Id)
            .Select(t => new TransactionRow(t.Id, t.Date, t.Ticker, t.Action, t.Shares, t.Price, t.Value, t.Commission))
            .ToListAsync(ct);
    }

    public async Task<List<ReportRowDto>> GetReportAsync(string userId, CancellationToken ct = default)
    {
        // Per-ticker report of the most recent non-undone run (an
        // applied run, or an imported one — the analogue of the old
        // 'legacy' run), in original row order.
        await using var db = await _factory.CreateDbContextAsync(ct);
        var run = await db.Runs
            .Where(r => r.UserId == userId
                        && (r.Status == RunStatus.Applied || r.Status == RunStatus.Imported))
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (run is null) return new List<ReportRowDto>();
        return await db.RunReports
            .Where(x => x.RunId == run.Id)
            .OrderBy(x => x.Id)
            .Select(x => new ReportRowDto(
                x.Ticker, x.Name, x.Weight, x.Price, x.LotSize,
                x.CurrentShares, x.CurrentValue, x.CostBasis,
                x.BuyShares, x.BuyValue, x.BuyComm,
                x.PostValue, x.PostWeight, x.TargetW, x.WeightDiff))
            .ToListAsync(ct);
    }

    public async Task<UndoTarget?> LatestUndoableRunAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var run = await db.Runs
            .Where(r => r.UserId == userId && r.Status == RunStatus.Applied)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        return run is null
            ? null
            : new UndoTarget { Id = run.Id, Date = run.Date, Amount = run.Amount, TradeDate = run.TradeDate };
    }

    public async Task<UserSettings?> GetSettingsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.UserSettings.FindAsync(new object[] { userId }, ct);
    }

    public async Task SaveSettingsAsync(string userId, decimal commissionPercent, string indexId, CancellationToken ct = default)
    {
        ValidateIndexId(indexId);
        if (commissionPercent < 0 || commissionPercent >= 100)
            throw new ValidationError("Commission must be a percentage in [0, 100)");
        await using var db = await _factory.CreateDbContextAsync(ct);
        var s = await db.UserSettings.FindAsync(new object[] { userId }, ct);
        if (s is null)
        {
            db.UserSettings.Add(new UserSettings
            {
                UserId = userId,
                CommissionPercent = commissionPercent,
                IndexId = indexId,
            });
        }
        else
        {
            s.CommissionPercent = commissionPercent;
            s.IndexId = indexId;
        }
        await db.SaveChangesAsync(ct);
    }

    public static void ValidateIndexId(string indexId)
    {
        if (string.IsNullOrWhiteSpace(indexId) || indexId.Length > 32
            || indexId.Any(c => !char.IsLetterOrDigit(c)))
            throw new ValidationError(
                "Index id must be 1-32 letters/digits (e.g. IMOEX, MOEXBMI, MOEXRUS)");
    }

    // ============================================================
    // Writers — one DCA run / one undo, each a single transaction
    // ============================================================

    public async Task<int> ApplyRunAsync(
        string userId, string runDate, string? indexId, double amount, string? tradeDate,
        List<BuyRow> buys, List<ReportRow> reportRows, CancellationToken ct = default)
    {
        // Commit one applied DCA run atomically: run row + BUY audit
        // rows + position updates + per-ticker report.
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var run = new Run
            {
                UserId = userId,
                Date = runDate,
                Amount = amount,
                TradeDate = tradeDate,
                IndexId = indexId,
                Status = RunStatus.Applied,
            };
            db.Runs.Add(run);
            await db.SaveChangesAsync(ct);

            foreach (var b in buys)
            {
                db.Transactions.Add(new Transaction
                {
                    UserId = userId,
                    RunId = run.Id,
                    Date = runDate,
                    Ticker = b.Ticker,
                    Action = TransactionAction.Buy,
                    Shares = b.Shares,
                    Price = b.Price,
                    Value = b.Value,
                    Commission = b.Commission,
                });
                ApplyPosition(db, userId, b.Ticker, b.Shares, b.Value, b.Commission);
            }

            if (reportRows.Count > 0)
            {
                db.RunReports.AddRange(reportRows.Select(r => new RunReport
                {
                    RunId = run.Id,
                    Ticker = r.Ticker,
                    Name = r.Name,
                    Weight = r.Weight,
                    Price = r.Price,
                    LotSize = r.LotSize,
                    CurrentShares = r.CurrentShares,
                    CurrentValue = r.CurrentValue,
                    CostBasis = r.CostBasis,
                    BuyShares = r.BuyShares,
                    BuyValue = r.BuyValue,
                    BuyComm = r.BuyComm,
                    PostValue = r.PostValue,
                    PostWeight = r.PostWeight,
                    TargetW = r.TargetW,
                    WeightDiff = r.WeightDiff,
                }));
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return run.Id;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<UndoResult> UndoRunAsync(
        string userId, int runId, string undoDate, CancellationToken ct = default)
    {
        // Reverse an applied run (one step back, git-style): restore
        // the pre-run positions, mark the run 'undone', append UNDO
        // rows to the audit log. Fails if the run is not applied or
        // the state no longer matches (position missing / oversold).
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var run = await db.Runs
                .FirstOrDefaultAsync(r => r.Id == runId && r.UserId == userId, ct);
            if (run is null || run.Status != RunStatus.Applied)
                throw new ConflictException($"Run {runId} is not an applied run");

            var buys = await db.Transactions
                .Where(t => t.RunId == runId && t.Action == TransactionAction.Buy)
                .OrderBy(t => t.Id)
                .Select(t => new { t.Ticker, t.Shares, t.Value, t.Commission })
                .ToListAsync(ct);

            foreach (var b in buys)
            {
                var pos = await db.Positions
                    .FirstOrDefaultAsync(p => p.UserId == userId && p.Ticker == b.Ticker, ct);
                if (pos is null)
                    throw new ConflictException(
                        $"Cannot undo run {runId}: position for {b.Ticker} is missing");
                var newShares = pos.Shares - b.Shares;
                if (newShares < 0)
                    throw new ConflictException(
                        $"Cannot undo run {runId}: {b.Ticker} has {pos.Shares} shares, " +
                        $"but the run bought {b.Shares}");
                if (newShares == 0)
                {
                    db.Positions.Remove(pos);
                }
                else
                {
                    // avg' = (old_sh·old_avg − value − comm) / new_sh;
                    // this restores the pre-run value exactly
                    var oldCost = pos.Shares * pos.AvgPrice;
                    pos.Shares = newShares;
                    pos.AvgPrice = (oldCost - b.Value - b.Commission) / newShares;
                }
            }

            run.Status = RunStatus.Undone;
            foreach (var b in buys)
            {
                db.Transactions.Add(new Transaction
                {
                    UserId = userId,
                    RunId = runId,
                    Date = undoDate,
                    Ticker = b.Ticker,
                    Action = TransactionAction.Undo,
                    Shares = b.Shares,
                    Price = 0,
                    Value = b.Value,
                    Commission = b.Commission,
                });
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new UndoResult
            {
                Id = runId,
                Date = run.Date,
                Amount = run.Amount,
                TradeDate = run.TradeDate,
                NReversed = buys.Count,
            };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static void ApplyPosition(AppDbContext db, string userId, string ticker,
        long shares, double value, double comm)
    {
        // avg_price = total cost (incl. commission) / total shares —
        // the same math the original CSV flow used.
        var pos = db.Positions.Find(new object?[] { userId, ticker });
        if (pos is null)
        {
            db.Positions.Add(new Position
            {
                UserId = userId,
                Ticker = ticker,
                Shares = shares,
                AvgPrice = (value + comm) / shares,
            });
        }
        else
        {
            var oldCost = pos.Shares * pos.AvgPrice;
            var newShares = pos.Shares + shares;
            pos.Shares = newShares;
            pos.AvgPrice = (oldCost + value + comm) / newShares;
        }
    }

    // ============================================================
    // Portfolio management (multi-user: each user edits only theirs)
    // ============================================================

    /// <summary>
    /// Add or replace a position. shares = 0 removes it. This is how a
    /// user fixes holdings that did not come from an applied run.
    /// </summary>
    public async Task SetPositionAsync(
        string userId, string ticker, long shares, double avgPrice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker)) throw new ValidationError("Ticker is required");
        if (shares < 0) throw new ValidationError("Shares must be >= 0");
        if (avgPrice < 0) throw new ValidationError("Average price must be >= 0");
        if (shares > 0 && shares > long.MaxValue / 1_000_000) throw new ValidationError("Too many shares");

        await using var db = await _factory.CreateDbContextAsync(ct);
        var pos = await db.Positions
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Ticker == ticker, ct);
        if (shares == 0)
        {
            if (pos is not null)
            {
                db.Positions.Remove(pos);
                await db.SaveChangesAsync(ct);
            }
            return;
        }
        if (pos is null)
        {
            db.Positions.Add(new Position { UserId = userId, Ticker = ticker, Shares = shares, AvgPrice = avgPrice });
        }
        else
        {
            pos.Shares = shares;
            pos.AvgPrice = avgPrice;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeletePositionAsync(string userId, string ticker, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pos = await db.Positions
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Ticker == ticker, ct);
        if (pos is null) return false;
        db.Positions.Remove(pos);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Correct the *actual* price / commission of an executed BUY row
    /// (the run prices are last legal closes, t-1 — the real fill can
    /// differ). The position's average price is recomputed:
    ///
    ///   avg' = (shares·avg − old_value − old_comm + new_value + new_comm) / shares
    ///
    /// Undo math stays consistent because it reads the current
    /// transaction values. Only BUY rows can be edited (UNDO rows are
    /// mirrors).
    /// </summary>
    public async Task<bool> UpdateTransactionAsync(
        string userId, int transactionId, double newPrice, double newCommission, CancellationToken ct = default)
    {
        if (newPrice <= 0) throw new ValidationError("Price must be positive");
        if (newCommission < 0) throw new ValidationError("Commission must be >= 0");

        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var t = await db.Transactions
                .FirstOrDefaultAsync(x => x.Id == transactionId && x.UserId == userId, ct);
            if (t is null) return false;
            if (t.Action != TransactionAction.Buy)
                throw new ValidationError("Only BUY rows can be edited");

            var pos = await db.Positions
                .FirstOrDefaultAsync(p => p.UserId == userId && p.Ticker == t.Ticker, ct);
            if (pos is null)
                throw new ConflictException(
                    $"Cannot edit transaction: position for {t.Ticker} is missing");

            var oldValue = t.Value;
            var oldComm = t.Commission;
            t.Price = newPrice;
            t.Value = newPrice * t.Shares;
            t.Commission = newCommission;
            pos.AvgPrice = (pos.Shares * pos.AvgPrice - oldValue - oldComm + t.Value + t.Commission) / pos.Shares;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ============================================================
    // Imports — one-time user-initiated, editable afterwards
    // ============================================================

    /// <summary>
    /// Import current holdings: each row REPLACES the position for
    /// that ticker (so re-importing a full portfolio is safe).
    /// </summary>
    public async Task<ImportResult> ImportPositionsAsync(
        string userId, List<ImportPositionRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) throw new ValidationError("No rows to import");
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var r in rows)
            {
                var pos = await db.Positions
                    .FirstOrDefaultAsync(p => p.UserId == userId && p.Ticker == r.Ticker, ct);
                if (pos is null)
                    db.Positions.Add(new Position { UserId = userId, Ticker = r.Ticker, Shares = r.Shares, AvgPrice = r.AvgPrice });
                else
                {
                    pos.Shares = r.Shares;
                    pos.AvgPrice = r.AvgPrice;
                }
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ImportResult { RowsImported = rows.Count };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Import a transaction history. The rows are replayed in order
    /// (BUY adds, UNDO reverses) on top of the existing positions and
    /// land in one non-undoable 'imported' run, like the old Python
    /// 'legacy' run. Any inconsistency (oversell, missing position)
    /// rolls the whole import back.
    /// </summary>
    public async Task<ImportResult> ImportTransactionsAsync(
        string userId, List<ImportTransactionRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) throw new ValidationError("No rows to import");
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var run = new Run
            {
                UserId = userId,
                Date = Dates.Today(),
                Status = RunStatus.Imported,
            };
            db.Runs.Add(run);
            await db.SaveChangesAsync(ct);

            foreach (var r in rows)
            {
                db.Transactions.Add(new Transaction
                {
                    UserId = userId,
                    RunId = run.Id,
                    Date = r.Date,
                    Ticker = r.Ticker,
                    Action = r.Action,
                    Shares = r.Shares,
                    Price = r.Price,
                    Value = r.Value,
                    Commission = r.Commission,
                });
                if (r.Action == TransactionAction.Buy)
                {
                    ApplyPosition(db, userId, r.Ticker, r.Shares, r.Value, r.Commission);
                }
                else
                {
                    var pos = await db.Positions
                        .FirstOrDefaultAsync(p => p.UserId == userId && p.Ticker == r.Ticker, ct);
                    if (pos is null)
                        throw new ValidationError(
                            $"Import failed at {r.Date} {r.Ticker}: UNDO row but the position is missing");
                    var newShares = pos.Shares - r.Shares;
                    if (newShares < 0)
                        throw new ValidationError(
                            $"Import failed at {r.Date} {r.Ticker}: has {pos.Shares} shares, " +
                            $"but the UNDO row reverses {r.Shares}");
                    if (newShares == 0) db.Positions.Remove(pos);
                    else
                    {
                        var oldCost = pos.Shares * pos.AvgPrice;
                        pos.Shares = newShares;
                        pos.AvgPrice = (oldCost - r.Value - r.Commission) / newShares;
                    }
                }
                // persist each row so later rows see earlier ones
                await db.SaveChangesAsync(ct);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation("User {User}: imported {Count} transactions as run {RunId}",
                userId, rows.Count, run.Id);
            return new ImportResult { RowsImported = rows.Count, RunId = run.Id };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ============================================================
    // CSV exports — synthesized from the DB on demand
    // ============================================================

    public async Task<string> PositionsCsvAsync(string userId, CancellationToken ct = default)
    {
        var rows = await GetPositionsAsync(userId, ct);
        var sb = new System.Text.StringBuilder("ticker,shares,avg_price\n");
        foreach (var r in rows)
            sb.Append(r.Ticker).Append(',').Append(r.Shares).Append(',')
              .Append(Fmt(r.AvgPrice)).Append('\n');
        return sb.ToString();
    }

    public async Task<string> TransactionsCsvAsync(string userId, CancellationToken ct = default)
    {
        // chronological (append order), like the original log file;
        // the UI listing uses the descending order
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Transactions
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.Id)
            .Select(t => new { t.Date, t.Ticker, t.Action, t.Shares, t.Price, t.Value, t.Commission })
            .ToListAsync(ct);
        var sb = new System.Text.StringBuilder("date,ticker,action,shares,price,value,commission\n");
        foreach (var r in rows)
            sb.Append(r.Date).Append(',').Append(r.Ticker).Append(',').Append(r.Action).Append(',')
              .Append(r.Shares).Append(',').Append(Fmt(r.Price)).Append(',')
              .Append(Fmt(r.Value)).Append(',').Append(Fmt(r.Commission)).Append('\n');
        return sb.ToString();
    }

    public async Task<string> CompositionCsvAsync(string userId, CancellationToken ct = default)
    {
        var rows = await GetReportAsync(userId, ct);
        var sb = new System.Text.StringBuilder(
            "ticker,shortnames,weight,price_rub,lot_size,current_shares,current_value," +
            "buy_shares,buy_value,buy_comm,post_value,post_weight,target_w,weight_diff\n");
        foreach (var r in rows)
        {
            sb.Append(r.Ticker).Append(',')
              .Append(r.Shortnames ?? "").Append(',')
              .Append(Fmt(r.Weight)).Append(',')
              .Append(Fmt(r.PriceRub)).Append(',')
              .Append(r.LotSize).Append(',')
              .Append(r.CurrentShares).Append(',')
              .Append(Fmt(r.CurrentValue)).Append(',')
              .Append(r.BuyShares).Append(',')
              .Append(Fmt(r.BuyValue)).Append(',')
              .Append(Fmt(r.BuyComm)).Append(',')
              .Append(Fmt(r.PostValue)).Append(',')
              .Append(Fmt(r.PostWeight)).Append(',')
              .Append(Fmt(r.TargetW)).Append(',')
              .Append(Fmt(r.WeightDiff)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Round-trip floating-point format (like pandas to_csv).</summary>
    internal static string Fmt(double? v) =>
        v is { } x ? x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "";
}
