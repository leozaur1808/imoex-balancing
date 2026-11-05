using IndexInvestment.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace IndexInvestment.Web.Controllers;

/// <summary>All portfolio data endpoints — every one is per-user.</summary>
[ApiController]
[Authorize]
public class ApiController : ControllerBase
{
    private readonly UserManager<IdentityUser> _users;
    private readonly PortfolioService _portfolio;
    private readonly DcaEngine _engine;
    private readonly MarketDataService _market;

    public ApiController(
        UserManager<IdentityUser> users,
        PortfolioService portfolio,
        DcaEngine engine,
        MarketDataService market)
    {
        _users = users;
        _portfolio = portfolio;
        _engine = engine;
        _market = market;
    }

    private async Task<IdentityUser> CurrentUser() =>
        await _users.GetUserAsync(User) ?? throw new ConflictException("Not authenticated");

    private async Task<(decimal Commission, string Index)> Settings(IdentityUser user)
    {
        var s = await _portfolio.GetSettingsAsync(user.Id);
        return (s?.CommissionPercent ?? 0.045m, s?.IndexId ?? "IMOEX");
    }

    // ============================================================
    // Read model
    // ============================================================

    [HttpGet("/api/portfolio")]
    public async Task<IActionResult> Portfolio()
    {
        var user = await CurrentUser();
        var (_, index) = await Settings(user);
        var view = await _engine.GetPortfolioAsync(
            user.Id, index, MarketDataService.SnapshotTtl, HttpContext.RequestAborted);
        return Ok(view);
    }

    [HttpGet("/api/positions")]
    public async Task<IActionResult> Positions() =>
        Ok(new { rows = await _portfolio.GetPositionsAsync((await CurrentUser()).Id) });

    [HttpGet("/api/transactions")]
    public async Task<IActionResult> Transactions() =>
        Ok(new { rows = await _portfolio.GetTransactionsAsync((await CurrentUser()).Id) });

    [HttpGet("/api/report")]
    public async Task<IActionResult> Report() =>
        Ok(new { rows = await _portfolio.GetReportAsync((await CurrentUser()).Id) });

    [HttpGet("/api/undo-target")]
    public async Task<IActionResult> UndoTarget() =>
        Ok(new { run = await _portfolio.LatestUndoableRunAsync((await CurrentUser()).Id) });

    // ============================================================
    // Run / undo
    // ============================================================

    [HttpPost("/api/run")]
    public async Task<IActionResult> Run([FromBody] RunRequest req)
    {
        var user = await CurrentUser();
        var (comm, index) = await Settings(user);
        if (req.Amount <= 0)
            throw new ValidationError("Amount must be positive");

        // Previews reuse the cached snapshot (instant); an applied run
        // always computes on a fresh fetch.
        var maxAge = req.DryRun ? MarketDataService.SnapshotTtl : 0.0;
        var res = await _engine.RunAsync(
            user.Id, index, (double)(comm / 100m), req.Amount, req.DryRun, maxAge,
            HttpContext.RequestAborted);
        return Ok(res);
    }

    [HttpPost("/api/undo")]
    public async Task<IActionResult> Undo() =>
        Ok(await _engine.UndoAsync((await CurrentUser()).Id, HttpContext.RequestAborted));

    [HttpPost("/api/refresh")]
    public async Task<IActionResult> Refresh()
    {
        // Drop the market-data cache for the user's index; the next
        // dashboard load refetches.
        var user = await CurrentUser();
        var (_, index) = await Settings(user);
        _market.Invalidate(index);
        return Ok(new { ok = true });
    }

    // ============================================================
    // Settings (per user)
    // ============================================================

    [HttpGet("/api/settings")]
    public async Task<IActionResult> GetSettings()
    {
        var user = await CurrentUser();
        var (comm, index) = await Settings(user);
        return Ok(new { commission_percent = comm, index_id = index });
    }

    [HttpPatch("/api/settings")]
    public async Task<IActionResult> SaveSettings([FromBody] SettingsRequest req)
    {
        var user = await CurrentUser();
        if (req.IndexId is null && req.CommissionPercent is null)
            throw new ValidationError("Nothing to save");
        var (comm, index) = await Settings(user);
        PortfolioService.ValidateIndexId(req.IndexId ?? index);
        await _portfolio.SaveSettingsAsync(
            user.Id,
            req.CommissionPercent ?? comm,
            (req.IndexId ?? index).Trim().ToUpperInvariant());
        _market.Invalidate(index);
        return Ok(new { ok = true });
    }

    // ============================================================
    // Portfolio management (each user: their own data only)
    // ============================================================

    [HttpPost("/api/positions")]
    public async Task<IActionResult> SetPosition([FromBody] PositionRequest req)
    {
        await _portfolio.SetPositionAsync(
            (await CurrentUser()).Id,
            (req.Ticker ?? "").Trim().ToUpperInvariant(),
            req.Shares, req.AvgPrice,
            HttpContext.RequestAborted);
        return Ok(new { ok = true });
    }

    [HttpDelete("/api/positions/{ticker}")]
    public async Task<IActionResult> DeletePosition(string ticker)
    {
        var removed = await _portfolio.DeletePositionAsync(
            (await CurrentUser()).Id, ticker.Trim().ToUpperInvariant(), HttpContext.RequestAborted);
        if (!removed)
            throw new ConflictException($"No position for {ticker}");
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Correct the actual price/commission of an executed BUY row
    /// (runs are priced at last legal closes, t-1; the real fill can
    /// differ). Recomputes the position's average price.
    /// </summary>
    [HttpPatch("/api/transactions/{id:int}")]
    public async Task<IActionResult> UpdateTransaction(int id, [FromBody] TransactionUpdateRequest req)
    {
        var ok = await _portfolio.UpdateTransactionAsync(
            (await CurrentUser()).Id, id, req.Price, req.Commission, HttpContext.RequestAborted);
        if (!ok)
            throw new ConflictException($"Transaction {id} not found");
        return Ok(new { ok = true });
    }

    // ============================================================
    // Imports
    // ============================================================

    [HttpPost("/api/import/positions")]
    public async Task<IActionResult> ImportPositions([FromBody] ImportRequest req)
    {
        var rows = CsvImporter.ParsePositions(req.Csv ?? "");
        var res = await _portfolio.ImportPositionsAsync((await CurrentUser()).Id, rows, HttpContext.RequestAborted);
        return Ok(new { ok = true, rows_imported = res.RowsImported });
    }

    [HttpPost("/api/import/transactions")]
    public async Task<IActionResult> ImportTransactions([FromBody] ImportRequest req)
    {
        var rows = CsvImporter.ParseTransactions(req.Csv ?? "");
        var res = await _portfolio.ImportTransactionsAsync((await CurrentUser()).Id, rows, HttpContext.RequestAborted);
        return Ok(new { ok = true, rows_imported = res.RowsImported, run_id = res.RunId });
    }

    // ============================================================
    // CSV downloads — synthesized from the DB on demand
    // ============================================================

    [HttpGet("/download/{name}")]
    public async Task<IActionResult> Download(string name)
    {
        var user = await CurrentUser();
        string csv;
        string filename;
        switch (name)
        {
            case "positions":
                csv = await _portfolio.PositionsCsvAsync(user.Id);
                filename = "positions.csv";
                break;
            case "transactions":
                csv = await _portfolio.TransactionsCsvAsync(user.Id);
                filename = "transactions.csv";
                break;
            case "composition":
                csv = await _portfolio.CompositionCsvAsync(user.Id);
                filename = "composition.csv";
                break;
            default:
                return NotFound();
        }
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", filename);
    }

    // ============================================================
    // Request bodies
    // ============================================================

    public sealed class RunRequest
    {
        public double Amount { get; init; }
        public bool DryRun { get; init; }
    }

    public sealed class SettingsRequest
    {
        public decimal? CommissionPercent { get; init; }
        public string? IndexId { get; init; }
    }

    public sealed class PositionRequest
    {
        public string? Ticker { get; init; }
        public long Shares { get; init; }
        public double AvgPrice { get; init; }
    }

    public sealed class TransactionUpdateRequest
    {
        public double Price { get; init; }
        public double Commission { get; init; }
    }

    public sealed class ImportRequest
    {
        public string? Csv { get; init; }
    }
}
