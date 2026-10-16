import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const backendSource = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');
const storeSource = await readFile(new URL('../src/Backend/SteamAuthenticatorStore.cs', import.meta.url), 'utf8');
const appSource = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');
const htmlSource = await readFile(new URL('../src/Backend/wwwroot/index.html', import.meta.url), 'utf8');
const noticesSource = await readFile(new URL('../THIRD_PARTY_NOTICES.md', import.meta.url), 'utf8');

test('MAFile secrets are encrypted at rest and never exposed by the list DTO', () => {
  expect(storeSource).toContain('SecretProtector');
  expect(storeSource).toContain('ProtectedSharedSecret');
  expect(storeSource).toContain('new SteamAuthenticatorSummaryDto');
  expect(storeSource).not.toContain('SharedSecret,');
  expect(backendSource).toContain('app.MapPost("/api/authenticators/import"');
  expect(backendSource).toContain('file.Length > 256 * 1024');
});

test('MAFile codes use a protected endpoint and account-name autofill', () => {
  expect(storeSource).toContain('HMACSHA1');
  expect(storeSource).toContain('ITwoFactorService/QueryTime/v0001/');
  expect(storeSource).toContain('JsonValueKind.String');
  expect(storeSource).toContain('long.TryParse');
  expect(backendSource).toContain('app.MapPost("/api/authenticators/{id:guid}/code"');
  expect(backendSource).toContain('TryGenerateCodeForAccountAsync(session.AccountName');
  expect(backendSource).toContain('SteamGuardType.DeviceCode');
  expect(appSource).toContain('/api/authenticators/${id}/code');
  expect(htmlSource).toContain('data-view="authenticator"');
});

test('authenticator codes are live with an expiry countdown instead of a fetch button', () => {
  expect(appSource).toContain('data-authenticator-progress');
  expect(appSource).toContain('clearAuthenticatorTimers');
  expect(appSource).toContain('refreshAuthenticatorCode');
  expect(appSource).toContain('authenticatorCodeExpiresAt');
  expect(appSource).toContain('Gültig noch');
  expect(appSource).not.toContain('data-authenticator-code');
});

test('the upstream licensing and scope are documented', () => {
  expect(noticesSource).toContain('https://github.com/geel9/SteamAuth');
  expect(noticesSource).toContain('Only the shared-secret-based code-generation algorithm is used.');
});
