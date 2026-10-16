import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const contracts = await readFile(new URL('../src/Shared/Contracts.cs', import.meta.url), 'utf8');
const program = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');
const html = await readFile(new URL('../src/Backend/wwwroot/index.html', import.meta.url), 'utf8');
const app = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');

test('defines a bounded bulk command contract and endpoint', () => {
  expect(contracts).toContain('BulkCommandRequest');
  expect(program).toContain('/api/accounts/bulk-commands');
  expect(program).toContain('Maximal 50 Accounts');
  expect(program).toContain('AccountIds.Distinct()');
});

test('offers selectable accounts and visible bulk progress in the UI', () => {
  expect(html).toContain('id="bulkToolbar"');
  expect(html).toContain('id="bulkCommand"');
  expect(app).toContain('state.selectedIds');
  expect(app).toContain('/api/accounts/bulk-commands');
  expect(app).toContain('bulkProgress');
});
