"""
imoex_dca.py — Bi-weekly DCA into IMOEX with position tracking.

Usage:
    python imoex_dca.py

Files (created automatically):
    data/positions.csv      current holdings
    data/transactions.csv   full audit log
    export/composition.csv latest run report
"""

from __future__ import annotations
import os
import sys
from datetime import date, datetime
from pathlib import Path
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

DATA_DIR.mkdir(exist_ok=True)
EXPORT_DIR.mkdir(exist_ok=True)

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

def get_price_by_ticker(tickers: list[str], date: str) -> list[float]:
    prices = []
    for ticker in tickers:
        base_url = (
            f"https://iss.moex.com/iss/history/engines/stock/markets/shares/boards/TQBR/securities/{ticker}.json?from={date}&till={date}"
        )
        resp = requests.get(base_url, timeout=30)
        resp.raise_for_status()
        payload = resp.json()['history']
        data = payload['data'][0]
        price = float(data[9])
        prices.append(price)
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
def run(new_cash: float, dry_run: bool = False) -> None:
    print(f"\n{'='*60}")
    print(f"  IMOEX DCA run — {datetime.now():%Y-%m-%d %H:%M:%S}")
    print(f"  New cash: {new_cash:,.2f} RUB")
    print(f"{'='*60}\n")

    # --- 1. Fetch index composition ---
    df = get_index_composition(INDEX_NAME)
    trade_date = df.loc[0, 'trade_session_date']
    df['price_rub'] = get_price_by_ticker(df['ticker'], trade_date)
    df['lot_size']  = get_lot_size_by_ticker(df['ticker'])

    # --- 2. Load current positions ---
    pos = load_positions()
    df['current_shares'] = (
        df['ticker'].map(pos.set_index('ticker')['shares']).fillna(0).astype(int)
    )
    df['current_value'] = df['current_shares'] * df['price_rub']

    portfolio_value = float(df['current_value'].sum())
    total_after     = portfolio_value + new_cash

    print(f"Current portfolio value : {portfolio_value:,.2f} RUB")
    print(f"After contribution      : {total_after:,.2f} RUB\n")

    # --- 3. Target end-state stock value per ticker ---
    w = (df['weight'] / 100).to_numpy(dtype=float)
    w = w / w.sum()                                # normalize to 1.0
    target_end = w * (total_after / (1 + COMMISSION))   # net of commission

    # --- 4. Positive gaps only (never sell in DCA mode) ---
    current_vals = df['current_value'].to_numpy(dtype=float)
    gaps = np.clip(target_end - current_vals, a_min=0, a_max=None)

    investable = new_cash / (1 + COMMISSION)

    if gaps.sum() <= 0:
        print("⚠ Portfolio already at or above target — carrying cash.\n")
        df['buy_shares'] = 0
        df['buy_value']  = 0.0
        df['buy_comm']   = 0.0
        df['buy_cost']   = 0.0
        leftover         = new_cash
        buys = df.iloc[0:0]
    else:
        # scale gaps so they sum to investable
        scaled_targets = gaps * (investable / gaps.sum())

        # --- 5. Allocate ---
        df, leftover = allocate(df, investable, scaled_targets)
        buys = df[df['buy_shares'] > 0].copy()

        # --- 6. Carry-forward check ---
        if len(buys) < CARRY_THRESHOLD:
            print(f"⚠ Only {len(buys)} ticker(s) fit in budget "
                  f"(threshold {CARRY_THRESHOLD}). Carrying cash to next period.\n")
            df['buy_shares'] = 0
            df['buy_value']  = 0.0
            df['buy_comm']   = 0.0
            df['buy_cost']   = 0.0
            leftover         = new_cash
            buys = df.iloc[0:0]

    # --- 7. Report ---
    stock_bought   = float(df['buy_value'].sum())
    comm_paid      = float(df['buy_comm'].sum())
    total_spent    = stock_bought + comm_paid
    leftover_final = new_cash - total_spent

    print("─── BUY ORDERS ───────────────────────────────────────")
    if len(buys):
        show = buys[['ticker', 'shortnames', 'weight',
                     'buy_shares', 'price_rub', 'buy_value', 'buy_comm']].copy()
        show.columns = ['ticker', 'name', 'idx_w%',
                        'shares', 'price', 'value_rub', 'comm_rub']
        print(show.to_string(index=False))
    else:
        print("(none)")

    print(f"\nStock value bought : {stock_bought:>15,.2f} RUB")
    print(f"Commission paid    : {comm_paid:>15,.2f} RUB")
    print(f"Total spent        : {total_spent:>15,.2f} RUB")
    print(f"Leftover carried   : {leftover_final:>15,.2f} RUB "
          f"({leftover_final/new_cash*100:.3f}%)")

    # --- 8. Tracking metrics ---
    if len(buys):
        post_shares = df['current_shares'] + df['buy_shares']
        post_value  = post_shares * df['price_rub']
        post_total  = post_value.sum()
        df['post_weight'] = post_value / post_total * 100
        df['target_w']    = w * 100
        df['weight_diff'] = df['post_weight'] - df['target_w']
        l1_error = df['weight_diff'].abs().sum() / 2     # half-sum convention
        print(f"Post-trade L1 weight error : {l1_error:.4f}% "
              f"(half-sum, 0 = perfect)")
        # update df for export
        df['post_value'] = post_value
    else:
        df['post_weight'] = 0.0
        df['target_w']    = w * 100
        df['weight_diff'] = 0.0
        df['post_value']  = df['current_value']

    # --- 9. Persist ---
    if dry_run:
        print("\n[dry-run] no files written.")
        return

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

    # excel report
    report_cols = ['ticker', 'shortnames', 'weight', 'price_rub', 'lot_size',
                   'current_shares', 'current_value',
                   'buy_shares', 'buy_value', 'buy_comm',
                   'post_value', 'post_weight', 'target_w', 'weight_diff']
    df[report_cols].to_csv(REPORT_FILE, index=False)

    print(f"\n✓ Positions saved    → {POSITIONS_FILE}")
    print(f"✓ Transactions log   → {TRANSACTIONS_FILE}")
    print(f"✓ Report             → {REPORT_FILE}")
    print(f"✓ Rows processed     : {len(df)}\n")


# ============================================================
# ENTRY POINT
# ============================================================
if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--dry-run":
        amount = float(sys.argv[2]) if len(sys.argv) > 2 else 10_000
        run(amount, dry_run=True)
    else:
        amount = float(input("Amount to invest this period (RUB): "))
        run(amount)