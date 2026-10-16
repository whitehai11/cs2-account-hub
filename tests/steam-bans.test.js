import { expect, test } from 'bun:test';
import { readFile } from 'node:fs/promises';

const banServiceSource = await readFile(new URL('../src/Backend/SteamBanService.cs', import.meta.url), 'utf8');
const contractsSource = await readFile(new URL('../src/Shared/Contracts.cs', import.meta.url), 'utf8');
const programSource = await readFile(new URL('../src/Backend/Program.cs', import.meta.url), 'utf8');
const appSource = await readFile(new URL('../src/Backend/wwwroot/app.js', import.meta.url), 'utf8');

test('queries Steam PlayerBans through the optional server-side Web API key', () => {
  expect(banServiceSource).toContain('CS2_HUB_STEAM_API_KEY');
  expect(banServiceSource).toContain('ISteamUser/GetPlayerBans/v1/');
  expect(banServiceSource).toContain('NumberOfGameBans');
  expect(banServiceSource).toContain('NumberOfVACBans');
});

test('keeps Steam bans separate from the Game Coordinator cooldown state', () => {
  expect(contractsSource).toContain('SteamGameBanned');
  expect(contractsSource).toContain('SteamBansCheckedUtc');
  expect(programSource).toContain('ApplySteamBans');
  expect(programSource).toContain('SteamBanService');
  expect(programSource).toContain('QueryAsync(account.SteamId');
});

test('shows game bans explicitly in the account UI and attention queue', () => {
  expect(appSource).toContain('account.steamGameBanned === true');
  expect(appSource).toContain('Game-Ban');
  expect(appSource).toContain('Steam-Bans');
});
