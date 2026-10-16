import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const backendSource = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');
const notifierSource = await readFile(new URL('../src/Backend/DiscordWebhookNotifier.cs', import.meta.url), 'utf8');
const contractsSource = await readFile(new URL('../src/Shared/Contracts.cs', import.meta.url), 'utf8');
const appSource = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');

test('exposes a persisted reauthentication state for every account', () => {
  expect(backendSource).toContain('public bool LoginRequiresReauthentication { get; set; }');
  expect(backendSource).toContain('public bool ReauthenticationNotificationSent { get; set; }');
  expect(contractsSource).toContain('public bool LoginRequiresReauthentication { get; set; }');
  expect(backendSource).toContain('LoginRequiresReauthentication = account.LoginRequiresReauthentication');
  expect(appSource).toContain("value === 'login-required'");
  expect(appSource).toContain('Steam-Login erneut erforderlich');
});

test('only token failures trigger the reauthentication alert', () => {
  expect(backendSource).toContain('cmException.IsTokenFailure');
  expect(backendSource).toContain('MarkLoginRequiresReauthenticationAsync');
  expect(backendSource).toContain('SteamLoginExpired');
  expect(backendSource).toContain('LoginRequiresReauthentication = false');
});

test('reauthentication notification explicitly allows one everyone mention', () => {
  expect(notifierSource).toContain('SendReauthenticationRequiredAsync');
  expect(notifierSource).toContain('content = mentionEveryone ? "@everyone" : null');
  expect(notifierSource).toContain('mentionEveryone ? new[] { "everyone" } : Array.Empty<string>()');
  expect(backendSource).toContain('ReauthenticationNotificationSent');
});
