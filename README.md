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
dca.py              core logic: MOEX fetches, allocator, pipeline, persistence
imoex_dca.py        CLI wrapper (terminal usage unchanged)
main.py             FastAPI app + routes
auth.py             single-user password auth (PBKDF2, session cookies)
templates/          Jinja2 pages (dashboard, login/setup)
static/             vanilla JS + CSS for the dashboard
data/               positions.csv, transactions.csv, auth.json   (gitignored)
export/             composition.csv — latest run report          (gitignored)
```

## Setup

```bash
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
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
3. Login sets a signed session cookie (`HttpOnly`, `SameSite=Lax`). The
   session secret is persisted in the same file, so a server restart does not
   force a re-login.
4. After 5 failed logins the app locks out for 30 seconds.

To reset the password, delete `data/auth.json` and restart — the setup screen
comes back.

> **Security note:** uvicorn binds to `127.0.0.1` by default. Keep it that
> way — the password is a second layer, not the only one. Do not expose this
> app on your LAN without putting it behind a proper reverse proxy with TLS.

### Using the dashboard

- **Summary cards** — portfolio value, cost basis, P/L, held/index ticker
  counts. Prices are the legal close of the latest trading session (if the
  market is still trading, the app falls back to the most recent close within
  30 days).
- **Run DCA** — enter the contribution amount:
  1. *Preview (dry run)* computes the buy orders **without writing any files**.
  2. *Confirm & apply* runs the same computation for real: updates
     `data/positions.csv`, appends to `data/transactions.csv`, and rewrites
     `export/composition.csv`.
- **Tabs** — Holdings (current positions vs index weights), Transactions
  (full audit log), Last report (per-ticker composition after the last run).
- **CSV downloads** — the three data files, right from the tab bar.

The run makes ~50 sequential requests to MOEX ISS, so expect a 10–60 s wait
while the spinner is up.

## CLI

```bash
.venv/bin/python imoex_dca.py                  # interactive: prompts for amount
.venv/bin/python imoex_dca.py --dry-run [N]    # preview, defaults to 10,000 RUB
```

## Docker

```bash
docker compose up --build
```

Then open <http://127.0.0.1:8080> (change the port mapping in
`docker-compose.yml` if you want a different one).

- `Dockerfile` — Python image with the app; uvicorn binds to `0.0.0.0`
  *inside* the container, but the app port is not published.
- `nginx.conf` — reverse proxy, the only external entry point, with a
  120s proxy timeout (a cold-cache MOEX fetch can take ~60s).
- `data/` and `export/` are bind-mounted from the host, so positions,
  transactions, and the password hash survive image rebuilds and are never
  baked into the image (see `.dockerignore`).

## Caching

Market data (index composition + closes) is settled once per trading day,
so it is cached in-process for an hour (`SNAPSHOT_TTL` in `dca.py`):

- the cache is invalidated on every login, so each login refreshes data once;
- dashboard loads and dry-run previews are served from the cache (instant);
- an *applied* run always fetches fresh data before allocating.

## How the allocator works

Given the new cash (net of commission) and the positive gaps between current
holdings and index-proportional targets, the allocator:

1. starts from the floor number of lots per ticker,
2. greedily adds one lot at a time to whichever ticker reduces the L1 weight
   error the most,
3. optionally spends the remaining cash even at a slight error cost.

If fewer than `CARRY_THRESHOLD` tickers fit in the budget, the whole
contribution is carried over to the next period instead of buying.

## Configuration

Constants at the top of `dca.py`: commission (`0.045%`), carry threshold,
index name, and file paths.
