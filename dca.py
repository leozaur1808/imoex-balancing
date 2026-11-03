"""
dca.py — Core logic for bi-weekly DCA into IMOEX with position tracking.

Refactored from the original imoex_dca.py CLI script. The pipeline
functions here return structured results instead of printing, so both
the CLI (imoex_dca.py) and the webapp (main.py) can reuse them.

Storage lives in SQLite (data/imoex.db, see db.py); the legacy CSVs
were imported once and are kept untouched. An applied run is one
atomic DB transaction, and the most recent run can be undone (dca.undo)
to restore the pre-run positions.

This module has no import-time side effects: entry points call
db.init() before using it.
"""

from __future__ import annotations
from datetime import date, datetime
import logging
import threading
import time
import requests
import numpy as np
import pandas as pd

import db

logger = logging.getLogger("imoex.dca")

# ============================================================
# CONFIG
# ============================================================
COMMISSION      = 0.045/100        # 0.045% per trade
CARRY_THRESHOLD = 1               # skip buying if fewer than N tickers fit
INDEX_NAME      = "IMOEX"

# Market data (composition + closes) is settled once per trading day,
# so the dashboard serves it from this in-process cache.
SNAPSHOT_TTL = 3600               # seconds

# One snapshot is exactly two requests: the index analytics (weights +
# trade date) and the TQBR board (lot sizes + last legal close).
ANALYTICS_URL = (
    "https://iss.moex.com/iss/statistics/engines/stock/markets/index/analytics/"
    "{indexid}.json?limit={limit}"
)
BOARD_URL = (
    "https://iss.moex.com/iss/engines/stock/markets/shares/boards/TQBR/"
    "securities.json?iss.meta=off&iss.only=securities"
)
MAX_PRICE_AGE_DAYS = 30           # reject quotes older than this

# ============================================================
# MARKET SNAPSHOT — cached composition + prices
# ============================================================
_snapshot_lock = threading.Lock()
_snapshot      = None  # (trade_date, df, fetched_at)

def get_snapshot(max_age: float = SNAPSHOT_TTL) -> tuple[str, pd.DataFrame]:
    """
    (trade_date, composition_df_with_prices_and_lots). Served from the
    in-process cache while fresh. max_age=0 forces a fresh fetch (used
    by applied runs, which must allocate on current prices). The lock
    is held during the fetch so concurrent callers wait and then reuse
    the result.
    """
    global _snapshot
    with _snapshot_lock:
        if _snapshot is not None and time.time() - _snapshot[2] < max_age:
            return _snapshot[0], _snapshot[1]
        df = get_index_composition(INDEX_NAME)
        trade_date = df.loc[0, 'trade_session_date']
        board = get_board_securities()
        _fill_prices_and_lots(df, board, trade_date)
        _snapshot = (trade_date, df, time.time())
        return trade_date, df

def invalidate_snapshot() -> None:
    """Drop the cache (called on login/refresh so the dashboard refetches)."""
    global _snapshot
    with _snapshot_lock:
        _snapshot = None

# ============================================================
# MOEX ISS DATA
# ============================================================
def get_index_composition(indexid: str = "IMOEX", limit: int = 100) -> pd.DataFrame:
    """
    Fetches the index analytics table for a given index from MOEX ISS.

    A single page is all there is: the index has a fixed, small number
    of constituents (IMOEX is capped by design), so the limit=100 page
    always covers it and no pagination is needed.
    """
    resp = requests.get(ANALYTICS_URL.format(indexid=indexid, limit=limit), timeout=30)
    resp.raise_for_status()
    payload = resp.json()['analytics']
    return pd.DataFrame(payload['data'], columns=payload['columns'])

def get_board_securities() -> dict[str, dict]:
    """
    All TQBR board securities in one request: secid -> row dict with
    SHORTNAME, LOTSIZE, PREVLEGALCLOSEPRICE, PREVDATE, ...
    """
    resp = requests.get(BOARD_URL, timeout=30)
    resp.raise_for_status()
    payload = resp.json()['securities']
    columns = payload['columns']
    secid_i = columns.index('SECID')
    return {row[secid_i]: dict(zip(columns, row)) for row in payload['data']}

def _fill_prices_and_lots(df: pd.DataFrame, board: dict[str, dict],
                          trade_date: str) -> None:
    """
    Fill price_rub (last legal close) and lot_size for every index
    ticker from the board payload.

    The board's PREVLEGALCLOSEPRICE is the close of the ticker's last
    settled session (PREVDATE). While the market is still trading that
    is the previous day's close — the same fallback the old per-ticker
    history fetch used. A ticker whose last session is more than
    MAX_PRICE_AGE_DAYS older than trade_date (long suspension) makes the
    whole snapshot fail with a clear error.
    """
    trade_dt = datetime.strptime(str(trade_date), "%Y-%m-%d")
    prices, lots = [], []
    problems = []
    for ticker in df['ticker']:
        sec = board.get(ticker)
        if sec is None:
            problems.append(f"{ticker}: not found on TQBR board")
            continue
        prevdate = sec.get('PREVDATE') or ''
        price    = sec.get('PREVLEGALCLOSEPRICE')
        if not prevdate or price in (None, '') or float(price) <= 0:
            problems.append(f"{ticker}: no price data (last session {prevdate or 'n/a'})")
            continue
        age_days = (trade_dt - datetime.strptime(prevdate, "%Y-%m-%d")).days
        if age_days > MAX_PRICE_AGE_DAYS:
            problems.append(
                f"{ticker}: last session {prevdate} is {age_days}d before {trade_date}")
            continue
        prices.append(float(price))
        lots.append(int(float(sec['LOTSIZE'])))
    if problems:
        raise RuntimeError("Incomplete market data: " + "; ".join(problems))
    df['price_rub'] = prices
    df['lot_size']  = lots

# ============================================================
# READERS — for the webapp dashboard
# ============================================================
def get_positions() -> list[dict]:
    return db.get_positions()

def get_transactions() -> list[dict]:
    return db.get_transactions()

def get_report() -> list[dict]:
    return db.get_report()

def get_portfolio() -> dict:
    """
    Current portfolio state: index composition enriched with prices and
    the current holdings. No buys are made. Market data comes from the
    cached snapshot; positions are always read fresh from the DB.
    """
    trade_date, df = get_snapshot()
    df = df.copy()

    pos     = db.load_positions_df()
    pos_idx = pos.set_index('ticker')
    df['current_shares'] = (
        df['ticker'].map(pos_idx['shares']).fillna(0).astype(int)
    )
    df['current_value'] = df['current_shares'] * df['price_rub']
    df['cost_basis']    = df['current_shares'] * df['ticker'].map(pos_idx['avg_price']).fillna(0)

    rows = df.sort_values('current_value', ascending=False)[
        ['ticker', 'shortnames', 'weight', 'price_rub',
         'current_shares', 'current_value', 'cost_basis']
    ].to_dict('records')
    for row in rows:
        row['name']       = row.pop('shortnames')
        row['price']      = float(row.pop('price_rub'))
        row['shares']     = int(row.pop('current_shares'))
        row['value']      = float(row.pop('current_value'))
        row['cost_basis'] = float(row.pop('cost_basis'))
        row['weight']     = float(row['weight'])

    return {
        'trade_date':      trade_date,
        'rows':            rows,
        'portfolio_value': float(df['current_value'].sum()),
        'cost_basis':      float(df['cost_basis'].sum()),
        'n_held':          int((df['current_shares'] > 0).sum()),
        'n_index':         len(df),
    }

# ============================================================
# ALLOCATOR — commission-aware greedy, minimizes tracking error
# ============================================================
def allocate(
    df: pd.DataFrame,
    investable_cash: float,
    target_values: np.ndarray,
    commission: float = COMMISSION,
) -> tuple[pd.DataFrame, float]:
    """
    Given per-ticker target *stock* values (RUB) and a cash budget,
    return (df_with_buy_columns, leftover_cash).

    investable_cash : stock-buying budget (already net of commission)
                      = new_cash / (1 + commission)
    target_values   : desired stock value per ticker, summing to ~investable_cash
    """
    price     = df['price_rub'].to_numpy(dtype=float)
    lot       = df['lot_size'].to_numpy(dtype=np.int64)
    lot_price = price * lot
    lot_cost  = lot_price * (1.0 + commission)

    # start from floor
    num_lots = np.floor(target_values / lot_price).astype(np.int64)
    cash     = investable_cash * (1 + commission) - float((num_lots * lot_cost).sum())

    # greedy: add 1 lot at a time to reduce L1 error
    for _ in range(len(df) + 1):
        cur    = num_lots * lot_price
        gain   = np.abs(cur - target_values) - np.abs(cur + lot_price - target_values)
        afford = lot_cost <= cash + 1e-6
        score  = np.where(afford, gain, -np.inf)
        best   = int(np.argmax(score))
        if score[best] <= 1e-9:
            break
        num_lots[best] += 1
        cash          -= lot_cost[best]

    # phase 2 — optionally spend remaining cash even at slight error cost
    while cash + 1e-6 >= lot_cost.min():
        cur     = num_lots * lot_price
        penalty = np.abs(cur + lot_price - target_values) - np.abs(cur - target_values)
        afford  = lot_cost <= cash + 1e-6
        score   = np.where(afford, -penalty, -np.inf)
        best    = int(np.argmax(score))
        if not np.isfinite(score[best]):
            break
        num_lots[best] += 1
        cash          -= lot_cost[best]

    df = df.copy()
    df['buy_shares']   = num_lots * lot
    df['buy_value']    = df['buy_shares'] * price
    df['buy_comm']     = df['buy_value'] * commission
    df['buy_cost']     = df['buy_value'] + df['buy_comm']
    return df, cash

# ============================================================
# MAIN PIPELINE
# ============================================================
# Serializes overlapping runs inside this process. Across processes
# (webapp + CLI) SQLite's BEGIN IMMEDIATE in db.apply_run is the hard
# guarantee; this lock just turns the wait into a friendly error.
_RUN_LOCK = threading.Lock()

def run(new_cash: float, dry_run: bool = False, max_age: float = 0.0) -> dict:
    """
    Run one DCA cycle. Returns a structured result; only persists
    (positions / transactions / report) when dry_run is False.

    max_age : max age of the market snapshot in seconds. 0 (default)
              forces a fresh fetch — an applied run must allocate on
              current prices. Previews may pass SNAPSHOT_TTL to reuse
              the cached snapshot.
    """
    if not _RUN_LOCK.acquire(blocking=False):
        raise RuntimeError("Another run is already in progress")
    try:
        return _run_locked(new_cash, dry_run, max_age)
    finally:
        _RUN_LOCK.release()

def _run_locked(new_cash: float, dry_run: bool, max_age: float) -> dict:
    # --- 1. Market snapshot (composition + prices + lot sizes) ---
    trade_date, df = get_snapshot(max_age=max_age)
    df = df.copy()

    # --- 2. Load current positions ---
    pos     = db.load_positions_df()
    pos_idx = pos.set_index('ticker')
    df['current_shares'] = (
        df['ticker'].map(pos_idx['shares']).fillna(0).astype(int)
    )
    df['current_value'] = df['current_shares'] * df['price_rub']
    df['cost_basis']    = df['current_shares'] * df['ticker'].map(pos_idx['avg_price']).fillna(0)

    portfolio_value = float(df['current_value'].sum())
    total_after     = portfolio_value + new_cash

    # --- 3. Target end-state stock value per ticker ---
    w = (df['weight'] / 100).to_numpy(dtype=float)
    w = w / w.sum()                                # normalize to 1.0
    target_end = w * (total_after / (1 + COMMISSION))   # net of commission

    # --- 4. Positive gaps only (never sell in DCA mode) ---
    current_vals = df['current_value'].to_numpy(dtype=float)
    gaps = np.clip(target_end - current_vals, a_min=0, a_max=None)

    investable = new_cash / (1 + COMMISSION)

    carried      = False
    carry_reason = None

    if gaps.sum() <= 0:
        carried      = True
        carry_reason = "Portfolio already at or above target — carrying cash."
        df['buy_shares'] = 0
        df['buy_value']  = 0.0
        df['buy_comm']   = 0.0
        df['buy_cost']   = 0.0
        buys = df.iloc[0:0]
    else:
        # scale gaps so they sum to investable
        scaled_targets = gaps * (investable / gaps.sum())

        # --- 5. Allocate ---
        df, leftover = allocate(df, investable, scaled_targets)
        buys = df[df['buy_shares'] > 0].copy()

        # --- 6. Carry-forward check ---
        if len(buys) < CARRY_THRESHOLD:
            carried = True
            carry_reason = (
                f"Only {len(buys)} ticker(s) fit in budget "
                f"(threshold {CARRY_THRESHOLD}). Carrying cash to next period."
            )
            df['buy_shares'] = 0
            df['buy_value']  = 0.0
            df['buy_comm']   = 0.0
            df['buy_cost']   = 0.0
            buys = df.iloc[0:0]

    # --- 7. Summary ---
    stock_bought   = float(df['buy_value'].sum())
    comm_paid      = float(df['buy_comm'].sum())
    total_spent    = stock_bought + comm_paid
    leftover_final = new_cash - total_spent

    buy_rows = []
    for _, r in buys.sort_values('buy_value', ascending=False).iterrows():
        buy_rows.append({
            'ticker':     r['ticker'],
            'name':       r['shortnames'],
            'weight':     float(r['weight']),
            'shares':     int(r['buy_shares']),
            'price':      float(r['price_rub']),
            'value':      float(r['buy_value']),
            'commission': float(r['buy_comm']),
        })

    # --- 8. Tracking metrics ---
    l1_error = None
    if len(buys):
        post_shares = df['current_shares'] + df['buy_shares']
        post_value  = post_shares * df['price_rub']
        post_total  = post_value.sum()
        df['post_weight'] = post_value / post_total * 100
        df['target_w']    = w * 100
        df['weight_diff'] = df['post_weight'] - df['target_w']
        l1_error = float(df['weight_diff'].abs().sum() / 2)  # half-sum convention
    else:
        df['post_weight'] = 0.0
        df['target_w']    = w * 100
        df['weight_diff'] = 0.0
    # post_value is kept in the df for the report in both branches
    df['post_value'] = (df['current_shares'] + df['buy_shares']) * df['price_rub']

    # --- 9. Persist ---
    if dry_run:
        return {
            'trade_date':      trade_date,
            'portfolio_value': portfolio_value,
            'total_after':     total_after,
            'buys':            buy_rows,
            'stock_bought':    stock_bought,
            'commission_paid': comm_paid,
            'total_spent':     total_spent,
            'leftover':        leftover_final,
            'l1_error':        l1_error,
            'carried':         carried,
            'carry_reason':    carry_reason,
            'rows':            len(df),
            'dry_run':         True,
        }

    # Plain Python types: sqlite3 does not bind numpy scalars.
    report_df = df[['ticker', 'shortnames', 'weight', 'price_rub', 'lot_size',
                    'current_shares', 'current_value', 'cost_basis',
                    'buy_shares', 'buy_value', 'buy_comm',
                    'post_value', 'post_weight', 'target_w', 'weight_diff']]
    report_rows = [
        {'ticker': r['ticker'], 'name': r['shortnames'], 'weight': float(r['weight']),
         'price': float(r['price_rub']), 'lot_size': int(r['lot_size']),
         'current_shares': int(r['current_shares']),
         'current_value': float(r['current_value']), 'cost_basis': float(r['cost_basis']),
         'buy_shares': int(r['buy_shares']), 'buy_value': float(r['buy_value']),
         'buy_comm': float(r['buy_comm']), 'post_value': float(r['post_value']),
         'post_weight': float(r['post_weight']), 'target_w': float(r['target_w']),
         'weight_diff': float(r['weight_diff'])}
        for r in report_df.to_dict('records')]

    run_id = db.apply_run(
        run_date=date.today().isoformat(),
        amount=new_cash,
        trade_date=trade_date,
        buys=buy_rows,
        report_rows=report_rows,
    )
    logger.info(
        "Run %d applied: %s RUB, %d buys, %.2f RUB leftover",
        run_id, f"{new_cash:,.2f}", len(buy_rows), leftover_final)
    if carried:
        logger.warning("Run %d carried cash: %s", run_id, carry_reason)

    return {
        'trade_date':      trade_date,
        'portfolio_value': portfolio_value,
        'total_after':     total_after,
        'buys':            buy_rows,
        'stock_bought':    stock_bought,
        'commission_paid': comm_paid,
        'total_spent':     total_spent,
        'leftover':        leftover_final,
        'l1_error':        l1_error,
        'carried':         carried,
        'carry_reason':    carry_reason,
        'rows':            len(df),
        'dry_run':         False,
        'run_id':          run_id,
    }

# ============================================================
# UNDO — one step back, git-style
# ============================================================
def latest_undoable_run() -> dict | None:
    """The most recent applied run the UI can offer to undo (or None)."""
    return db.latest_undoable_run()

def undo() -> dict:
    """
    Reverse the most recent applied run: restore the pre-run positions,
    mark the run undone, and append UNDO rows to the audit log. Only
    the latest applied run can be undone at a time; after an undo the
    previous run becomes the target.
    """
    if not _RUN_LOCK.acquire(blocking=False):
        raise RuntimeError("Another run is already in progress")
    try:
        target = db.latest_undoable_run()
        if target is None:
            raise RuntimeError("Nothing to undo — no applied runs")
        res = db.undo_run(target['id'], date.today().isoformat())
        logger.info(
            "Run %d undone (%s, %s RUB), %d buys reversed",
            res['id'], res['date'], f"{res['amount'] or 0:,.2f}", res['n_reversed'])
        return res
    finally:
        _RUN_LOCK.release()
