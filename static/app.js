/* IMOEX DCA webapp — dashboard logic (vanilla JS) */

const $ = (id) => document.getElementById(id);

const fmtNum = (v, digits = 2) =>
  Number(v).toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits });

async function api(path, opts = {}) {
  const r = await fetch(path, opts);
  if (r.status === 401) {
    window.location.href = '/login';
    throw new Error('Not authenticated');
  }
  if (!r.ok) {
    const body = await r.json().catch(() => ({}));
    throw new Error(body.detail || `HTTP ${r.status}`);
  }
  return r.json();
}

/* ---------- summary cards ---------- */

function renderCards(p) {
  $('c-value').textContent = fmtNum(p.portfolio_value) + ' ₽';
  $('c-cost').textContent = fmtNum(p.cost_basis) + ' ₽';
  const pl = p.portfolio_value - p.cost_basis;
  const el = $('c-pl');
  const pct = p.cost_basis > 0 ? (pl / p.cost_basis) * 100 : 0;
  el.textContent = `${pl >= 0 ? '+' : ''}${fmtNum(pl)} ₽ (${pct.toFixed(2)}%)`;
  el.classList.toggle('pos', pl >= 0);
  el.classList.toggle('neg', pl < 0);
  $('c-held').textContent = `${p.n_held} / ${p.n_index}`;
  $('trade-date').textContent = 'Prices as of ' + p.trade_date;
}

async function loadPortfolio() {
  const p = await api('/api/portfolio');
  renderCards(p);
  if (activeTab === 'holdings') renderTab(p.rows);
}

/* ---------- data tabs ---------- */

const TABS = {
  holdings: {
    load: async () => (await api('/api/portfolio')).rows,
    cols: [
      ['ticker', 'Ticker'], ['name', 'Name'], ['weight', 'Idx w%'],
      ['price', 'Price'], ['shares', 'Shares'],
      ['value', 'Value, RUB'], ['cost_basis', 'Cost basis, RUB'],
    ],
    fmt: {
      weight: (v) => Number(v).toFixed(2),
      price: (v) => fmtNum(v),
      value: (v) => fmtNum(v),
      cost_basis: (v) => fmtNum(v),
    },
  },
  transactions: {
    load: async () => (await api('/api/transactions')).rows,
    cols: [
      ['date', 'Date'], ['ticker', 'Ticker'], ['action', 'Action'],
      ['shares', 'Shares'], ['price', 'Price'],
      ['value', 'Value, RUB'], ['commission', 'Comm, RUB'],
    ],
    fmt: {
      price: (v) => fmtNum(v),
      value: (v) => fmtNum(v),
      commission: (v) => fmtNum(v, 4),
    },
  },
  report: {
    load: async () => (await api('/api/report')).rows,
    cols: [
      ['ticker', 'Ticker'], ['shortnames', 'Name'], ['weight', 'Idx w%'],
      ['price_rub', 'Price'], ['lot_size', 'Lot'],
      ['current_shares', 'Cur shares'], ['current_value', 'Cur value'],
      ['buy_shares', 'Buy shares'], ['buy_value', 'Buy value'], ['buy_comm', 'Comm'],
      ['post_value', 'Post value'], ['post_weight', 'Post w%'],
      ['target_w', 'Target w%'], ['weight_diff', 'Δw%'],
    ],
    fmt: {
      weight: (v) => Number(v).toFixed(2),
      price_rub: (v) => fmtNum(v),
      current_value: (v) => fmtNum(v),
      buy_value: (v) => fmtNum(v),
      buy_comm: (v) => fmtNum(v, 4),
      post_value: (v) => fmtNum(v),
      post_weight: (v) => Number(v).toFixed(2),
      target_w: (v) => Number(v).toFixed(2),
      weight_diff: (v) => Number(v).toFixed(2),
    },
  },
};

let activeTab = 'holdings';

function renderTab(rows) {
  const { cols, fmt = {} } = TABS[activeTab];
  const thead = $('tab-table').querySelector('thead');
  const tbody = $('tab-table').querySelector('tbody');
  thead.innerHTML = '<tr>' + cols.map(([, label]) => `<th>${label}</th>`).join('') + '</tr>';
  tbody.innerHTML = rows.map((row) =>
    '<tr>' + cols.map(([key]) => {
      const v = row[key];
      const cell = v === null || v === undefined ? '' : (fmt[key] ? fmt[key](v) : v);
      return `<td>${cell}</td>`;
    }).join('') + '</tr>'
  ).join('');
  $('tab-empty').hidden = rows.length > 0;
}

async function loadTab() {
  const rows = await TABS[activeTab].load();
  renderTab(rows);
}

/* ---------- run flow ---------- */

function setBusy(busy, label) {
  $('run-spinner').hidden = !busy;
  if (label) $('spinner-label').textContent = label;
  $('preview-btn').disabled = busy;
  $('confirm-btn').disabled = busy;
}

function showRunError(msg) {
  $('run-error').textContent = msg;
  $('run-error').hidden = false;
}

function hideRunError() {
  $('run-error').hidden = true;
}

function renderResult(res, dryRun) {
  $('run-result').hidden = false;
  let title = dryRun ? 'Dry run — preview of buy orders' : 'Run applied';
  if (res.carried) title += ' — cash carried';
  $('result-title').textContent = title;

  const items = [
    ['Stock bought', fmtNum(res.stock_bought) + ' ₽'],
    ['Commission', fmtNum(res.commission_paid, 4) + ' ₽'],
    ['Total spent', fmtNum(res.total_spent) + ' ₽'],
    ['Leftover carried', fmtNum(res.leftover) + ' ₽'],
  ];
  if (res.l1_error !== null && res.l1_error !== undefined) {
    items.push(['L1 weight error', res.l1_error.toFixed(4) + '%']);
  }
  $('result-summary').innerHTML = items
    .map(([k, v]) => `<span class="chip"><b>${k}</b> ${v}</span>`)
    .join('');

  const tbody = $('buy-table').querySelector('tbody');
  tbody.innerHTML = res.buys.length
    ? res.buys.map((b) =>
        `<tr><td>${b.ticker}</td><td>${b.name}</td><td>${b.weight.toFixed(2)}</td>` +
        `<td>${b.shares}</td><td>${fmtNum(b.price)}</td><td>${fmtNum(b.value)}</td>` +
        `<td>${fmtNum(b.commission, 4)}</td></tr>`
      ).join('')
    : '<tr><td colspan="7" class="muted">No buy orders — ' +
      (res.carry_reason || 'nothing to buy') + '</td></tr>';
}

async function doRun(dryRun) {
  const amount = parseFloat($('amount').value);
  if (!(amount > 0)) {
    showRunError('Enter a positive amount');
    return;
  }
  setBusy(true, dryRun ? 'Computing orders…' : 'Fetching fresh MOEX data & computing orders…');
  hideRunError();
  $('run-result').hidden = true;
  try {
    const res = await api('/api/run', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ amount, dry_run: dryRun }),
    });
    renderResult(res, dryRun);
    $('confirm-btn').hidden = !dryRun || res.carried;
    if (!dryRun) {
      await loadPortfolio();
      await loadTab();
    }
  } catch (e) {
    showRunError(e.message);
  } finally {
    setBusy(false);
  }
}

/* ---------- wiring ---------- */

document.addEventListener('DOMContentLoaded', () => {
  $('run-form').addEventListener('submit', (e) => {
    e.preventDefault();
    doRun(true);
  });
  $('confirm-btn').addEventListener('click', () => doRun(false));

  document.querySelectorAll('.tab').forEach((btn) => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.tab').forEach((b) => b.classList.remove('active'));
      btn.classList.add('active');
      activeTab = btn.dataset.tab;
      loadTab().catch((e) => showRunError(e.message));
    });
  });

  $('logout-btn').addEventListener('click', async () => {
    try {
      await api('/api/logout', { method: 'POST' });
    } finally {
      window.location.href = '/login';
    }
  });

  setBusy(true, 'Loading portfolio data…');
  loadPortfolio()
    .catch((e) => showRunError(e.message))
    .finally(() => setBusy(false));
});
