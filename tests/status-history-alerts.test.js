import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const worker = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');
const notifier = await readFile(new URL('../src/Backend/DiscordWebhookNotifier.cs', import.meta.url), 'utf8');

test('tracks security and status transitions instead of only periodic checks', () => {
  expect(worker).toContain('previousSteamVacBanned');
  expect(worker).toContain('previousGameBanned');
  expect(worker).toContain('GC meldet einen VAC-Ban');
  expect(worker).toContain('Steam Game-Ban erkannt');
  expect(worker).toContain('CS2-Besitz geändert');
});

test('critical ban transitions can trigger an explicit Discord everyone alert', () => {
  expect(worker).toContain('mentionEveryone: true');
  expect(notifier).toContain('allowed_mentions');
  expect(notifier).toContain('SendAccountEventAsync');
});
