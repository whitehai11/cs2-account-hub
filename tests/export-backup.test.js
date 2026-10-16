import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const contracts = await readFile(new URL('../src/Shared/Contracts.cs', import.meta.url), 'utf8');
const program = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');
const backup = await readFile(new URL('../src/Backend/ExportBackupService.cs', import.meta.url), 'utf8');
const html = await readFile(new URL('../src/Backend/wwwroot/index.html', import.meta.url), 'utf8');
const app = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');

test('offers a safe account export without protected secrets', () => {
  expect(contracts).toContain('SafeAccountExportDto');
  expect(contracts).toContain('AccountExportBundleDto');
  expect(program).toContain('/api/export/accounts');
  expect(program).toContain('ProtectedRefreshToken');
  expect(program).toContain('text/csv');
});

test('creates an encrypted backup that includes the key ring', () => {
  expect(contracts).toContain('BackupRequest');
  expect(program).toContain('/api/backup');
  expect(backup).toContain('CS2HUB-BACKUP-V1');
  expect(backup).toContain('keys');
  expect(backup).toContain('Pbkdf2');
  expect(backup).toContain('steam-authenticators.json');
});

test('exposes export and backup actions in a dedicated view', () => {
  expect(html).toContain('data-view="export"');
  expect(html).toContain('id="exportView"');
  expect(html).toContain('id="backupPassphrase"');
  expect(app).toContain('/api/export/accounts?format=');
  expect(app).toContain('/api/backup');
});
