const $ = (id) => document.getElementById(id);
const state = { view: 'play', accounts: [], selected: null, authSession: null, avatarBase64: null, loadout: null };

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: 'same-origin',
    ...options,
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }
  });
  if (response.status === 401) { showLogin(); throw new Error('Nicht angemeldet.'); }
  if (!response.ok) {
    let message = `HTTP ${response.status}`;
    try { message = (await response.json()).message || message; } catch {}
    throw new Error(message);
  }
  if (response.status === 204) return null;
  return response.json();
}

function enumName(value, names) { return typeof value === 'number' ? names[value] : value; }
function primeName(value) { return enumName(value, ['Unknown', 'Prime', 'NonPrime']) || 'Unknown'; }
function sectionName(value) { return enumName(value, ['PlayReady', 'NotPlayReady']) || 'NotPlayReady'; }
function healthLabel(value) { return value === 'healthy' ? 'GC gesund' : value === 'gc-unknown' ? 'GC unvollständig' : value?.startsWith('error:') ? 'GC-Fehler' : 'Noch nicht geprüft'; }
function presenceLabel(account) { return account.inMatch === true ? 'In CS2-Match' : account.inMatch === false ? 'Nicht im Match' : 'Unbekannt'; }
function formatDate(value) { return value ? new Date(value).toLocaleString('de-DE') : '—'; }
function relativeTime(value) { if (!value) return '—'; const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1000); if (Math.abs(seconds) < 60) return seconds >= 0 ? 'gleich' : 'gerade eben'; const minutes = Math.round(Math.abs(seconds) / 60); return seconds >= 0 ? `in ${minutes} Min.` : `vor ${minutes} Min.`; }
function cooldownPhase(account) { if (account.cooldownSeconds == null && !account.cooldownEndsUtc) return 'Unbekannt'; if (remainingSeconds(account) > 0) return 'Aktiv'; return account.cooldownEndsUtc ? 'GC-Recheck fällig' : 'Bestätigt frei'; }
function nextCooldownText(account) { const seconds = account.nextCooldownSeconds; if (!seconds) return 'Unbekannt'; if (seconds === 20 * 60 * 60) return '20 Stunden'; if (seconds === 7 * 86400) return '7 Tage'; if (seconds === 31 * 86400) return '31 Tage'; if (seconds === 90 * 86400) return '90 Tage'; return '181 Tage'; }
function matchResult(match) { if (match.teamScore != null && match.opponentScore != null) return `${match.teamScore} : ${match.opponentScore}`; return match.resultCode == null ? 'Ergebnis nicht verfügbar' : `Match ${match.resultCode}`; }
function renderRecentMatches(account) { const matches = account.recentMatches || []; const content = matches.length ? matches.map(match => `<div class="match-row"><span class="match-map">${esc(match.map || 'CS2 Match')}</span><span class="match-score">${esc(matchResult(match))}</span><span class="match-date">${esc(formatDate(match.playedUtc))}</span></div>`).join('') : `<div class="matches-empty">${account.matchesStatus === 'ok' ? 'Keine aktuellen Spiele gefunden.' : 'Noch keine Matchdaten geladen.'}</div>`; return `<section class="matches-panel"><div class="matches-heading"><div><h4>Letzte Spiele</h4><small>${account.matchesCheckedUtc ? `Zuletzt geladen ${relativeTime(account.matchesCheckedUtc)}` : 'Abruf über den CS2 Game Coordinator'}</small></div><button class="secondary" data-command="RefreshMatches">${matches.length ? 'Aktualisieren' : 'Laden'}</button></div><div class="matches-list">${content}</div></section>`; }
function remainingSeconds(account) {
  if (account.cooldownEndsUtc) return Math.max(0, Math.floor((new Date(account.cooldownEndsUtc).getTime() - Date.now()) / 1000));
  return account.cooldownSeconds;
}
function duration(account) {
  const seconds = remainingSeconds(account);
  if (seconds == null) return 'Unbekannt';
  if (!seconds) return account.cooldownEndsUtc ? 'Abgelaufen – Status prüfen' : 'Kein Cooldown';
  const days = Math.floor(seconds / 86400), hours = Math.floor(seconds % 86400 / 3600), minutes = Math.floor(seconds % 3600 / 60);
  return `${days ? `${days}T ` : ''}${hours}h ${minutes}m`;
}
function esc(value) { return String(value ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c])); }
function avatarMarkup(account, size = '') {
  return account.avatarUrl
    ? `<img class="avatar-image ${size}" src="${esc(account.avatarUrl)}" alt="">`
    : `<span class="avatar-fallback ${size}">${esc((account.personaName || '?').slice(0, 1).toUpperCase())}</span>`;
}
function showLogin() { $('appView').classList.add('hidden'); $('loginView').classList.remove('hidden'); setButtonBusy('dashboardLoginButton', false, 'Dashboard öffnen'); }
function showApp(username) { $('loginView').classList.add('hidden'); $('appView').classList.remove('hidden'); $('userLabel').textContent = username || ''; }
function toast(message, bad = false) { const el = $('toast'); el.textContent = message; el.classList.remove('hidden'); el.style.background = bad ? '#a52318' : '#17324d'; setTimeout(() => el.classList.add('hidden'), 4500); }
function setButtonBusy(id, busy, label) { const button = $(id); if (!button) return; button.disabled = busy; button.querySelector('.button-spinner')?.classList.toggle('hidden', !busy); const text = button.querySelector('.button-text'); if (text && label) text.textContent = label; }
function setAuthStage(title, message, progress = 25) { if ($('authStage')) $('authStage').textContent = title; if ($('authMessage')) $('authMessage').textContent = message; if ($('authProgressBar')) $('authProgressBar').style.width = `${progress}%`; }

function setView(view) {
  state.view = view;
  document.querySelectorAll('.nav-item').forEach(item => item.classList.toggle('active', item.dataset.view === view));
  const settings = view === 'loadout' || view === 'personalize';
  $('accountsView').classList.toggle('hidden', settings);
  $('settingsView').classList.toggle('hidden', !settings);
  $('overview').classList.toggle('hidden', settings);
  $('loadoutSettings').classList.toggle('hidden', view !== 'loadout');
  $('personalizeSettings').classList.toggle('hidden', view !== 'personalize');
  $('pageTitle').textContent = view === 'play' ? 'Play Ready' : view === 'notplay' ? 'Not Play Ready' : view === 'loadout' ? 'Loadout' : 'Personalize';
  $('pageSubtitle').textContent = view === 'play' ? 'Accounts ohne aktiven Cooldown oder VAC' : view === 'notplay' ? 'Accounts mit Cooldown, VAC oder unbekanntem Status' : settings ? 'Serverseitige Einstellungen' : 'CS2 Accounts';
  const animatedView = settings ? $('settingsView') : $('accountsView'); animatedView.classList.remove('view-enter'); requestAnimationFrame(() => animatedView.classList.add('view-enter'));
  if (settings) loadSettings(); else loadAccounts();
}

async function loadAccounts() {
  const section = state.view === 'notplay' ? 'NotPlayReady' : 'PlayReady';
  const prime = $('primeFilter').value;
  try {
    state.accounts = await api(`/api/accounts?section=${section}${prime ? `&prime=${prime}` : ''}`);
    renderAccounts();
    await loadOverview();
    if (state.selected) await selectAccount(state.selected.id, false);
  } catch (error) { toast(error.message, true); }
}

async function loadOverview() {
  try {
    const [ready, notReady] = await Promise.all([api('/api/accounts?section=PlayReady'), api('/api/accounts?section=NotPlayReady')]);
    const all = [...ready, ...notReady];
    $('readyCount').textContent = ready.length;
    $('notReadyCount').textContent = notReady.length;
    $('primeCount').textContent = all.filter(a => primeName(a.prime) === 'Prime').length;
    $('nonPrimeCount').textContent = all.filter(a => primeName(a.prime) === 'NonPrime').length;
    $('healthCount').textContent = all.filter(a => a.healthStatus === 'healthy').length;
    $('inMatchCount').textContent = all.filter(a => a.inMatch === true).length;
    $('driftCount').textContent = all.reduce((sum, account) => sum + (account.loadoutMismatchCount || 0), 0);
    const next = all.map(a => a.nextCheckUtc).filter(Boolean).sort()[0];
    $('nextCheckHint').textContent = relativeTime(next);
  } catch {}
}

function renderAccounts() {
  const query = $('accountSearch').value.toLowerCase();
  const list = state.accounts.filter(account =>
    (account.personaName || account.steamId).toLowerCase().includes(query) || account.steamId.includes(query));
  $('accountCount').textContent = `${list.length} Account${list.length === 1 ? '' : 's'}`;
  $('accountList').innerHTML = list.length ? list.map(account => `
    <button class="account-row ${state.selected?.id === account.id ? 'selected' : ''}" data-id="${account.id}">
      <span class="avatar">${avatarMarkup(account)}</span>
      <span class="account-main"><span class="account-name">${esc(account.personaName || 'Unbekannter Account')}</span><span class="account-meta">${esc(account.steamId)} · ${primeName(account.prime)} · ${healthLabel(account.healthStatus)}</span><span class="account-next-cooldown">Nächster Cooldown: ${nextCooldownText(account)}</span></span>
      <span class="status-pill ${sectionName(account.section) === 'PlayReady' ? '' : 'bad'}">${sectionName(account.section) === 'PlayReady' ? 'Ready' : 'Nicht ready'}</span>
    </button>`).join('') : '<div class="empty">Keine Accounts in dieser Kategorie.</div>';
  document.querySelectorAll('.account-row').forEach(row => row.addEventListener('click', () => selectAccount(row.dataset.id)));
}

async function selectAccount(id, refreshList = true) {
  try {
    state.selected = await api(`/api/accounts/${id}`);
    renderDetails();
    if (refreshList) renderAccounts();
  } catch (error) { toast(error.message, true); }
}

function renderDetails() {
  const account = state.selected;
  if (!account) { $('emptyDetails').classList.remove('hidden'); $('accountDetails').classList.add('hidden'); return; }
  $('emptyDetails').classList.add('hidden'); $('accountDetails').classList.remove('hidden');
  $('accountDetails').innerHTML = `
    <div class="details">
      <div class="detail-head"><span class="avatar avatar-large">${avatarMarkup(account, 'large')}</span><div class="detail-identity"><h3>${esc(account.personaName || 'Unbekannter Account')}</h3><span class="muted">${esc(account.steamId)}</span><div class="status-badges"><span class="badge ${sectionName(account.section) === 'PlayReady' ? 'good' : 'bad'}">${sectionName(account.section) === 'PlayReady' ? 'Play Ready' : 'Not Play Ready'}</span><span class="badge">${primeName(account.prime)}</span><span class="badge ${account.inMatch ? 'live' : ''}">${presenceLabel(account)}</span></div></div></div>
      <div class="health-grid"><div class="health-card"><small>GC-Verbindung</small><strong>${healthLabel(account.healthStatus)}</strong><span>${formatDate(account.lastCheckUtc || account.statusUpdatedUtc)}</span></div><div class="health-card"><small>Cooldown</small><strong class="${remainingSeconds(account) > 0 ? 'warning-text' : ''}">${cooldownPhase(account)}</strong><span>${duration(account)}</span></div><div class="health-card"><small>Loadout</small><strong>${account.loadoutMismatchCount ?? '—'}</strong><span>${account.loadoutCheckedUtc ? `geprüft ${relativeTime(account.loadoutCheckedUtc)}` : 'noch nicht geprüft'}</span></div></div>
      <div class="stat-grid"><div class="stat"><small>Premier Rating</small><strong>${account.premierRating ?? '—'}</strong></div><div class="stat"><small>CS2 Level</small><strong>${account.cs2Level ?? '—'}</strong></div></div>
      <div class="detail-lines"><div class="detail-line"><span>VAC</span><strong>${account.vacBanned === true ? 'Ja' : account.vacBanned === false ? 'Nein' : 'Unbekannt'}</strong></div><div class="detail-line"><span>Cooldown-Ende</span><strong>${formatDate(account.cooldownEndsUtc)}</strong></div><div class="detail-line"><span>Nächster Cooldown</span><strong class="next-cooldown-value">${nextCooldownText(account)}</strong></div><div class="detail-line"><span>Nächster GC-Check</span><strong>${formatDate(account.nextCheckUtc)} <span class="muted">(${relativeTime(account.nextCheckUtc)})</span></strong></div><div class="detail-line"><span>Letzter Login</span><strong>${formatDate(account.lastLoginUtc)}</strong></div></div>
      <div class="note-row"><input id="noteInput" value="${esc(account.note || '')}" placeholder="Notiz..."><button id="saveNote">Speichern</button></div>
      <div class="detail-actions"><button data-command="RefreshStatus">Status aktualisieren</button><button data-command="SyncLoadout">Loadout prüfen</button><button data-command="ClearWorkshop">Workshop leeren</button><button data-command="ApplyLoadout">Loadout anwenden</button><button data-command="Personalize">Personalize</button></div>
      ${renderRecentMatches(account)}
      <p id="operationMessage" class="muted"></p>
    </div>`;
  $('saveNote').addEventListener('click', saveNote);
  document.querySelectorAll('[data-command]').forEach(button => button.addEventListener('click', () => runCommand(button.dataset.command)));
}

async function saveNote() {
  try { await api(`/api/accounts/${state.selected.id}/note`, { method: 'PATCH', body: JSON.stringify({ note: $('noteInput').value }) }); state.selected.note = $('noteInput').value; toast('Notiz gespeichert.'); }
  catch (error) { toast(error.message, true); }
}

async function runCommand(command) {
  if (!state.selected) return;
  try {
    let payload = null;
    if (command === 'ApplyLoadout') payload = await api('/api/settings/loadout');
    if (command === 'Personalize') payload = await api('/api/settings/personalize');
    const commandValues = { RefreshStatus: 0, ClearWorkshop: 1, ApplyLoadout: 2, Personalize: 3, SyncLoadout: 5, RefreshMatches: 6 };
    const operation = await api(`/api/accounts/${state.selected.id}/commands`, { method: 'POST', body: JSON.stringify({ command: commandValues[command], payload }) });
    $('operationMessage').textContent = 'Aktion läuft ...';
    let current = operation;
    for (let attempt = 0; attempt < 120 && current.state < 2; attempt++) { await new Promise(resolve => setTimeout(resolve, 1000)); current = await api(`/api/operations/${operation.id}`); }
    $('operationMessage').textContent = current.message || 'Aktion beendet.';
    toast(current.state === 2 ? 'Aktion bestätigt.' : 'Aktion fehlgeschlagen.', current.state !== 2);
    await selectAccount(state.selected.id);
    await loadAccounts();
  } catch (error) { toast(error.message, true); }
}

const slotGroups = [
  { title: 'Pistolen', slots: [2, 3, 4, 5, 6] },
  { title: 'SMG / Shotgun', slots: [8, 9, 10, 11, 12] },
  { title: 'Gewehre / Sniper', slots: [14, 15, 16, 17, 18] }
];
const slotNames = { 2:'Startpistole', 3:'Pistolen-Slot 2', 4:'Pistolen-Slot 3', 5:'Pistolen-Slot 4', 6:'Pistolen-Slot 5', 8:'SMG-Slot 1', 9:'SMG-Slot 2', 10:'SMG-Slot 3', 11:'SMG-Slot 4', 12:'SMG-Slot 5', 14:'Gewehr-Slot 1', 15:'Gewehr-Slot 2', 16:'Gewehr-Slot 3', 17:'Gewehr-Slot 4', 18:'Gewehr-Slot 5' };
const weaponNames = { 1:'Desert Eagle', 2:'Dual Berettas', 3:'Five-SeveN', 4:'Glock-18', 7:'AK-47', 8:'AUG', 9:'AWP', 10:'FAMAS', 11:'G3SG1', 13:'Galil AR', 14:'M249', 16:'M4A1', 17:'MAC-10', 19:'P90', 23:'MP5-SD', 24:'UMP-45', 25:'XM1014', 26:'PP-Bizon', 27:'MAG-7', 28:'Negev', 29:'Sawed-Off', 30:'Tec-9', 32:'P2000', 33:'MP7', 34:'MP9', 35:'Nova', 36:'P250', 38:'SCAR-20', 39:'SG 553', 40:'SSG 08', 60:'M4A1-S', 61:'USP-S', 63:'CZ75-Auto', 64:'R8 Revolver' };
const loadoutOptions = { T: { 2:[4], pistols:[1,2,4,30,36,63,64], mid:[14,17,19,23,24,25,26,28,29,33,35], rifles:[7,9,11,13,39,40] }, CT: { 2:[32,61], pistols:[1,2,3,36,63,64], mid:[14,19,23,24,25,26,27,28,33,34,35], rifles:[8,9,10,16,38,40,60] } };
function optionsFor(team, slot) { return slot === 2 ? loadoutOptions[team][2] : slot <= 6 ? loadoutOptions[team].pistols : slot <= 12 ? loadoutOptions[team].mid : loadoutOptions[team].rifles; }
function renderLoadoutEditor(loadout) {
  state.loadout = loadout;
  $('loadoutEditor').innerHTML = ['T', 'CT'].map(team => `<div class="loadout-team"><div class="team-heading"><span class="team-dot ${team.toLowerCase()}"></span><h4>${team === 'T' ? 'Terrorist' : 'Counter-Terrorist'}</h4></div>${slotGroups.map(group => `<div class="loadout-group"><h5>${group.title}</h5>${group.slots.map(slot => `<label class="slot-row"><span>${slotNames[slot]}</span><select data-team="${team}" data-slot="${slot}">${optionsFor(team, slot).map(definition => `<option value="${definition}" ${Number(loadout[team === 'T' ? 't' : 'ct']?.[slot]) === definition ? 'selected' : ''}>${esc(weaponNames[definition] || `Item ${definition}`)}</option>`).join('')}</select></label>`).join('')}</div>`).join('')}</div>`).join('');
}
async function loadSettings() {
  try {
    const loadout = await api('/api/settings/loadout');
    $('loadoutName').value = loadout.name || 'Default';
    renderLoadoutEditor(loadout);
    const personalize = await api('/api/settings/personalize');
    $('personaName').value = personalize.personaName || ''; $('realName').value = personalize.realName || ''; $('summary').value = personalize.summary || '';
    $('clearAliasHistory').checked = !!personalize.clearAliasHistory; state.avatarBase64 = personalize.avatarBase64 || null;
  } catch (error) { toast(error.message, true); }
}
async function saveLoadout() {
  try {
    const t = {}, ct = {};
    document.querySelectorAll('#loadoutEditor select').forEach(select => (select.dataset.team === 'T' ? t : ct)[select.dataset.slot] = Number(select.value));
    await api('/api/settings/loadout', { method: 'PUT', body: JSON.stringify({ name: $('loadoutName').value || 'Default', t, ct }) });
    toast('Loadout gespeichert.');
  } catch (error) { toast(error.message, true); }
}
async function savePersonalize() {
  try { await api('/api/settings/personalize', { method: 'PUT', body: JSON.stringify({ personaName: $('personaName').value || null, realName: $('realName').value || null, summary: $('summary').value || null, avatarBase64: state.avatarBase64, clearAliasHistory: $('clearAliasHistory').checked }) }); toast('Personalize gespeichert.'); }
  catch (error) { toast(error.message, true); }
}

async function startAuth() {
  setButtonBusy('steamSubmitButton', true, 'Steam-Login läuft ...');
  $('authProgress').classList.remove('hidden'); setAuthStage('Steam-Verbindung wird aufgebaut', 'Die CM-Verbindung wird vorbereitet ...', 22);
  try { const accountName = $('steamUsername').value, password = $('steamPassword').value; $('steamPassword').value = ''; const session = await api('/api/auth/sessions', { method: 'POST', body: JSON.stringify({ accountName, password }) }); state.authSession = session.sessionId; setAuthStage('Steam wartet auf Antwort', session.promptMessage || 'Authentifizierung läuft serverseitig ...', 46); pollAuth(); }
  catch (error) { setAuthStage('Login konnte nicht gestartet werden', error.message, 100); setButtonBusy('steamSubmitButton', false, 'Erneut versuchen'); toast(error.message, true); }
}
async function pollAuth() {
  if (!state.authSession) return;
  try {
    const session = await api(`/api/auth/sessions/${state.authSession}`); $('authMessage').textContent = session.promptMessage || 'Steam-Authentifizierung läuft ...';
    const waiting = session.state === 0 || session.state === 1; $('guardLabel').classList.toggle('hidden', !waiting); $('guardSubmit').classList.toggle('hidden', !waiting);
    if (waiting) { setAuthStage(session.state === 1 ? 'Bestätigung erforderlich' : 'Steam Guard wird erwartet', session.promptMessage || 'Bitte den Code eingeben.', session.state === 1 ? 70 : 58); }
    if (session.state === 2) { setAuthStage('Account erfolgreich verbunden', 'Status und GC-Daten werden gespeichert ...', 100); toast('Steam-Account hinzugefügt.'); setTimeout(() => { $('addAccountDialog').close(); state.authSession = null; setButtonBusy('steamSubmitButton', false, 'Steam-Login starten'); loadAccounts(); }, 700); return; }
    if (session.state === 3) { setAuthStage('Steam-Login fehlgeschlagen', session.promptMessage || 'Bitte die Zugangsdaten prüfen.', 100); setButtonBusy('steamSubmitButton', false, 'Erneut versuchen'); return; }
    setTimeout(pollAuth, 1200);
  } catch (error) { $('authMessage').textContent = error.message; }
}
async function submitGuard() { if (!state.authSession) return; try { await api(`/api/auth/sessions/${state.authSession}/code`, { method: 'POST', body: JSON.stringify({ code: $('guardCode').value }) }); $('guardCode').value = ''; $('authMessage').textContent = 'Code übermittelt ...'; } catch (error) { toast(error.message, true); } }
async function checkSession() { try { const me = await api('/api/web/me'); showApp(me.username); await loadSettings(); await loadAccounts(); } catch { showLogin(); } }

$('loginForm').addEventListener('submit', async event => { event.preventDefault(); setButtonBusy('dashboardLoginButton', true, 'Anmeldung läuft ...'); $('loginError').textContent = ''; try { const me = await api('/api/web/login', { method: 'POST', body: JSON.stringify({ username: $('loginUsername').value, password: $('loginPassword').value }) }); $('loginPassword').value = ''; showApp(me.username); await loadSettings(); await loadAccounts(); } catch (error) { $('loginError').textContent = error.message; setButtonBusy('dashboardLoginButton', false, 'Dashboard öffnen'); } });
document.querySelectorAll('.nav-item').forEach(item => item.addEventListener('click', () => setView(item.dataset.view)));
$('refreshButton').addEventListener('click', () => state.view === 'play' || state.view === 'notplay' ? loadAccounts() : loadSettings());
$('primeFilter').addEventListener('change', loadAccounts); $('accountSearch').addEventListener('input', renderAccounts);
$('logoutButton').addEventListener('click', async () => { await api('/api/web/logout', { method: 'POST' }); showLogin(); });
$('addAccountButton').addEventListener('click', () => { $('addAccountForm').reset(); $('authProgress').classList.add('hidden'); $('guardLabel').classList.add('hidden'); $('guardSubmit').classList.add('hidden'); setAuthStage('Verbindung wird vorbereitet', 'Bitte einen Moment warten ...', 25); setButtonBusy('steamSubmitButton', false, 'Steam-Login starten'); $('addAccountDialog').showModal(); });
$('closeDialog').addEventListener('click', () => $('addAccountDialog').close()); $('addAccountForm').addEventListener('submit', event => { event.preventDefault(); startAuth(); }); $('guardSubmit').addEventListener('click', submitGuard);
$('saveLoadout').addEventListener('click', saveLoadout); $('savePersonalize').addEventListener('click', savePersonalize);
$('avatarFile').addEventListener('change', event => { const file = event.target.files[0]; if (!file) { state.avatarBase64 = null; return; } const reader = new FileReader(); reader.onload = () => state.avatarBase64 = String(reader.result).split(',')[1] || null; reader.readAsDataURL(file); });
checkSession();
