const $ = (id) => document.getElementById(id);
const state = { view: 'control', accounts: [], allAccounts: [], selected: null, authSession: null, avatarBase64: null, loadout: null, timeline: [], focusMode: false, mail: { messages: [], groups: [], selected: null, configured: false, activeGroup: 'primary', requestId: 0 } };

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
function nextCooldownText(account) { const seconds = account.nextCooldownSeconds; if (!seconds) return ''; if (seconds === 20 * 60 * 60) return '20 Stunden'; if (seconds === 7 * 86400) return '7 Tage'; if (seconds === 31 * 86400) return '31 Tage'; if (seconds === 90 * 86400) return '90 Tage'; return '181 Tage'; }
function matchResult(match) { return match.teamScore != null && match.opponentScore != null ? `${match.teamScore} : ${match.opponentScore}` : '—'; }
function matchOutcome(match) { return match.outcome === 'win' ? ['W', 'match-win'] : match.outcome === 'loss' ? ['L', 'match-loss'] : match.outcome === 'draw' ? ['T', 'match-draw'] : ['—', '']; }
function renderRatingTrend(account) {
  const points = (account.ratingHistory || []).filter(point => Number.isFinite(point.rating)).slice(-90);
  if (!points.length) return '<section class="rating-panel"><div class="rating-empty">Noch keine Premier-Historie. Der Trend baut sich mit den nächsten GC-Checks auf.</div></section>';
  const width = 760, height = 190, padX = 32, padY = 22;
  const values = points.map(point => point.rating), min = Math.min(...values), max = Math.max(...values), span = Math.max(500, max - min);
  const coords = points.map((point, index) => { const x = points.length === 1 ? width / 2 : padX + index * ((width - padX * 2) / (points.length - 1)); const y = height - padY - ((point.rating - (min - span * .08)) / (span * 1.16)) * (height - padY * 2); return { x, y, point }; });
  const polyline = coords.map(coord => `${coord.x.toFixed(1)},${coord.y.toFixed(1)}`).join(' ');
  const latest = points[points.length - 1];
  return `<section class="rating-panel"><div class="rating-heading"><div><span class="eyebrow">PREMIER TREND</span><h4>Rating-Verlauf</h4><small>${points.length} Messpunkte · zuletzt ${esc(relativeTime(latest.capturedUtc))}</small></div><strong>${esc(latest.rating.toLocaleString('de-DE'))}</strong></div><div class="rating-chart"><span class="chart-label chart-high">${esc(max.toLocaleString('de-DE'))}</span><span class="chart-label chart-low">${esc(min.toLocaleString('de-DE'))}</span><svg viewBox="0 0 ${width} ${height}" role="img" aria-label="Premier Rating Verlauf"><line x1="${padX}" y1="${padY}" x2="${width - padX}" y2="${padY}"/><line x1="${padX}" y1="${height / 2}" x2="${width - padX}" y2="${height / 2}"/><line x1="${padX}" y1="${height - padY}" x2="${width - padX}" y2="${height - padY}"/><polyline points="${polyline}"/></svg></div></section>`;
}
function renderRecentMatches(account) { const matches = account.recentMatches || []; const content = matches.length ? matches.map(match => { const [outcome, outcomeClass] = matchOutcome(match); return `<div class="match-row"><span class="match-map">${esc(match.map || 'CS2 Match')}</span><span class="match-outcome ${outcomeClass}">${outcome}</span><span class="match-score">${esc(matchResult(match))}</span><span class="match-mode">${esc(match.mode || 'Premier')}</span><span class="match-stat">${match.kills ?? '—'} / ${match.assists ?? '—'} / ${match.deaths ?? '—'}</span><span class="match-date">${esc(formatDate(match.playedUtc))}</span></div>`; }).join('') : `<div class="matches-empty">${account.matchesStatus === 'ok' ? 'Keine aktuellen Spiele gefunden.' : 'Noch keine Matchdaten geladen.'}</div>`; return `<section class="matches-panel"><div class="matches-heading"><div><h4>Letzte Spiele</h4><small>${account.matchesCheckedUtc ? `Zuletzt geladen ${relativeTime(account.matchesCheckedUtc)}` : 'Abruf über den CS2 Game Coordinator'}</small></div><button class="secondary" data-command="RefreshMatches">${matches.length ? 'Aktualisieren' : 'Laden'}</button></div><div class="match-table-head"><span>Map</span><span>Ergebnis</span><span>Score</span><span>Modus</span><span>K / A / D</span><span>Datum</span></div><div class="matches-list">${content}</div></section>`; }
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
function accountAttention(account) {
  if (account.vacBanned === true) return ['critical', 'VAC-Status', 'Sofort prüfen'];
  if (remainingSeconds(account) > 0) return ['warning', 'Cooldown aktiv', duration(account)];
  if (account.healthStatus && account.healthStatus !== 'healthy') return ['warning', healthLabel(account.healthStatus), 'GC-Check wiederholen'];
  if (account.loadoutMismatchCount > 0) return ['info', 'Loadout-Diff', `${account.loadoutMismatchCount} Abweichungen`];
  if (account.inMatch === true || account.monitoringPaused) return ['info', 'Spielmodus aktiv', 'Checks pausiert'];
  return null;
}
function signalTitle(item) { return item?.title || 'Ereignis'; }
function mailSenderLabel(sender) { return sender?.replace(/^.*</, '').replace(/>$/, '') || 'Steam'; }
function showLogin() { $('appView').classList.add('hidden'); $('loginView').classList.remove('hidden'); setButtonBusy('dashboardLoginButton', false, 'Dashboard öffnen'); }
function showApp(username) { $('loginView').classList.add('hidden'); $('appView').classList.remove('hidden'); $('userLabel').textContent = username || ''; }
function toast(message, bad = false) { const el = $('toast'); el.textContent = message; el.classList.remove('hidden'); el.style.background = bad ? '#a52318' : '#17324d'; setTimeout(() => el.classList.add('hidden'), 4500); }
function setButtonBusy(id, busy, label) { const button = $(id); if (!button) return; button.disabled = busy; button.querySelector('.button-spinner')?.classList.toggle('hidden', !busy); const text = button.querySelector('.button-text'); if (text && label) text.textContent = label; }
function setAuthStage(title, message, progress = 25) { if ($('authStage')) $('authStage').textContent = title; if ($('authMessage')) $('authMessage').textContent = message; if ($('authProgressBar')) $('authProgressBar').style.width = `${progress}%`; }

function setView(view) {
  state.view = view;
  document.querySelectorAll('.nav-item').forEach(item => item.classList.toggle('active', item.dataset.view === view));
  const settings = view === 'loadout' || view === 'personalize';
  const mail = view === 'mail';
  const control = view === 'control';
  $('accountsView').classList.toggle('hidden', settings || control || mail);
  $('settingsView').classList.toggle('hidden', !settings);
  $('controlView').classList.toggle('hidden', !control);
  $('mailView').classList.toggle('hidden', !mail);
  $('overview').classList.toggle('hidden', settings || mail);
  $('signalBar').classList.toggle('hidden', settings || mail);
  $('loadoutSettings').classList.toggle('hidden', view !== 'loadout');
  $('personalizeSettings').classList.toggle('hidden', view !== 'personalize');
  const titles = { control: ['Mission Control', 'Alle Accounts. Alle Signale. Ein klarer nächster Schritt.'], play: ['Play Ready', 'Accounts ohne aktiven Cooldown oder VAC'], notplay: ['Not Play Ready', 'Accounts mit Cooldown, VAC oder unbekanntem Status'], mail: ['Steam Mail', 'Globaler Steam-Posteingang ohne Account-Zuordnung'], loadout: ['Loadout', 'Serverseitige Presets und Synchronisierung'], personalize: ['Personalize', 'Profil-Presets und Account-Timeline'] };
  $('pageTitle').textContent = titles[view][0]; $('pageSubtitle').textContent = titles[view][1];
  const animatedView = control ? $('controlView') : mail ? $('mailView') : settings ? $('settingsView') : $('accountsView'); animatedView.classList.remove('view-enter'); requestAnimationFrame(() => animatedView.classList.add('view-enter'));
  if (control) loadControl(); else if (mail) loadMail(); else if (settings) loadSettings(); else loadAccounts();
}

async function loadControl() {
  try {
    const [ready, notReady, timeline] = await Promise.all([api('/api/accounts?section=PlayReady'), api('/api/accounts?section=NotPlayReady'), api('/api/timeline?limit=12')]);
    state.allAccounts = [...ready, ...notReady]; state.timeline = timeline; renderControl(); await loadOverview();
  } catch (error) { toast(error.message, true); }
}

function renderControl() {
  const attention = state.allAccounts.filter(account => accountAttention(account));
  const visible = state.focusMode ? attention : attention.slice(0, 8);
  $('attentionCount').textContent = `${attention.length} offen`;
  $('focusButton').textContent = state.focusMode ? 'Fleet anzeigen' : 'Nur Aufmerksamkeit';
  $('attentionList').innerHTML = visible.length ? visible.map(account => { const item = accountAttention(account); return `<button class="attention-row ${item[0]}" data-account-id="${account.id}"><span class="attention-icon">${item[0] === 'critical' ? '!' : item[0] === 'warning' ? '◷' : '·'}</span><span class="attention-main"><strong>${esc(account.personaName || account.steamId)}</strong><small>${esc(item[1])} · ${esc(item[2])}</small></span><span class="attention-arrow">→</span></button>`; }).join('') : '<div class="control-empty"><strong>Fleet ist ruhig</strong><span>Aktuell braucht kein Account deine Aufmerksamkeit.</span></div>';
  $('controlSignals').innerHTML = state.timeline.length ? state.timeline.slice(0, 8).map(item => `<button class="control-signal ${esc(item.severity || 'info')}" data-account-id="${item.accountId}"><span class="timeline-icon">${timelineIcon(item.type)}</span><span><strong>${esc(signalTitle(item))}</strong><small>${esc(item.personaName || item.steamId)} · ${esc(relativeTime(item.occurredUtc))}</small></span><span class="control-signal-arrow">→</span></button>`).join('') : '<div class="control-empty"><strong>Noch keine Signale</strong><span>Scheduler-Checks und Aktionen erscheinen hier.</span></div>';
  document.querySelectorAll('[data-account-id]').forEach(button => button.addEventListener('click', () => { setView('play'); selectAccount(button.dataset.accountId); }));
}

async function loadAccounts() {
  const section = state.view === 'notplay' ? 'NotPlayReady' : 'PlayReady';
  const prime = $('primeFilter').value;
  try {
    state.accounts = await api(`/api/accounts?section=${section}${prime ? `&prime=${prime}` : ''}`);
    state.allAccounts = state.accounts;
    renderAccounts();
    await loadOverview();
    if (state.selected) await selectAccount(state.selected.id, false);
  } catch (error) { toast(error.message, true); }
}

async function loadOverview() {
  try {
    const ready = state.allAccounts.filter(account => sectionName(account.section) === 'PlayReady');
    const notReady = state.allAccounts.filter(account => sectionName(account.section) !== 'PlayReady');
    const all = state.allAccounts;
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
      <span class="account-main"><span class="account-name">${esc(account.personaName || 'Unbekannter Account')} ${account.isPermaGreen ? '<span class="perma-badge">PERMA-GREEN</span>' : ''}</span><span class="account-meta">${esc(account.steamId)} · ${primeName(account.prime)} · ${healthLabel(account.healthStatus)}</span>${account.nextCooldownSeconds ? `<span class="account-next-cooldown">Nächster Cooldown: ${nextCooldownText(account)}</span>` : ''}</span>
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
      <div class="detail-head"><span class="avatar avatar-large">${avatarMarkup(account, 'large')}</span><div class="detail-identity"><h3>${esc(account.personaName || 'Unbekannter Account')}</h3><span class="muted">${esc(account.steamId)}</span><div class="status-badges"><span class="badge ${sectionName(account.section) === 'PlayReady' ? 'good' : 'bad'}">${sectionName(account.section) === 'PlayReady' ? 'Play Ready' : 'Not Play Ready'}</span><span class="badge">${primeName(account.prime)}</span><span class="badge ${account.inMatch ? 'live' : ''}">${presenceLabel(account)}</span>${account.publicPlayingCs2 ? '<span class="badge live">CS2 erkannt</span>' : ''}${account.monitoringPaused ? '<span class="badge perma-badge">CHECKS PAUSIERT</span>' : ''}${account.isPermaGreen ? '<span class="badge perma-badge">PERMA-GREEN</span>' : ''}</div></div></div>
      <div class="health-grid"><div class="health-card"><small>GC-Verbindung</small><strong>${healthLabel(account.healthStatus)}</strong><span>${formatDate(account.lastCheckUtc || account.statusUpdatedUtc)}</span></div><div class="health-card"><small>Cooldown</small><strong class="${remainingSeconds(account) > 0 ? 'warning-text' : ''}">${cooldownPhase(account)}</strong><span>${duration(account)}</span></div><div class="health-card"><small>Loadout</small><strong>${account.loadoutMismatchCount ?? '—'}</strong><span>${account.loadoutCheckedUtc ? `geprüft ${relativeTime(account.loadoutCheckedUtc)}` : 'noch nicht geprüft'}</span></div></div>
      <div class="stat-grid"><div class="stat"><small>Premier Rating</small><strong>${account.premierRating ?? '—'}</strong></div><div class="stat"><small>CS2 Level</small><strong>${account.cs2Level ?? '—'}</strong></div></div>
      ${renderRatingTrend(account)}
      <div class="detail-lines"><div class="detail-line"><span>VAC</span><strong>${account.vacBanned === true ? 'Ja' : account.vacBanned === false ? 'Nein' : 'Unbekannt'}</strong></div><div class="detail-line"><span>Cooldown-Ende</span><strong>${formatDate(account.cooldownEndsUtc)}</strong></div>${account.nextCooldownSeconds ? `<div class="detail-line"><span>Nächster Cooldown</span><strong class="next-cooldown-value">${nextCooldownText(account)}</strong></div>` : ''}<div class="detail-line"><span>Nächster GC-Check</span><strong>${formatDate(account.nextCheckUtc)} <span class="muted">(${relativeTime(account.nextCheckUtc)})</span></strong></div><div class="detail-line"><span>Letzter Login</span><strong>${formatDate(account.lastLoginUtc)}</strong></div></div>
      <div class="note-row"><input id="noteInput" value="${esc(account.note || '')}" placeholder="Notiz..."><button id="saveNote">Speichern</button></div>
      <div class="detail-actions"><button data-command="RefreshStatus">Status aktualisieren</button><button data-command="SyncLoadout">Loadout prüfen</button><button data-command="ClearWorkshop">Workshop leeren</button><button data-command="ApplyLoadout">Loadout anwenden</button><button data-command="Personalize">Personalize</button><button id="permaGreenButton" class="perma-action">${account.isPermaGreen ? 'Perma-Green entfernen' : 'Als Perma-Green markieren'}</button><button id="monitoringButton" class="monitoring-action">${account.monitoringPaused ? 'Checks fortsetzen' : 'Spielmodus aktivieren'}</button></div>
      ${renderRecentMatches(account)}
      <p id="operationMessage" class="muted"></p>
    </div>`;
  $('saveNote').addEventListener('click', saveNote);
  $('permaGreenButton').addEventListener('click', togglePermaGreen);
  $('monitoringButton').addEventListener('click', toggleMonitoring);
  document.querySelectorAll('[data-command]').forEach(button => button.addEventListener('click', () => runCommand(button.dataset.command)));
}

async function toggleMonitoring() {
  if (!state.selected) return;
  try {
    const paused = !state.selected.monitoringPaused;
    await api(`/api/accounts/${state.selected.id}/monitoring`, { method: 'PATCH', body: JSON.stringify({ paused }) });
    toast(paused ? 'Spielmodus aktiv: automatische Checks pausiert.' : 'Automatische Checks fortgesetzt.');
    await selectAccount(state.selected.id);
    await loadAccounts();
  } catch (error) { toast(error.message, true); }
}

async function togglePermaGreen() {
  if (!state.selected) return;
  try {
    await api(`/api/accounts/${state.selected.id}/perma-green`, { method: 'PATCH', body: JSON.stringify({ enabled: !state.selected.isPermaGreen }) });
    toast(state.selected.isPermaGreen ? 'Perma-Green entfernt.' : 'Perma-Green aktiviert.');
    await selectAccount(state.selected.id);
    await loadAccounts();
  } catch (error) { toast(error.message, true); }
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
    if (state.view === 'personalize') await loadTimeline();
  } catch (error) { toast(error.message, true); }
}

function timelineIcon(type) { return ({ status: '↗', cooldown: '◷', action: '✦', matches: '▦', account: '+', 'perma-green': '★', error: '!' }[type] || '•'); }
function timelineLabel(type) { return ({ status: 'Status', cooldown: 'Cooldown', action: 'Aktion', matches: 'Matches', account: 'Account', 'perma-green': 'Perma-Green', error: 'Fehler' }[type] || 'Ereignis'); }
function renderTimeline() {
  const items = state.timeline || [];
  const counts = items.reduce((result, item) => { result[item.type] = (result[item.type] || 0) + 1; return result; }, {});
  $('timelineSummary').innerHTML = [['status', 'Checks'], ['cooldown', 'Cooldowns'], ['action', 'Aktionen'], ['error', 'Fehler']].map(([type, label]) => `<div class="timeline-stat"><span class="timeline-stat-bar ${type}" style="--bar:${Math.min(100, (counts[type] || 0) * 12)}%"></span><strong>${counts[type] || 0}</strong><small>${label}</small></div>`).join('');
  $('timelineList').innerHTML = items.length ? items.map(item => `<button class="timeline-event ${esc(item.severity || 'info')}" data-account-id="${item.accountId}"><span class="timeline-icon">${timelineIcon(item.type)}</span><span class="timeline-event-main"><strong>${esc(item.title)}</strong><span>${esc(item.personaName || item.steamId)}${item.isPermaGreen ? ' · Perma-Green' : ''}</span><small>${esc(item.detail || '')}</small></span><span class="timeline-event-side"><b>${timelineLabel(item.type)}</b><time>${esc(relativeTime(item.occurredUtc))}</time></span></button>`).join('') : '<div class="timeline-empty">Noch keine Ereignisse vorhanden. Sobald der Scheduler oder eine Account-Aktion läuft, erscheint hier die Historie.</div>';
  document.querySelectorAll('.timeline-event').forEach(item => item.addEventListener('click', () => { setView('play'); selectAccount(item.dataset.accountId); }));
}
async function loadTimeline() {
  try { state.timeline = await api('/api/timeline?limit=180'); renderTimeline(); }
  catch (error) { toast(error.message, true); }
}

async function loadMail() {
  const requestId = ++state.mail.requestId;
  try {
    const query = encodeURIComponent($('mailSearch').value.trim());
    const unread = $('mailUnread').checked ? '&unread=true' : '';
    const group = encodeURIComponent(state.mail.activeGroup || 'primary');
    const result = await api(`/api/mail?limit=100&group=${group}${query ? `&query=${query}` : ''}${unread}`);
    if (requestId !== state.mail.requestId) return;
    state.mail = { ...state.mail, ...result };
    renderMail();
  } catch (error) { toast(error.message, true); }
}

function renderMail() {
  const result = state.mail;
  const messages = result.messages || [];
  const groups = result.groups?.length ? result.groups : [
    { id: 'primary', label: 'Steam', total: messages.length, unread: messages.filter(message => !message.isRead).length, enabled: true },
    { id: 'secondary', label: 'Zweite Domain', total: 0, unread: 0, enabled: false },
  ];
  const activeGroup = groups.find(group => group.id === state.mail.activeGroup) || groups[0];
  const unreadCount = messages.filter(message => !message.isRead).length;
  $('mailUnreadBadge').textContent = unreadCount;
  $('mailUnreadBadge').classList.toggle('hidden', unreadCount === 0);
  $('mailGroupHeading').textContent = activeGroup.label;
  $('mailCount').textContent = `${messages.length} Nachricht${messages.length === 1 ? '' : 'en'}`;
  $('mailLastSync').textContent = result.lastSyncUtc ? `Zuletzt ${relativeTime(result.lastSyncUtc)}` : 'Noch nicht synchronisiert';
  const notice = $('mailNotice');
  const noticeText = !result.configured ? 'IMAP ist noch nicht konfiguriert. Die App läuft normal weiter; setze die Mailbox-Variablen auf dem Server, um Steam-Mails zu laden.' : result.lastSyncError ? result.lastSyncError : '';
  notice.textContent = noticeText; notice.classList.toggle('hidden', !noticeText); notice.classList.toggle('mail-notice-error', !!result.lastSyncError);
  document.querySelectorAll('[data-mail-group]').forEach(button => {
    const group = groups.find(item => item.id === button.dataset.mailGroup);
    const count = group?.unread || 0;
    button.classList.toggle('active', button.dataset.mailGroup === activeGroup.id);
    button.setAttribute('aria-selected', button.dataset.mailGroup === activeGroup.id ? 'true' : 'false');
    button.querySelector('.mail-group-count').textContent = count;
    button.title = group?.enabled ? `${group.total} Nachrichten` : 'Noch keine Domain konfiguriert';
  });
  const emptyText = activeGroup.enabled ? 'Keine Nachrichten in diesem Bereich.' : 'Zweite Domain noch nicht konfiguriert.';
  $('mailList').innerHTML = messages.length ? messages.map(message => `<button type="button" class="mail-row ${message.isRead ? '' : 'unread'} ${state.mail.selected?.id === message.id ? 'selected' : ''}" role="listitem" data-mail-id="${message.id}"><span class="mail-status" aria-label="${message.isRead ? 'Gelesen' : 'Ungelesen'}"></span><span class="mail-row-main"><strong>${esc(message.subject)}</strong><small>${esc(mailSenderLabel(message.sender))}</small><span>${esc(message.preview || 'Keine Textvorschau verfügbar.')}</span></span><time>${esc(relativeTime(message.receivedUtc))}</time></button>`).join('') : `<div class="mail-list-empty">${esc(emptyText)}</div>`;
  if (state.mail.selected) renderMailDetail(state.mail.selected);
}

async function selectMail(id) {
  try {
    const detail = await api(`/api/mail/${id}`);
    state.mail.selected = detail;
    if (!detail.isRead) {
      await api(`/api/mail/${id}/read`, { method: 'PATCH', body: JSON.stringify({ isRead: true }) });
      detail.isRead = true;
    }
    renderMail();
    renderMailDetail(detail);
  } catch (error) { toast(error.message, true); }
}

function renderMailDetail(message) {
  if (!message) { $('mailEmpty').classList.remove('hidden'); $('mailDetail').classList.add('hidden'); return; }
  $('mailEmpty').classList.add('hidden'); $('mailDetail').classList.remove('hidden');
  $('mailDetail').innerHTML = `<div class="mail-detail-head"><div><span class="eyebrow">STEAM MESSAGE</span><h3>${esc(message.subject)}</h3><p class="muted">${esc(mailSenderLabel(message.sender))} · ${esc(formatDate(message.receivedUtc))}</p></div><span class="badge good">Gelesen</span></div><div class="mail-body">${esc(message.body || 'Keine Textversion dieser Nachricht verfügbar.')}</div>`;
}

async function syncMail() {
  setButtonBusy('syncMailButton', true, 'Synchronisiere ...');
  try {
    await api('/api/mail/sync', { method: 'POST' });
    await loadMail();
    toast('Mail-Postfach synchronisiert.');
  } catch (error) {
    await loadMail();
    toast(state.mail.lastSyncError || error.message, true);
  }
  finally { setButtonBusy('syncMailButton', false, 'Synchronisieren'); }
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
async function checkSession() { try { const me = await api('/api/web/me'); showApp(me.username); await loadSettings(); await loadControl(); } catch { showLogin(); } }

$('loginForm').addEventListener('submit', async event => { event.preventDefault(); setButtonBusy('dashboardLoginButton', true, 'Anmeldung läuft ...'); $('loginError').textContent = ''; try { const me = await api('/api/web/login', { method: 'POST', body: JSON.stringify({ username: $('loginUsername').value, password: $('loginPassword').value }) }); $('loginPassword').value = ''; showApp(me.username); await loadSettings(); await loadControl(); } catch (error) { $('loginError').textContent = error.message; setButtonBusy('dashboardLoginButton', false, 'Dashboard öffnen'); } });
document.querySelectorAll('.nav-item').forEach(item => item.addEventListener('click', () => setView(item.dataset.view)));
$('refreshButton').addEventListener('click', () => state.view === 'play' || state.view === 'notplay' ? loadAccounts() : state.view === 'control' ? loadControl() : state.view === 'mail' ? loadMail() : loadSettings());
$('primeFilter').addEventListener('change', loadAccounts); $('accountSearch').addEventListener('input', renderAccounts);
$('focusButton').addEventListener('click', () => { state.focusMode = !state.focusMode; renderControl(); });
$('refreshAllButton').addEventListener('click', loadControl);
$('openTimelineButton').addEventListener('click', () => setView('personalize'));
$('mailSearch').addEventListener('input', () => { clearTimeout(state.mail.searchTimer); state.mail.searchTimer = setTimeout(loadMail, 250); });
$('mailUnread').addEventListener('change', loadMail); $('syncMailButton').addEventListener('click', syncMail);
$('mailList').addEventListener('click', event => { const row = event.target.closest('[data-mail-id]'); if (row) selectMail(row.dataset.mailId); });
document.querySelectorAll('[data-mail-group]').forEach(button => button.addEventListener('click', () => { if (state.mail.activeGroup === button.dataset.mailGroup) return; state.mail.activeGroup = button.dataset.mailGroup; state.mail.selected = null; loadMail(); }));
$('logoutButton').addEventListener('click', async () => { await api('/api/web/logout', { method: 'POST' }); showLogin(); });
$('addAccountButton').addEventListener('click', () => { $('addAccountForm').reset(); $('authProgress').classList.add('hidden'); $('guardLabel').classList.add('hidden'); $('guardSubmit').classList.add('hidden'); setAuthStage('Verbindung wird vorbereitet', 'Bitte einen Moment warten ...', 25); setButtonBusy('steamSubmitButton', false, 'Steam-Login starten'); $('addAccountDialog').showModal(); });
$('closeDialog').addEventListener('click', () => $('addAccountDialog').close()); $('addAccountForm').addEventListener('submit', event => { event.preventDefault(); startAuth(); }); $('guardSubmit').addEventListener('click', submitGuard);
$('saveLoadout').addEventListener('click', saveLoadout); $('savePersonalize').addEventListener('click', savePersonalize);
$('refreshTimelineButton').addEventListener('click', loadTimeline);
$('avatarFile').addEventListener('change', event => { const file = event.target.files[0]; if (!file) { state.avatarBase64 = null; return; } const reader = new FileReader(); reader.onload = () => state.avatarBase64 = String(reader.result).split(',')[1] || null; reader.readAsDataURL(file); });
checkSession();
