/* Index DCA webapp — dashboard logic (vanilla JS) */

const $ = (id) => document.getElementById(id);

const fmtNum = (v, digits = 2) =>
  Number(v).toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits });

/* escape dynamic values before injecting into innerHTML */
const esc = (v) => String(v).replace(/[&<>"']/g,
  (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

let me = null;   // { email, is_admin, commission_percent, index_id }

async function api(path, opts = {}) {
  const r = await fetch(path, opts);
  if (r.status === 401) {
    window.location.href = '/login.html';
    throw new Error('Not authenticated');
  }
  if (!r.ok) {
    const body = await r.json().catch(() => ({}));
    throw new Error(body.detail || `HTTP ${r.status}`);
  }
  return r.json();
}

const post = (path, body) => api(path, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});
const patch = (path, body) => api(path, {
  method: 'PATCH',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});

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

/* ---------- settings ---------- */

function renderSettings() {
  $('settings-panel').hidden = false;
  $('set-commission').value = me.commission_percent;
  $('set-index').value = me.index_id;
  if (me.is_admin) $('admin-link').hidden = false;
}

function flashSettingsSaved() {
  $('settings-msg').hidden = false;
  setTimeout(() => { $('settings-msg').hidden = true; }, 2000);
}

async function saveSettings() {
  const commission = parseFloat($('set-commission').value);
  const indexId = $('set-index').value.trim();
  if (!(commission >= 0 && commission < 100)) {
    alert('Commission must be a number in [0, 100).');
    return;
  }
  if (!/^[A-Za-z0-9]{1,32}$/.test(indexId)) {
    alert('Index id: 1-32 letters/digits (e.g. IMOEX, MOEXBMI).');
    return;
  }
  try {
    await patch('/api/settings', { commission_percent: commission, index_id: indexId });
    me.commission_percent = commission;
    me.index_id = indexId;
    flashSettingsSaved();
    $('settings-details').removeAttribute('open');
    await loadPortfolio();
    if (activeTab === 'holdings') await loadTab();
  } catch (e) {
    alert(e.message);
  }
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
  tbody.innerHTML = rows.map((row) => {
    const cells = cols.map(([key]) => {
      const v = row[key];
      const cell = v === null || v === undefined ? '' : (fmt[key] ? fmt[key](v) : v);
      return `<td>${esc(cell)}</td>`;
    });
    // BUY rows in the transactions tab get an edit button (actual price fix)
    if (activeTab === 'transactions' && row.action === 'BUY') {
      cells.push(`<td><button class="btn-icon" data-tx-id="${row.id}" data-price="${row.price}"
        data-comm="${row.commission}" data-ticker="${esc(row.ticker)}"
        title="Correct the actual price / commission (runs are priced at t-1 legal closes)">✎ price</button></td>`);
    }
    return '<tr>' + cells.join('') + '</tr>';
  }).join('');
  if (activeTab === 'transactions' && rows.some((r) => r.action === 'BUY')) {
    thead.innerHTML += '<th>Actual</th>';
  }
  $('tab-empty').hidden = rows.length > 0;
}

async function loadTab() {
  if (activeTab === 'import') return;
  const rows = await TABS[activeTab].load();
  renderTab(rows);
}

function setTabVisible(tab) {
  $('mgmt-positions').hidden = tab !== 'holdings';
  $('import-holder').hidden = tab !== 'import';
  $('tab-table').parentElement.style.display = tab === 'import' ? 'none' : '';
  $('tab-empty').style.display = tab === 'import' ? 'none' : '';
}

/* ---------- positions management ---------- */

async function loadPositions() {
  const { rows } = await api('/api/positions');
  const tbody = $('pos-table').querySelector('tbody');
  tbody.innerHTML = rows.map((p) =>
    `<tr data-ticker="${esc(p.ticker)}">
       <td>${esc(p.ticker)}</td>
       <td>${p.shares}</td>
       <td>${fmtNum(p.avg_price, 4)}</td>
       <td>
         <button class="btn-icon" data-act="edit" title="Edit shares / average price">✎</button>
         <button class="btn-icon danger" data-act="del" title="Remove this position">✕</button>
       </td>
     </tr>`).join('');
  $('pos-empty').hidden = rows.length > 0;
  $('pos-table').parentElement.style.display = rows.length ? '' : 'none';
}

function flash(msg, hiddenEl) {
  $(hiddenEl).textContent = msg;
  $(hiddenEl).hidden = false;
  setTimeout(() => { $(hiddenEl).hidden = true; }, 2000);
}

async function savePosition(ticker, shares, avgPrice) {
  await post('/api/positions', { ticker, shares, avg_price: avgPrice });
}

async function posAdd() {
  const ticker = $('pos-ticker').value.trim().toUpperCase();
  const shares = parseInt($('pos-shares').value, 10);
  const avg = parseFloat($('pos-avg').value);
  if (!ticker || !(shares > 0) || !(avg >= 0)) {
    alert('Fill in ticker, positive shares and an average price >= 0.');
    return;
  }
  try {
    await savePosition(ticker, shares, avg);
    $('pos-ticker').value = ''; $('pos-shares').value = ''; $('pos-avg').value = '';
    flash('Saved', 'pos-msg');
    await loadPositions();
    await loadPortfolio();
    if (activeTab === 'holdings') await loadTab();
  } catch (e) { alert(e.message); }
}

async function posEdit(tr) {
  const ticker = tr.dataset.ticker;
  const cells = tr.querySelectorAll('td');
  const shares0 = cells[1].textContent;
  const avg0 = cells[2].textContent;
  const shares = prompt(`Shares for ${ticker}`, shares0);
  if (shares === null) return;
  const avg = prompt(`Average price for ${ticker}`, avg0);
  if (avg === null) return;
  const s = parseInt(shares, 10);
  const a = parseFloat(avg);
  if (!(s >= 0) || isNaN(a) || a < 0) { alert('Bad number'); return; }
  try {
    await savePosition(ticker, s, a);
    await loadPositions();
    await loadPortfolio();
    if (activeTab === 'holdings') await loadTab();
  } catch (e) { alert(e.message); }
}

async function posDelete(tr) {
  const ticker = tr.dataset.ticker;
  if (!confirm(`Remove position ${ticker}?`)) return;
  try {
    await api('/api/positions/' + encodeURIComponent(ticker), { method: 'DELETE' });
    await loadPositions();
    await loadPortfolio();
    if (activeTab === 'holdings') await loadTab();
  } catch (e) { alert(e.message); }
}

/* ---------- transaction price correction ---------- */

async function txEdit(btn) {
  const id = btn.dataset.txId;
  const price0 = btn.dataset.price;
  const comm0 = btn.dataset.comm;
  const ticker = btn.dataset.ticker;
  const price = prompt(`Actual price for ${ticker} (run price: ${price0})`, price0);
  if (price === null) return;
  const comm = prompt(`Commission (run commission: ${comm0})`, comm0);
  if (comm === null) return;
  const p = parseFloat(price);
  const c = parseFloat(comm);
  if (!(p > 0) || isNaN(c) || c < 0) { alert('Bad number'); return; }
  try {
    await patch('/api/transactions/' + id, { price: p, commission: c });
    await loadPortfolio();
    await loadTab();
    if (activeTab === 'holdings') await loadPositions();
  } catch (e) { alert(e.message); }
}

/* ---------- imports ---------- */

async function doImport(kind) {
  const csv = $(kind === 'pos' ? 'imp-pos-csv' : 'imp-tx-csv').value;
  const btn = $(kind === 'pos' ? 'imp-pos-btn' : 'imp-tx-btn');
  const msg = $(kind === 'pos' ? 'imp-pos-msg' : 'imp-tx-msg');
  const err = $(kind === 'pos' ? 'imp-pos-err' : 'imp-tx-err');
  if (!csv.trim()) {
    err.textContent = 'Paste CSV rows first';
    err.hidden = false;
    return;
  }
  err.hidden = true;
  msg.hidden = true;
  btn.disabled = true;
  try {
    const res = await post(kind === 'pos' ? '/api/import/positions' : '/api/import/transactions', { csv });
    msg.textContent = `Imported ${res.rows_imported} rows` + (res.run_id ? ` (run #${res.run_id})` : '');
    msg.hidden = false;
    $(kind === 'pos' ? 'imp-pos-csv' : 'imp-tx-csv').value = '';
    await loadPortfolio();
    await loadTab();
    await loadPositions();
  } catch (e) {
    err.textContent = e.message;
    err.hidden = false;
  } finally {
    btn.disabled = false;
  }
}

/* ---------- run flow ---------- */

function setBusy(busy, label) {
  $('run-spinner').hidden = !busy;
  if (label) $('spinner-label').textContent = label;
  $('preview-btn').disabled = busy;
  $('confirm-btn').disabled = busy;
  $('undo-btn').disabled = busy;
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
    ['Prices as of', esc(res.trade_date)],
    ['Index', esc(me.index_id)],
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
        `<tr><td>${esc(b.ticker)}</td><td>${esc(b.name)}</td><td>${b.weight.toFixed(2)}</td>` +
        `<td>${b.shares}</td><td>${fmtNum(b.price)}</td><td>${fmtNum(b.value)}</td>` +
        `<td>${fmtNum(b.commission, 4)}</td></tr>`
      ).join('')
    : '<tr><td colspan="7" class="muted-cell">No buy orders — ' +
      esc(res.carry_reason || 'nothing to buy') + '</td></tr>';
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
      await loadUndoTarget();
    }
  } catch (e) {
    showRunError(e.message);
  } finally {
    setBusy(false);
  }
}

/* ---------- undo ---------- */

async function loadUndoTarget() {
  const data = await api('/api/undo-target');
  const run = data.run;
  $('undo-wrap').hidden = !run;
  if (run) {
    $('undo-info').textContent =
      `run of ${run.date} · ${fmtNum(run.amount)} RUB — undo restores the positions to their state before that run`;
  }
}

async function doUndo() {
  if (!confirm('Undo the last applied run? Positions will be restored to their state before it.')) return;
  setBusy(true, 'Undoing last run…');
  hideRunError();
  try {
    const res = await api('/api/undo', { method: 'POST' });
    $('run-result').hidden = false;
    $('result-title').textContent = 'Last run undone';
    $('result-summary').innerHTML =
      `<span class="chip"><b>Run</b> ${esc(res.date)} · ${fmtNum(res.amount)} ₽</span>` +
      `<span class="chip"><b>Reversed</b> ${res.n_reversed} buys</span>`;
    $('buy-table').querySelector('tbody').innerHTML =
      '<tr><td colspan="7" class="muted-cell">Positions restored to their pre-run state.</td></tr>';
    $('confirm-btn').hidden = true;
    await loadPortfolio();
    await loadTab();
    await loadUndoTarget();
  } catch (e) {
    showRunError(e.message);
  } finally {
    setBusy(false);
  }
}

/* ---------- refresh market data ---------- */

async function doRefresh() {
  setBusy(true, 'Refreshing market data…');
  hideRunError();
  try {
    await api('/api/refresh', { method: 'POST' });
    await loadPortfolio();
    await loadTab();
  } catch (e) {
    showRunError(e.message);
  } finally {
    setBusy(false);
  }
}

/* ---------- wiring ---------- */

document.addEventListener('DOMContentLoaded', async () => {
  $('run-form').addEventListener('submit', (e) => {
    e.preventDefault();
    doRun(true);
  });
  $('confirm-btn').addEventListener('click', () => doRun(false));
  $('undo-btn').addEventListener('click', doUndo);
  $('refresh-btn').addEventListener('click', doRefresh);
  $('settings-btn').addEventListener('click', () => {
    const d = $('settings-details');
    if (d.hasAttribute('open')) d.removeAttribute('open');
    else d.setAttribute('open', '');
  });
  $('settings-save').addEventListener('click', saveSettings);
  $('pos-add').addEventListener('click', posAdd);
  $('pos-table').addEventListener('click', (e) => {
    const btn = e.target.closest('button[data-act]');
    if (!btn) return;
    const tr = btn.closest('tr');
    if (btn.dataset.act === 'edit') posEdit(tr);
    else posDelete(tr);
  });
  $('tab-table').addEventListener('click', (e) => {
    const btn = e.target.closest('button[data-tx-id]');
    if (btn) txEdit(btn);
  });
  $('imp-pos-btn').addEventListener('click', () => doImport('pos'));
  $('imp-tx-btn').addEventListener('click', () => doImport('tx'));

  document.querySelectorAll('.tab').forEach((btn) => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.tab').forEach((b) => b.classList.remove('active'));
      btn.classList.add('active');
      activeTab = btn.dataset.tab;
      setTabVisible(activeTab);
      if (activeTab === 'holdings') loadPositions().catch((e) => showRunError(e.message));
      loadTab().catch((e) => showRunError(e.message));
    });
  });

  $('logout-btn').addEventListener('click', async () => {
    try {
      await api('/api/logout', { method: 'POST' });
    } finally {
      window.location.href = '/login.html';
    }
  });

  setBusy(true, 'Loading portfolio data…');
  try {
    me = await api('/api/me');
    renderSettings();
    await loadPortfolio();
    setTabVisible('holdings');
    await loadPositions();
  } catch (e) {
    showRunError(e.message);
  } finally {
    setBusy(false);
  }
  loadUndoTarget().catch(() => { $('undo-wrap').hidden = true; });
});
