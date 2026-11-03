"""
db.py — SQLite storage for positions, runs, and transactions.

Everything lives in a single file, data/imoex.db (gitignored). The old
CSV files (data/positions.csv, data/transactions.csv) are imported once
on first start and left untouched afterwards; the database becomes the
source of truth.

Tables
------
positions(ticker PK, shares, avg_price)
runs(id PK, date, amount, trade_date, status)        # applied | undone | legacy
transactions(id PK, run_id, date, ticker, action, shares, price, value, commission)
run_report(id PK, run_id, ticker, name, weight, price, lot_size,
           current_shares, current_value, cost_basis,
           buy_shares, buy_value, buy_comm,
           post_value, post_weight, target_w, weight_diff)

An applied run is committed as a single BEGIN IMMEDIATE transaction, so
two overlapping runs (e.g. webapp + CLI) can never both apply — the
second writer waits on the file lock (or times out).

`init()` must be called once at startup; main.py and imoex_dca.py do this.
"""

from __future__ import annotations
import logging
import sqlite3
from contextlib import contextmanager, suppress
from datetime import date
from pathlib import Path

import pandas as pd

logger = logging.getLogger("imoex.db")

BASE_DIR     = Path(__file__).parent
DB_PATH              = BASE_DIR / "data" / "imoex.db"
LEGACY_POSITIONS_CSV = BASE_DIR / "data" / "positions.csv"
LEGACY_TRANSACTIONS_CSV = BASE_DIR / "data" / "transactions.csv"
LEGACY_REPORT_CSV    = BASE_DIR / "export" / "composition.csv"

# Column order of the "Last report" API/export — identical to the old
# export/composition.csv header.
REPORT_EXPORT_COLUMNS = [
    'ticker', 'shortnames', 'weight', 'price_rub', 'lot_size',
    'current_shares', 'current_value',
    'buy_shares', 'buy_value', 'buy_comm',
    'post_value', 'post_weight', 'target_w', 'weight_diff',
]

_SCHEMA = """
CREATE TABLE IF NOT EXISTS positions (
    ticker    TEXT PRIMARY KEY,
    shares    INTEGER NOT NULL,
    avg_price REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS runs (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    date       TEXT NOT NULL,
    amount     REAL,
    trade_date TEXT,
    status     TEXT NOT NULL CHECK (status IN ('applied', 'undone', 'legacy'))
);

CREATE TABLE IF NOT EXISTS transactions (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id     INTEGER NOT NULL REFERENCES runs(id),
    date       TEXT NOT NULL,
    ticker     TEXT NOT NULL,
    action     TEXT NOT NULL CHECK (action IN ('BUY', 'UNDO')),
    shares     INTEGER NOT NULL,
    price      REAL NOT NULL,
    value      REAL NOT NULL,
    commission REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS run_report (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id         INTEGER NOT NULL REFERENCES runs(id),
    ticker         TEXT NOT NULL,
    name           TEXT,
    weight         REAL,
    price          REAL,
    lot_size       INTEGER,
    current_shares INTEGER NOT NULL DEFAULT 0,
    current_value  REAL,
    cost_basis     REAL,
    buy_shares     INTEGER NOT NULL DEFAULT 0,
    buy_value      REAL,
    buy_comm       REAL,
    post_value     REAL,
    post_weight    REAL,
    target_w       REAL,
    weight_diff    REAL
);

CREATE INDEX IF NOT EXISTS idx_transactions_run ON transactions(run_id);
CREATE INDEX IF NOT EXISTS idx_report_run       ON run_report(run_id);
"""

# ============================================================
# Connection helpers
# ============================================================
def connect() -> sqlite3.Connection:
    # timeout=30 s: overlapping writers (webapp + CLI, separate processes)
    # queue on the file lock instead of failing instantly.
    # isolation_level=None: autocommit mode, write paths manage their own
    # explicit BEGIN IMMEDIATE / COMMIT.
    return sqlite3.connect(DB_PATH, timeout=30, isolation_level=None)

@contextmanager
def _write_tx():
    """A single immediate (exclusive) transaction, rolled back on error."""
    conn = connect()
    try:
        conn.execute("BEGIN IMMEDIATE")
        yield conn
        conn.execute("COMMIT")
    except BaseException:
        with suppress(sqlite3.Error):
            conn.execute("ROLLBACK")
        raise
    finally:
        conn.close()

def query(sql: str, params: tuple = ()) -> list[tuple]:
    conn = connect()
    try:
        return conn.execute(sql, params).fetchall()
    finally:
        conn.close()

def query1(sql: str, params: tuple = ()):
    rows = query(sql, params)
    return rows[0] if rows else None

# ============================================================
# Init + one-time legacy CSV import
# ============================================================
def init() -> None:
    """Create the schema (idempotent) and import the legacy CSVs once."""
    DB_PATH.parent.mkdir(parents=True, exist_ok=True)
    conn = connect()
    try:
        conn.executescript(_SCHEMA)
    finally:
        conn.close()
    _migrate_legacy_if_needed()

def _migrate_legacy_if_needed() -> None:
    """
    One-time import of the pre-SQLite CSVs. They are left in place,
    untouched. The old audit log becomes a non-undoable 'legacy' run.
    """
    if not (LEGACY_POSITIONS_CSV.exists() or LEGACY_TRANSACTIONS_CSV.exists()
            or LEGACY_REPORT_CSV.exists()):
        return
    with _write_tx() as conn:
        has_pos  = conn.execute("SELECT COUNT(*) FROM positions").fetchone()[0]
        has_runs = conn.execute("SELECT COUNT(*) FROM runs").fetchone()[0]
        if has_pos or has_runs:
            return  # already migrated (or data was loaded some other way)

        imported = []
        legacy_id = None
        if LEGACY_POSITIONS_CSV.exists():
            df = pd.read_csv(LEGACY_POSITIONS_CSV)
            conn.executemany(
                "INSERT INTO positions (ticker, shares, avg_price) VALUES (?,?,?)",
                [(r.ticker, int(r.shares), float(r.avg_price)) for r in df.itertuples()])
            imported.append(f"{len(df)} positions")
        if LEGACY_TRANSACTIONS_CSV.exists() or LEGACY_REPORT_CSV.exists():
            # the old audit log (and the last report) become one
            # non-undoable 'legacy' run
            cur = conn.execute(
                "INSERT INTO runs (date, amount, trade_date, status) VALUES (?,?,?,?)",
                (date.today().isoformat(), None, None, "legacy"))
            legacy_id = cur.lastrowid
            if LEGACY_TRANSACTIONS_CSV.exists():
                df = pd.read_csv(LEGACY_TRANSACTIONS_CSV)
                conn.executemany(
                    "INSERT INTO transactions (run_id, date, ticker, action, shares, "
                    "price, value, commission) VALUES (?,?,?,?,?,?,?,?)",
                    [(legacy_id, r.date, r.ticker, r.action, int(r.shares),
                      float(r.price), float(r.value), float(r.commission))
                     for r in df.itertuples()])
                imported.append(f"{len(df)} transactions")
        if LEGACY_REPORT_CSV.exists():
            df = pd.read_csv(LEGACY_REPORT_CSV)
            conn.executemany(
                "INSERT INTO run_report (run_id, ticker, name, weight, price, "
                "lot_size, current_shares, current_value, cost_basis, "
                "buy_shares, buy_value, buy_comm, post_value, post_weight, "
                "target_w, weight_diff) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                [(legacy_id, r.ticker, r.shortnames, float(r.weight), float(r.price_rub),
                  int(float(r.lot_size)), int(r.current_shares), float(r.current_value),
                  0.0, int(r.buy_shares), float(r.buy_value), float(r.buy_comm),
                  float(r.post_value), float(r.post_weight), float(r.target_w),
                  float(r.weight_diff))
                 for r in df.itertuples()])
            imported.append(f"{len(df)} report rows")
        logger.info("Imported legacy CSVs into %s: %s", DB_PATH.name, "; ".join(imported))

# ============================================================
# Readers
# ============================================================
def load_positions_df() -> pd.DataFrame:
    """Positions as a DataFrame (ticker, shares, avg_price) for the pipeline."""
    rows = query("SELECT ticker, shares, avg_price FROM positions ORDER BY ticker")
    df = pd.DataFrame(rows, columns=['ticker', 'shares', 'avg_price'])
    df['shares']    = df['shares'].astype(int)
    df['avg_price'] = df['avg_price'].astype(float)
    return df

def get_positions() -> list[dict]:
    rows = query("SELECT ticker, shares, avg_price FROM positions ORDER BY ticker")
    return [{'ticker': t, 'shares': int(s), 'avg_price': float(a)} for t, s, a in rows]

def get_transactions() -> list[dict]:
    rows = query(
        "SELECT date, ticker, action, shares, price, value, commission "
        "FROM transactions ORDER BY id DESC")
    return [{'date': d, 'ticker': t, 'action': a, 'shares': int(s),
             'price': float(p), 'value': float(v), 'commission': float(c)}
            for d, t, a, s, p, v, c in rows]

def get_report() -> list[dict]:
    """
    Per-ticker report of the most recent non-undone run (an applied run,
    or the legacy run imported from export/composition.csv), in original
    row order.
    """
    rows = query(
        "SELECT r.ticker, r.name, r.weight, r.price, r.lot_size, "
        "       r.current_shares, r.current_value, "
        "       r.buy_shares, r.buy_value, r.buy_comm, "
        "       r.post_value, r.post_weight, r.target_w, r.weight_diff "
        "FROM run_report r "
        "JOIN runs u ON u.id = r.run_id "
        "WHERE u.id = (SELECT id FROM runs WHERE status IN ('applied','legacy') "
        "              ORDER BY id DESC LIMIT 1) "
        "ORDER BY r.rowid")
    return [dict(zip(REPORT_EXPORT_COLUMNS, row)) for row in rows]

def latest_undoable_run() -> dict | None:
    """The most recent run that can still be undone (status='applied')."""
    row = query1(
        "SELECT id, date, amount, trade_date FROM runs "
        "WHERE status = 'applied' ORDER BY id DESC LIMIT 1")
    if row is None:
        return None
    return {'id': row[0], 'date': row[1], 'amount': row[2], 'trade_date': row[3]}

# ============================================================
# Writers — one DCA run / one undo, each a single transaction
# ============================================================
def apply_run(run_date: str, amount: float, trade_date: str,
              buys: list[dict], report_rows: list[dict]) -> int:
    """
    Commit one applied DCA run atomically: run row + BUY audit rows +
    position updates + per-ticker report. Returns the new run id.
    """
    with _write_tx() as conn:
        cur = conn.execute(
            "INSERT INTO runs (date, amount, trade_date, status) "
            "VALUES (?,?,?,'applied')",
            (run_date, amount, trade_date))
        run_id = cur.lastrowid
        for b in buys:
            conn.execute(
                "INSERT INTO transactions (run_id, date, ticker, action, shares, "
                "price, value, commission) VALUES (?,?,?, 'BUY', ?, ?, ?, ?)",
                (run_id, run_date, b['ticker'], int(b['shares']),
                 float(b['price']), float(b['value']), float(b['commission'])))
            _apply_position(conn, b['ticker'], int(b['shares']),
                            float(b['value']), float(b['commission']))
        if report_rows:
            conn.executemany(
                "INSERT INTO run_report (run_id, ticker, name, weight, price, "
                "lot_size, current_shares, current_value, cost_basis, "
                "buy_shares, buy_value, buy_comm, post_value, post_weight, "
                "target_w, weight_diff) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                [(run_id, r['ticker'], r['name'], r['weight'], r['price'],
                  r['lot_size'], r['current_shares'], r['current_value'],
                  r['cost_basis'], r['buy_shares'], r['buy_value'],
                  r['buy_comm'], r['post_value'], r['post_weight'],
                  r['target_w'], r['weight_diff'])
                 for r in report_rows])
        return run_id

def undo_run(run_id: int, undo_date: str) -> dict:
    """
    Reverse an applied run (one step back, git-style): restore the
    pre-run positions, mark the run 'undone', append UNDO rows to the
    audit log. Raises if the run is not applied or the state no longer
    matches (position missing / oversold).
    """
    with _write_tx() as conn:
        run = conn.execute(
            "SELECT id, date, amount, trade_date, status "
            "FROM runs WHERE id = ?", (run_id,)).fetchone()
        if run is None or run[4] != 'applied':
            raise ValueError(f"Run {run_id} is not an applied run")

        buys = conn.execute(
            "SELECT ticker, shares, price, value, commission FROM transactions "
            "WHERE run_id = ? AND action = 'BUY' ORDER BY id",
            (run_id,)).fetchall()

        for ticker, shares, price, value, comm in buys:
            row = conn.execute(
                "SELECT shares, avg_price FROM positions WHERE ticker = ?",
                (ticker,)).fetchone()
            if row is None:
                raise RuntimeError(
                    f"Cannot undo run {run_id}: position for {ticker} is missing")
            old_sh, old_avg = row
            new_sh = old_sh - shares
            if new_sh < 0:
                raise RuntimeError(
                    f"Cannot undo run {run_id}: {ticker} has {old_sh} shares, "
                    f"but the run bought {shares}")
            if new_sh == 0:
                conn.execute("DELETE FROM positions WHERE ticker = ?", (ticker,))
            else:
                new_avg = (old_sh * old_avg - value - comm) / new_sh
                conn.execute(
                    "UPDATE positions SET shares = ?, avg_price = ? "
                    "WHERE ticker = ?",
                    (new_sh, new_avg, ticker))

        conn.execute("UPDATE runs SET status = 'undone' WHERE id = ?", (run_id,))
        for ticker, shares, price, value, comm in buys:
            conn.execute(
                "INSERT INTO transactions (run_id, date, ticker, action, shares, "
                "price, value, commission) VALUES (?,?,?, 'UNDO', ?, ?, ?, ?)",
                (run_id, undo_date, ticker, shares, price, value, comm))

        return {'id': run_id, 'date': run[1], 'amount': run[2],
                'trade_date': run[3], 'n_reversed': len(buys)}

def _apply_position(conn: sqlite3.Connection, ticker: str, shares: int,
                    value: float, comm: float) -> None:
    """avg_price = total cost (incl. commission) / total shares — the same
    math the old CSV flow used."""
    row = conn.execute(
        "SELECT shares, avg_price FROM positions WHERE ticker = ?",
        (ticker,)).fetchone()
    if row:
        old_sh, old_avg = row
        new_sh  = old_sh + shares
        new_avg = (old_sh * old_avg + value + comm) / new_sh
        conn.execute(
            "UPDATE positions SET shares = ?, avg_price = ? WHERE ticker = ?",
            (new_sh, new_avg, ticker))
    else:
        conn.execute(
            "INSERT INTO positions (ticker, shares, avg_price) VALUES (?,?,?)",
            (ticker, shares, (value + comm) / shares))

# ============================================================
# CSV exports — synthesized from the DB on demand
# ============================================================
def positions_csv() -> str:
    df = load_positions_df()
    return df.to_csv(index=False)

def transactions_csv() -> str:
    # chronological (append order), like the original log file; the UI
    # listing uses the descending order from get_transactions()
    rows = query(
        "SELECT date, ticker, action, shares, price, value, commission "
        "FROM transactions ORDER BY id ASC")
    df = pd.DataFrame(rows,
                      columns=['date', 'ticker', 'action', 'shares',
                               'price', 'value', 'commission'])
    return df.to_csv(index=False)

def composition_csv() -> str:
    df = pd.DataFrame(get_report(), columns=REPORT_EXPORT_COLUMNS)
    return df.to_csv(index=False)
