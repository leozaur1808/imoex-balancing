/* Index DCA webapp — admin page logic (vanilla JS) */

const $ = (id) => document.getElementById(id);

const esc = (v) => String(v).replace(/[&<>"']/g,
  (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

let me = null;

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

function flashMsg(id, text) {
  const el = $(id);
  el.textContent = text;
  el.hidden = false;
  setTimeout(() => { el.hidden = true; }, 2500);
}

async function renderUsers() {
  const { rows } = await api('/admin/users');
  const tbody = $('users-table').querySelector('tbody');
  tbody.innerHTML = rows.map((u) =>
    `<tr data-id="${esc(u.id)}">
       <td>${esc(u.email)}</td>
       <td>${u.is_admin ? '<span class="role-badge">admin</span>' : ''}</td>
       <td>${esc(u.index_id)}</td>
       <td>${Number(u.commission_percent).toFixed(3)}</td>
       <td>${u.locked ? '<span class="locked-badge">locked</span>' : '—'}</td>
       <td>
         <div class="admin-actions">
           <button class="btn-icon" data-act="pw" title="Reset password">✎ pw</button>
           ${u.is_admin ? '' : `<button class="btn-icon ${u.locked ? '' : 'danger'}" data-act="lock"
              title="${u.locked ? 'Unlock' : 'Lock'}">${u.locked ? '🔓 unlock' : '🔒 lock'}</button>`}
         </div>
       </td>
     </tr>`).join('');
}

async function createUser() {
  $('new-err').hidden = true;
  const email = $('new-email').value.trim();
  const password = $('new-password').value;
  if (!email || password.length < 4) {
    $('new-err').textContent = 'Provide an email and a password of at least 4 characters.';
    $('new-err').hidden = false;
    return;
  }
  try {
    await post('/admin/users', { email, password });
    $('new-email').value = '';
    $('new-password').value = '';
    flashMsg('new-msg', 'Created');
    await renderUsers();
  } catch (e) {
    $('new-err').textContent = e.message;
    $('new-err').hidden = false;
  }
}

async function resetPassword(tr) {
  const pw = prompt('New password (at least 4 characters):');
  if (pw === null) return;
  if (pw.length < 4) { alert('Too short'); return; }
  try {
    await patch(`/admin/users/${tr.dataset.id}/password`, { password: pw });
    flashMsg('new-msg', 'Password reset');
  } catch (e) { alert(e.message); }
}

async function toggleLock(tr) {
  const locked = !tr.querySelector('.locked-badge');
  if (locked && !confirm('Lock this account? The user will not be able to log in.')) return;
  try {
    await patch(`/admin/users/${tr.dataset.id}/lock`, { locked });
    await renderUsers();
  } catch (e) { alert(e.message); }
}

document.addEventListener('DOMContentLoaded', async () => {
  $('new-user').addEventListener('click', createUser);
  $('users-table').addEventListener('click', (e) => {
    const btn = e.target.closest('button[data-act]');
    if (!btn) return;
    const tr = btn.closest('tr');
    if (btn.dataset.act === 'pw') resetPassword(tr);
    else toggleLock(tr);
  });
  $('logout-btn').addEventListener('click', async () => {
    try { await api('/api/logout', { method: 'POST' }); } finally { window.location.href = '/login.html'; }
  });

  try {
    me = await api('/api/me');
  } catch (e) {
    window.location.href = '/login.html';
    return;
  }
  if (!me.is_admin) {
    window.location.href = '/';
    return;
  }
  renderUsers().catch((e) => alert(e.message));
});
