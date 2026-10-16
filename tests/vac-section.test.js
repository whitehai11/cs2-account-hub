import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const contracts = await readFile(new URL('../src/Shared/Contracts.cs', import.meta.url), 'utf8');
const program = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');
const app = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');
const html = await readFile(new URL('../src/Backend/wwwroot/index.html', import.meta.url), 'utf8');
const css = await readFile(new URL('../src/Backend/wwwroot/styles.css', import.meta.url), 'utf8');

test('stores VAC-banned accounts in a dedicated backend section', () => {
  expect(contracts).toContain('VacBanned = 2');
  expect(program).toContain('HasVacBan');
  expect(program).toContain('AccountSection.VacBanned');
  expect(program).toContain('SteamVacBanned is true');
});

test('calculates overview counters from the complete account fleet', () => {
  expect(app).toContain("api('/api/accounts')");
  expect(app).toContain("sectionName(account.section) === 'NotPlayReady'");
  expect(app).toContain("state.view === 'vac'");
});

test('exposes and clearly renders the dedicated VAC section', () => {
  expect(html).toContain('data-view="vac"');
  expect(app).toContain('hasVacBan(account)');
  expect(app).toContain('VAC-Ban');
  expect(css).toContain('.status-pill.vac');
  expect(css).toContain('.account-row.vac-banned');
});
