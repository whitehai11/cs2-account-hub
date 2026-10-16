import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const backendSource = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');

test('operation status polling uses its own rate-limit policy', () => {
  expect(backendSource).toContain('options.AddPolicy("operation-status"');
  expect(backendSource).toContain('context.Request.Path.StartsWithSegments("/api/operations")');
  expect(backendSource).toContain('}).RequireRateLimiting("operation-status");');
});
