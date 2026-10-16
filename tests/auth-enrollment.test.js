import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const backendSource = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');
const contractsSource = await readFile(new URL('../src/Shared/Contracts.cs', import.meta.url), 'utf8');
const appSource = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');
const runStart = backendSource.indexOf('private async Task RunAsync(AuthSessionRecord session');
const runEnd = backendSource.indexOf('\n    private async Task<string?> WaitForGuardAsync', runStart);
const runSource = runStart >= 0 && runEnd > runStart
  ? backendSource.slice(runStart, runEnd)
  : null;
if (!runSource) throw new Error('AuthSessionStore.RunAsync not found');

test('persists an authenticated account before optional status enrichment', () => {
  expect(runSource.indexOf('accounts.Upsert(account);')).toBeGreaterThan(-1);
  expect(runSource.indexOf('accounts.Upsert(account);')).toBeLessThan(runSource.indexOf('QueryStatusAsync'));
});

test('exposes the Steam login name alongside the display name', () => {
  expect(contractsSource).toContain('public string? AccountName');
  expect(backendSource).toContain('account.AccountName');
  expect(appSource).toContain('account.accountName');
});

test('refreshes credentials in place for an existing account', () => {
  expect(contractsSource).toContain('Guid? ExistingAccountId = null');
  expect(backendSource).toContain('request.ExistingAccountId');
  expect(runSource).toContain('session.ExistingAccountId');
  expect(runSource).toContain('The Steam login belongs to a different account.');
  expect(appSource).toContain('refreshLoginButton');
  expect(appSource).toContain('existingAccountId: state.authAccountId');
});
