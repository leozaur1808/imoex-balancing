"""
dca.py — Core logic for bi-weekly DCA into IMOEX with position tracking.

Refactored from the original imoex_dca.py CLI script. The pipeline
functions here return structured results instead of printing, so both
the CLI (imoex_dca.py) and the webapp (main.py) can reuse them.

Files (created automatically):
    data/positions.csv      current holdings
    data/transactions.csv   full audit log
    export/composition.csv  latest run report
"""

from __future__ import annotations
from datetime import date, datetime, timedelta
from pathlib import Path
import threading
import time
import requests
import numpy as np
import pandas as pd

# ============================================================
# CONFIG
# ============================================================
COMMISSION       = 0.045/100        # 0.045% per trade
CARRY_THRESHOLD  = 1             # skip buying if fewer than N tickers fit
INDEX_NAME       = "IMOEX"
DATA_DIR         = Path("data")
EXPORT_DIR       = Path("export")
POSITIONS_FILE   = DATA_DIR / "positions.csv"
TRANSACTIONS_FILE= DATA_DIR / "transactions.csv"
REPORT_FILE      = EXPORT_DIR / "composition.csv"

# Market data (composition + closes) is settled once per trading day,
# so the dashboard serves it from this in-process cache.
SNAPSHOT_TTL     = 3600           # seconds

DATA_DIR.mkdir(exist_ok=True)
EXPORT_DIR.mkdir(exist_ok=True)

# ============================================================
# MARKET SNAPSHOT — cached composition + prices
# ============================================================
_snapshot_lock = threading.Lock()
_snapshot      = None  # (trade_date, df, fetched_at)

def get_snapshot(max_age: float = SNAPSHOT_TTL) -> tuple[str, pd.DataFrame]:
    """
    (trade_date, composition_df_with_prices). Served from the in-process
    cache while fresh. max_age=0 forces a fresh fetch (used by runs,
    which must allocate on current prices). The lock is held during the
    fetch so concurrent callers wait and then reuse the result.
    """
    global _snapshot
    with _snapshot_lock:
        if _snapshot is not None and time.time() - _snapshot[2] < max_age:
            return _snapshot[0], _snapshot[1]
        df = get_index_composition(INDEX_NAME)
        trade_date = df.loc[0, 'trade_session_date']
        df['price_rub'] = get_price_by_ticker(df['ticker'], trade_date)
        _snapshot = (trade_date, df, time.time())
        return trade_date, df

def invalidate_snapshot() -> None:
    """Drop the cache (called on login so the dashboard refreshes)."""
    global _snapshot
    with _snapshot_lock:
        _snapshot = None

# ============================================================
# MOEX ISS DATA
# ============================================================
def get_index_composition(indexid: str = "IMOEX", limit: int = 100) -> pd.DataFrame:
    """
    Fetches all pages of index analytics for a given index from MOEX ISS
    and returns a pandas DataFrame.

    Parameters
    ----------
    indexid : str
        Index identifier (e.g. 'IMOEX', 'RTS').
    limit : int
        Number of rows per page. MOEX ISS caps this at 100.

    Returns
    -------
    pd.DataFrame
        Combined analytics table.
    """
    base_url = (
        f"https://iss.moex.com/iss/statistics/engines/stock/markets/index/analytics/{indexid}.json?limit={limit}"
    )
    resp = requests.get(base_url, timeout=30)
    resp.raise_for_status()
    payload = resp.json()['analytics']

    columns = payload['columns']
    data = payload['data']
    return pd.DataFrame(
        data, columns=columns
    )

def get_lot_size_by_ticker(tickers: list[str]) -> list[float]:
    lots = []
    base_url = (
        f"https://iss.moex.com/iss/engines/stock/markets/shares/boards/TQBR/securities.json?iss.meta=off&iss.only=securities&securities.columns=SECID,LOTSIZE"
    )
    resp = requests.get(base_url, timeout=30)
    resp.raise_for_status()
    payload = resp.json()['securities']
    data = payload['data']
    ticker_lot = {ticker: float(lot) for (ticker, lot) in data}
    return [ticker_lot[ticker] for ticker in tickers]

def _price_column(columns: list[str]) -> int:
    """Index of the price column in a MOEX history payload."""
    for name in ("LEGALCLOSEPRICE", "CLOSE"):
        if name in columns:
            return columns.index(name)
    return 9  # original hard-coded index

def get_price_by_ticker(tickers: list[str], trade_date: str) -> list[float]:
    """
    Legal close price per ticker on trade_date. If no data exists for
    that exact date (e.g. the market is still trading), fall back to
    the most recent close within the previous 30 days.
    """
    prices = []
    for ticker in tickers:
        base_url = (
            f"https://iss.moex.com/iss/history/engines/stock/markets/shares/boards/TQBR/securities/{ticker}.json?from={trade_date}&till={trade_date}"
        )
        resp = requests.get(base_url, timeout=30)
        resp.raise_for_status()
        payload = resp.json()['history']
        columns, data = payload['columns'], payload['data']

        if not data:
            from_dt = (
                datetime.strptime(trade_date, "%Y-%m-%d") - timedelta(days=30)
            ).date().isoformat()
            base_url = (
                f"https://iss.moex.com/iss/history/engines/stock/markets/shares/boards/TQBR/securities/{ticker}.json?from={from_dt}&till={trade_date}"
            )
            resp = requests.get(base_url, timeout=30)
            resp.raise_for_status()
            payload = resp.json()['history']
            columns, data = payload['columns'], payload['data']

        if not data:
            raise RuntimeError(f"No price data for {ticker} around {trade_date}")

        idx = _price_column(columns)
        prices.append(float(data[-1][idx]))
    return prices

# ============================================================
# PERSISTENCE — positions & transactions
# ============================================================
def load_positions() -> pd.DataFrame:
    if POSITIONS_FILE.exists():
        df = pd.read_csv(POSITIONS_FILE)
        df['shares']    = df['shares'].astype(int)
        df['avg_price'] = df['avg_price'].astype(float)
        return df
    return pd.DataFrame(columns=['ticker', 'shares', 'avg_price'])

def save_positions(pos: pd.DataFrame) -> None:
    pos.to_csv(POSITIONS_FILE, index=False)

def append_transactions(rows: list[dict]) -> None:
    if not rows:
        return
    df = pd.DataFrame(rows)
    header = not TRANSACTIONS_FILE.exists()
    df.to_csv(TRANSACTIONS_FILE, mode='a', header=header, index=False)

# ============================================================
# READERS — for the webapp dashboard
# ============================================================
def get_positions() -> list[dict]:
    pos = load_positions().sort_values('ticker')
    rows = pos.to_dict('records')
    for row in rows:
        row['shares']    = int(row['shares'])
        row['avg_price'] = float(row['avg_price'])
    return rows

def get_transactions() -> list[dict]:
    if not TRANSACTIONS_FILE.exists():
        return []
    df = pd.read_csv(TRANSACTIONS_FILE)
    rows = df.sort_values('date', ascending=False).to_dict('records')
    for row in rows:
        row['shares']     = int(row['shares'])
        row['price']      = float(row['price'])
        row['value']      = float(row['value'])
        row['commission'] = float(row['commission'])
    return rows

def get_report() -> list[dict]:
    if not REPORT_FILE.exists():
        return []
    df = pd.read_csv(REPORT_FILE)
    return df.to_dict('records')

def get_portfolio() -> dict:
    """
    Current portfolio state: index composition enriched with prices and
    the current holdings. No buys are made. Market data comes from the
    cached snapshot; positions are always read fresh from disk.
    """
    trade_date, df = get_snapshot()
    df = df.copy()

    pos = load_positions()
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
def run(new_cash: float, dry_run: bool = False, max_age: float = 0.0) -> dict:
    """
    Run one DCA cycle. Returns a structured result; only persists
    (positions / transactions / report) when dry_run is False.

    max_age : max age of the market snapshot in seconds. 0 (default)
              forces a fresh fetch — an applied run must allocate on
              current prices. Previews may pass SNAPSHOT_TTL to reuse
              the cached snapshot.
    """
    # --- 1. Market snapshot (composition + prices) ---
    trade_date, df = get_snapshot(max_age=max_age)
    df = df.copy()
    df['lot_size']  = get_lot_size_by_ticker(df['ticker'])

    # --- 2. Load current positions ---
    pos = load_positions()
    df['current_shares'] = (
        df['ticker'].map(pos.set_index('ticker')['shares']).fillna(0).astype(int)
    )
    df['current_value'] = df['current_shares'] * df['price_rub']

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
        # update df for export
        df['post_value'] = post_value
    else:
        df['post_weight'] = 0.0
        df['target_w']    = w * 100
        df['weight_diff'] = 0.0
        df['post_value']  = df['current_value']

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

    # update positions
    for _, r in buys.iterrows():
        t = r['ticker']
        mask = pos['ticker'] == t
        if mask.any():
            old_sh  = int(pos.loc[mask, 'shares'].iloc[0])
            old_avg = float(pos.loc[mask, 'avg_price'].iloc[0])
            new_sh  = old_sh + int(r['buy_shares'])
            new_cost = old_sh * old_avg + float(r['buy_value']) + float(r['buy_comm'])
            pos.loc[mask, 'shares']    = new_sh
            pos.loc[mask, 'avg_price'] = new_cost / new_sh
        else:
            pos = pd.concat([pos, pd.DataFrame([{
                'ticker':    t,
                'shares':    int(r['buy_shares']),
                'avg_price': (float(r['buy_value']) + float(r['buy_comm'])) / int(r['buy_shares']),
            }])], ignore_index=True)

    save_positions(pos)

    # transaction log
    today = date.today().isoformat()
    append_transactions([
        {
            'date':       today,
            'ticker':     r['ticker'],
            'action':     'BUY',
            'shares':     int(r['buy_shares']),
            'price':      float(r['price_rub']),
            'value':      float(r['buy_value']),
            'commission': float(r['buy_comm']),
        }
        for _, r in buys.iterrows()
    ])

    # report
    report_cols = ['ticker', 'shortnames', 'weight', 'price_rub', 'lot_size',
                   'current_shares', 'current_value',
                   'buy_shares', 'buy_value', 'buy_comm',
                   'post_value', 'post_weight', 'target_w', 'weight_diff']
    df[report_cols].to_csv(REPORT_FILE, index=False)

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
    }
