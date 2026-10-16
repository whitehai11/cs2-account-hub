import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const appSource = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');
const relativeTimeStart = appSource.indexOf('function relativeTime');
const relativeTimeEnd = appSource.indexOf('function cooldownPhase', relativeTimeStart);
const relativeTimeSource = relativeTimeStart >= 0 && relativeTimeEnd > relativeTimeStart
  ? appSource.slice(relativeTimeStart, relativeTimeEnd)
  : null;
if (!relativeTimeSource) throw new Error('relativeTime function not found');
const relativeTime = Function(`${relativeTimeSource}\nreturn relativeTime;`)();
const now = Date.parse('2026-10-16T12:00:00.000Z');

function atOffset(seconds) {
  const originalNow = Date.now;
  Date.now = () => now;
  try {
    return relativeTime(new Date(now + seconds * 1000).toISOString());
  } finally {
    Date.now = originalNow;
  }
}

test('formats relative timestamps using the largest useful unit', () => {
  expect(atOffset(-12)).toBe('vor 12 Sek.');
  expect(atOffset(-5 * 60)).toBe('vor 5 Min.');
  expect(atOffset(-3 * 60 * 60)).toBe('vor 3 Std.');
  expect(atOffset(-2 * 86400)).toBe('vor 2 Tagen');
  expect(atOffset(-2 * 7 * 86400)).toBe('vor 2 Wochen');
  expect(atOffset(-2 * 30 * 86400)).toBe('vor 2 Monaten');
  expect(atOffset(-2 * 365 * 86400)).toBe('vor 2 Jahren');
});

test('keeps future timestamps readable', () => {
  expect(atOffset(90)).toBe('in 1 Min.');
  expect(atOffset(2 * 86400)).toBe('in 2 Tagen');
});

test('switches units only after the previous unit is complete', () => {
  expect(atOffset(-(60 - 1))).toBe('vor 59 Sek.');
  expect(atOffset(-(60 * 60 - 1))).toBe('vor 59 Min.');
  expect(atOffset(-(86400 - 1))).toBe('vor 23 Std.');
  expect(atOffset(-(7 * 86400 - 1))).toBe('vor 6 Tagen');
  expect(atOffset(-(30 * 86400 - 1))).toBe('vor 4 Wochen');
  expect(atOffset(-(365 * 86400 - 1))).toBe('vor 12 Monaten');
});
