# index-investment

Multi-user DCA (dollar-cost averaging) into a **MOEX stock index of your
choice** with per-user commissions and position tracking. Fetches the index
composition from MOEX ISS, computes index-proportional targets for your
portfolio, and produces commission-aware buy orders that minimize tracking
error (L1 weight error). DCA mode never sells.

ASP.NET Core rewrite of the original Python/FastAPI app (see `main` branch).
Every user has their own portfolio, commission rate and index — the
allocator and run/undo pipeline are the same as the original, ported 1:1.

## Project layout

```
src/
  IndexInvestment.Core/   core logic: MOEX client, allocator, run/undo
                          pipeline, EF Core data layer, CSV import
  IndexInvestment.Web/    ASP.NET Core webapp (Identity auth, API, dashboard)
  IndexInvestment.Cli/    small console wrapper (same engine + database)
tests/IndexInvestment.Tests/  xunit suite (allocator, pipeline, undo,
                          imports, per-user isolation)
data/                     app.db (SQLite)                          (gitignored)
```

## Setup

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
```

## Webapp

From the **repo root** (so the webapp and the CLI share `data/app.db`):

```bash
cd <repo-root>
dotnet build
ASPNETCORE_URLS=http://127.0.0.1:8000 dotnet src/IndexInvestment.Web/bin/Debug/net10.0/IndexInvestment.Web.dll
```

Then open <http://127.0.0.1:8000>.

> The default database path is `data/app.db`, resolved against the
> *working directory*. Run both the webapp and the CLI from the repo
> root to share one database; point them elsewhere with
> `ConnectionStrings__Default` (webapp) / `--db` (CLI).

### First run — creating the admin account

The app contains personal financial data, so every page and API endpoint is
behind authentication:

1. On the first launch (no users yet) you get a **setup screen** instead of
   a login form: create the **admin** account (email + password).
2. The password is stored **hashed** (ASP.NET Core Identity, PBKDF2) in the
   `AspNetUsers` table of `data/app.db`, inside the gitignored `data/`
   directory — nothing personal ever reaches git.
3. Login sets a cookie session (`HttpOnly`, `SameSite=Lax`, 7-day sliding
   expiry). After 5 failed logins the account is locked out for 30 seconds.
4. Further accounts are created **only by the admin** (`Admin` link in the
   header → *Create account*). The admin can reset passwords and lock/
   unlock accounts.

> **Security note:** the dev server binds to `127.0.0.1`. Keep it that way —
> the password is a second layer, not the only one. Do not expose this app
> on your LAN without putting it behind a proper reverse proxy with TLS.

### Using the dashboard

- **⚙ Settings** — your own **commission %** and **MOEX index** (free text,
  suggestions: IMOEX, MOEXBMI, MOEXC, MOEXRUS, MOEXDYN, MOEXGAP, MOEXAIR).
  Any stock-based MOEX index id works. Applies to your runs only.
- **Summary cards** — portfolio value, cost basis, P/L, held/index ticker
  counts. Prices are the last legal close (if the market is still trading,
  that is the previous session's close).
- **↻ Refresh** (header) — drops the market-data cache and refetches from
  MOEX (2 requests, ~1 s).
- **Run DCA** — enter the contribution amount:
  1. *Preview (dry run)* computes the buy orders **without writing anything**.
  2. *Confirm & apply* computes on a fresh fetch and commits the run.
- **↩ Undo last run** — appears while there is an applied run. Reverses the
  most recent run (one step back, like popping a commit): restores the
  pre-run positions and marks the run undone. `UNDO` rows are appended to
  the audit log. Only the latest applied run can be undone at a time.
- **Holdings tab** — current positions vs index weights, plus a *Your
  positions* section: add / edit / delete positions directly.
- **Transactions tab** — full audit log (BUY and UNDO rows). Each BUY row
  has a **✎ price** button: runs are priced at last legal closes (t-1), so
  this is where you correct the *actual* fill price / commission — the
  position's average price is recomputed exactly.
- **Import tab** — import your current positions and/or a transaction
  history (CSV, same formats as the old app's `positions.csv` /
  `transactions.csv`, accepted verbatim). Positions import replaces the
  listed tickers; a history import is replayed (BUY adds, UNDO reverses)
  and stored as a non-undoable *imported* run.
- **Tabs / CSV downloads** — Holdings, Transactions, Last report
  (per-ticker composition after the last run), plus the three data files.

## Storage

All portfolio state lives in **SQLite** (`data/app.db`, gitignored):

```
AspNetUsers / AspNetRoles        Identity (accounts, hashed passwords)
UserSettings(user_id PK, commission_percent, index_id)
positions(user_id, ticker PK, shares, avg_price)
runs(id PK, user_id, date, amount, trade_date, index_id,
      status: applied | undone | imported)
transactions(id PK, run_id, user_id, date, ticker,
      action: BUY | UNDO, shares, price, value, commission)
run_report(run_id, ticker, …per-ticker composition + cost basis…)
```

- An applied run is one atomic database transaction (positions + audit
  rows + report), so an overlapping webapp + CLI run can never both apply —
  the second one waits on the database lock (or times out).
- Undo math is exact: `avg_price` is recomputed from
  `shares · avg_price − buy_value − commission`, which restores the
  pre-run value bit-for-bit. Editing a BUY row's price later keeps this
  consistent (undo reads the corrected values).
- PostgreSQL: the data layer is EF Core with a plain connection string
  (`ConnectionStrings__Default` + the Npgsql provider). SQLite is the
  default and all that ships in Docker today; switching is a provider swap
  if you ever outgrow it.

## CLI

From the **repo root** (same working-directory note as the webapp above):

```bash
dotnet build
dotnet src/IndexInvestment.Cli/bin/Debug/net10.0/IndexInvestment.Cli.dll run  --user <email> --amount 10000 [--dry-run]
dotnet src/IndexInvestment.Cli/bin/Debug/net10.0/IndexInvestment.Cli.dll undo --user <email>
dotnet src/IndexInvestment.Cli/bin/Debug/net10.0/IndexInvestment.Cli.dll list --user <email>
```

Uses the same database as the webapp (default `data/app.db`, `--db` to
override) and the user's own settings (commission + index). Overlapping
webapp + CLI writers are serialized by the database.

## Docker

```bash
docker compose up --build
```

Then open <http://127.0.0.1:8080> (change the port mapping in
`docker-compose.yml` if you want a different one).

- `Dockerfile` — .NET image with the app, running as a non-root user (uid
  1000) so bind-mounted files are not root-owned on the host. On a Linux
  host, `chown data/` to uid 1000 first (on Docker Desktop for macOS it
  just works). Uvicorn→Kestrel binds to `0.0.0.0` *inside* the container,
  but the app port is not published.
- `nginx.conf` — reverse proxy, the only external entry point, with a
  120s proxy timeout.
- `data/` is bind-mounted from the host, so the database (and everything
  in it) survives image rebuilds and is never baked into the image (see
  `.dockerignore`).

## Caching

Market data (index composition + closes) is settled once per trading day,
so it is cached in-process for an hour (`SnapshotTtl` in `MarketDataService`),
**per index** — two users on different indexes don't evict each other:

- the cache is invalidated on every login, on every *applied* run (which
  fetches fresh data), and via the **↻ Refresh** button;
- dashboard loads and dry-run previews are served from the cache.

A snapshot is exactly two MOEX requests: the index analytics (weights +
trade date, paginated for large indexes) and the TQBR board (lot sizes +
last legal close per ticker), so a cold fetch takes ~1 s. Tickers whose
last settled session is more than 30 days old (long suspensions) make the
snapshot fail with a clear error.

## How the allocator works

Given the new cash (net of commission) and the positive gaps between current
holdings and index-proportional targets, the allocator:

1. starts from the floor number of lots per ticker,
2. greedily adds one lot at a time to whichever ticker reduces the L1 weight
   error the most,
3. optionally spends the remaining cash even at a slight error cost.

If fewer than `CarryThreshold` tickers fit in the budget, the whole
contribution is carried over to the next period instead of buying.
Leftover cash is reported per run but not persisted — add it to the next
contribution manually if you want.

## Tests

```bash
dotnet test
```

Covers the allocator (floor start, greedy phase, phase-2 remainder, budget
with commission), the run/undo pipeline end-to-end with a fake market and
temp databases (apply → undo round-trip restores the exact pre-run state),
dry-run purity, position/transaction editing and average-price recompute,
CSV imports (including rollback on inconsistency), snapshot error handling,
and per-user isolation (two users' data never mix).
