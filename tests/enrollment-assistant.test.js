import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const contracts = await readFile(new URL('../src/Shared/Contracts.cs', import.meta.url), 'utf8');
const program = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');
const store = await readFile(new URL('../src/Backend/EnrollmentStore.cs', import.meta.url), 'utf8').catch(() => '');
const html = await readFile(new URL('../src/Backend/wwwroot/index.html', import.meta.url), 'utf8');
const app = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');

test('adds a bounded semi-automatic enrollment flow without signup automation', () => {
  expect(contracts).toContain('EnrollmentStartRequest');
  expect(contracts).toContain('EnrollmentSessionDto');
  expect(program).toContain('/api/enrollment/status');
  expect(program).toContain('/api/enrollment/start');
  expect(store).toContain('DailyLimit = 2');
  expect(store).toContain('store.steampowered.com/join');
  expect(program).not.toContain('HttpClient signup');
});

test('connects the manual signup step to the existing Steam login flow', () => {
  expect(html).toContain('id="enrollmentPanel"');
  expect(html).toContain('id="prepareEnrollmentButton"');
  expect(html).toContain('id="enrollmentLoginButton"');
  expect(app).toContain('/api/enrollment/start');
  expect(app).toContain('openAuthDialog()');
  expect(app).toContain('signupUrl');
});
