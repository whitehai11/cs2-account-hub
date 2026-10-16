import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const html = await readFile(new URL('../src/Backend/wwwroot/index.html', import.meta.url), 'utf8');
const app = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');

test('exposes the account timeline as its own navigation view', () => {
  expect(html).toContain('data-view="timeline"');
  expect(html).toContain('id="timelineView"');
  expect(html).toContain('Account-Timeline');
  expect(app).toContain("timeline: ['Timeline'");
  expect(app).toContain("view === 'timeline'");
  expect(app).toContain("if (timeline) loadTimeline()");
});

test('does not load the account timeline as part of Personalize', () => {
  expect(app).not.toContain("if (state.view === 'personalize') await loadTimeline()");
  expect(app).toContain("setView('timeline')");
});
