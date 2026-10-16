import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const contracts = await readFile(new URL('../src/Shared/Contracts.cs', import.meta.url), 'utf8');
const program = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');
const timeline = await readFile(new URL('../src/Backend/AccountTimeline.cs', import.meta.url), 'utf8');
const html = await readFile(new URL('../src/Backend/wwwroot/index.html', import.meta.url), 'utf8');
const app = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');

test('provides bounded fleet analytics derived from account history', () => {
  expect(contracts).toContain('AccountAnalyticsDto');
  expect(contracts).toContain('AccountAnalyticsPointDto');
  expect(program).toContain('/api/analytics');
  expect(timeline).toContain('Analytics(');
});

test('renders analytics on Mission Control', () => {
  expect(html).toContain('id="analyticsChart"');
  expect(html).toContain('id="analyticsSummary"');
  expect(app).toContain('/api/analytics?days=30');
  expect(app).toContain('renderAnalytics');
});
