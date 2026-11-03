# index-investment

Bi-weekly DCA (dollar-cost averaging) into the **IMOEX** index with position
tracking. Fetches the index composition from MOEX ISS, computes index-proportional
targets for your portfolio, and produces commission-aware buy orders that
minimize tracking error (L1 weight error). DCA mode never sells.

Two front-ends share the same core logic (`dca.py`):

- **Webapp** — FastAPI dashboard with password auth (see below)
- **CLI** — the original terminal script, kept as a thin wrapper

## Project layout

```
dca.py              core logic: MOEX fetches, allocator, pipeline
db.py               SQLite storage: positions, runs, transactions, reports
imoex_dca.py        CLI wrapper (terminal usage unchanged)
main.py             FastAPI app + routes
auth.py             single-user password auth (PBKDF2, session cookies)
templates/          Jinja2 pages (dashboard, login/setup)
static/             vanilla JS + CSS for the dashboard
tests/              pytest suite (allocator, pipeline, undo, migration)
data/               imoex.db, auth.json                              (gitignored)
                    positions.csv, transactions.csv — legacy, imported once
export/             composition.csv — legacy, imported once
```

## Setup

```bash
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
# for running the tests:
.venv/bin/pip install -r requirements-dev.txt
.venv/bin/python -m pytest
```

## Webapp

```bash
.venv/bin/uvicorn main:app --port 8000
```

Then open <http://127.0.0.1:8000>.

### First run — setting the password

The app contains personal financial data, so every page and API endpoint is
behind a single-user password:

1. On the first launch (no `data/auth.json` yet) you get a **create password**
   screen instead of a login form.
2. The password is stored **hashed** (PBKDF2-SHA256, 600k iterations, random
   salt) in `data/auth.json` with file mode `0600`. The file is inside the
   gitignored `data/` directory, so nothing personal ever reaches git.
3. Login sets a signed session cookie (`HttpOnly`, `SameSite=Lax`, expires
   after a week). The session secret is persisted in the same file, so a
   server restart does not force a re-login.
4. After 5 failed logins the app locks out for 30 seconds.

To reset the password, delete `data/auth.json` and restart — the setup screen
comes back.

> **Security note:** uvicorn binds to `127.0.0.1` by default. Keep it that
> way — the password is a second layer, not the only one. Do not expose this
> app on your LAN without putting it behind a proper reverse proxy with TLS.

### Using the dashboard

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
  pre-run positions and marks the run undone. `UNDO` rows are appended to the
  audit log. Only the latest applied run can be undone at a time.
- **Tabs** — Holdings (current positions vs index weights), Transactions
  (full audit log, BUY and UNDO rows), Last report (per-ticker composition
  after the last run).
- **CSV downloads** — the three data files, synthesized from the database.

## Storage

All portfolio state lives in **SQLite** (`data/imoex.db`, gitignored):

```
positions(ticker PK, shares, avg_price)
runs(id PK, date, amount, trade_date, status: applied | undone | legacy)
transactions(id PK, run_id, date, ticker, action: BUY | UNDO, shares, price, value, commission)
run_report(run_id, ticker, …per-ticker composition + cost basis…)
```

- An applied run is one atomic `BEGIN IMMEDIATE` transaction (positions +
  audit rows + report), so an overlapping webapp + CLI run can never both
  apply — the second one gets a clear error.
- Undo math is exact: `avg_price` is recomputed from
  `shares · avg_price − buy_value − commission`, which restores the
  pre-run value bit-for-bit.
- On first start, the legacy CSVs (`data/positions.csv`,
  `data/transactions.csv`, `export/composition.csv`) are imported once —
  positions as-is, the old audit log and last report as a non-undoable
  `legacy` run. The files are left untouched; the DB is the source of truth.

## CLI

```bash
.venv/bin/python imoex_dca.py                  # interactive: prompts for amount
.venv/bin/python imoex_dca.py --dry-run [N]    # preview, defaults to 10,000 RUB
.venv/bin/python imoex_dca.py --undo           # reverse the last applied run
```

## Docker

```bash
docker compose up --build
```

Then open <http://127.0.0.1:8080> (change the port mapping in
`docker-compose.yml` if you want a different one).

- `Dockerfile` — Python image with the app, running as a non-root user (uid
  1000) so bind-mounted files are not root-owned on the host. On a Linux
  host, `chown` `data/` and `export/` to uid 1000 first (on Docker Desktop
  for macOS it just works). Uvicorn binds to `0.0.0.0` *inside* the
  container, but the app port is not published.
- `nginx.conf` — reverse proxy, the only external entry point, with a
  120s proxy timeout.
- `data/` and `export/` are bind-mounted from the host, so the database,
  password hash, and legacy CSVs survive image rebuilds and are never baked
  into the image (see `.dockerignore`).

## Caching

Market data (index composition + closes) is settled once per trading day,
so it is cached in-process for an hour (`SNAPSHOT_TTL` in `dca.py`):

- the cache is invalidated on every login, on every *applied* run (which
  fetches fresh data), and via the **↻ Refresh** button;
- dashboard loads and dry-run previews are served from the cache.

A snapshot is exactly two MOEX requests: the index analytics (weights +
trade date) and the TQBR board (lot sizes + last legal close per ticker),
so a cold fetch takes ~1 s instead of the old ~50 sequential price requests.
Tickers whose last settled session is more than 30 days old (long
suspensions) make the snapshot fail with a clear error.

## How the allocator works

Given the new cash (net of commission) and the positive gaps between current
holdings and index-proportional targets, the allocator:

1. starts from the floor number of lots per ticker,
2. greedily adds one lot at a time to whichever ticker reduces the L1 weight
   error the most,
3. optionally spends the remaining cash even at a slight error cost.

If fewer than `CARRY_THRESHOLD` tickers fit in the budget, the whole
contribution is carried over to the next period instead of buying.
Leftover cash is reported per run but not persisted — add it to the next
contribution manually if you want.

## Configuration

Constants at the top of `dca.py`: commission (`0.045%`), carry threshold,
index name, snapshot TTL. Storage lives in `db.py`.

## Tests

```bash
.venv/bin/python -m pytest
```

Covers the allocator (floor start, greedy phase, phase-2 remainder, budget
with commission), the run/undo pipeline end-to-end with a mocked market and
temp database (apply → undo round-trip restores the exact pre-run state),
dry-run purity, the legacy CSV migration, and snapshot error handling.
