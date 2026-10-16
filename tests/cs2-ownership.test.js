import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const ownershipSource = await readFile(new URL('../src/Backend/SteamGameOwnershipService.cs', import.meta.url), 'utf8');
const gatewaySource = await readFile(new URL('../src/Backend/SteamGateway.cs', import.meta.url), 'utf8');
const programSource = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');
const contractsSource = await readFile(new URL('../src/Shared/Contracts.cs', import.meta.url), 'utf8');
const appSource = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');

test('checks authenticated ownership of CS2 AppID 730', () => {
  expect(ownershipSource).toContain('account/licenses/?l=english');
  expect(ownershipSource).toContain('counter-strike 2');
  expect(ownershipSource).toContain('SteamGameOwnershipSnapshot');
});

test('persists CS2 ownership independently from GC status', () => {
  expect(gatewaySource).toContain('QueryOwnershipAsync');
  expect(programSource).toContain('Cs2Owned');
  expect(programSource).toContain('Cs2OwnershipCheckedUtc');
  expect(programSource).toContain('ApplyCs2Ownership');
  expect(programSource).toContain('Cs2Owned = true');
  expect(contractsSource).toContain('Cs2Owned');
  expect(contractsSource).toContain('Cs2OwnershipCheckedUtc');
});

test('shows accounts without CS2 ownership explicitly', () => {
  expect(appSource).toContain('account.cs2Owned === false');
  expect(appSource).toContain('CS2 nicht im Besitz');
  expect(appSource).toContain('CS2-Besitz');
});
