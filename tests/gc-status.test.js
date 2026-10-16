import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const serviceSource = await readFile(new URL('../src/Backend/SteamReference/Services/CsPremierScoreService.cs', import.meta.url), 'utf8');

test('keeps the GC status check bounded and uses a short reconnect welcome window', () => {
  expect(serviceSource).toContain('HelloTotalBudget = TimeSpan.FromSeconds(45)');
  expect(serviceSource).toContain('RemainingTimeout(helloDeadline, RetryWelcomeTimeout)');
});

test('does not discard restriction fields returned with the players profile', () => {
  expect(serviceSource).toContain('profile.HasRestrictionData ? profile : null');
  expect(serviceSource).toContain('HasRestrictionData');
});

test('emits safe phase diagnostics for slow GC checks', () => {
  expect(serviceSource).toContain('GC status check {CheckId} started for account {AccountId}.');
  expect(serviceSource).toContain('GC status check {CheckId} finished for account {AccountId}');
  expect(serviceSource).toContain('restrictionFields={HasRestrictionData}');
  expect(serviceSource).toContain('gcMessages={GcMessages}');
  expect(serviceSource).toContain('message.MessageType');
});
