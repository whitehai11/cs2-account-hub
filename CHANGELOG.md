# Changelog

All notable changes to CS2 Account Hub are documented here.

## [Unreleased]

### Added

- Optional second Steam Mail sender/domain area with configurable labels and routing.

### Fixed

- Kept Mail list and detail scrolling inside their panels instead of scrolling the page.
- Restored stable full-row message interaction, debounced search updates and truthful sync failures.

## [0.2.0] - 2026-10-16

### Added

- Mission Control dashboard for play-ready and not-play-ready account operations.
- Global Steam Mail inbox with IMAP over TLS, sender filtering, bounded retention and read/unread state.
- Account timeline for status changes, cooldowns, matches and operations.
- Cooldown progression tracking and projected next cooldown duration.
- Recent CS2 match history and rating history in account details.
- Staggered background monitoring with active-match protection.
- Optional Discord notifications when cooldowns expire.
- Server-side Loadout and Personalize presets.
- Authenticated web dashboard with protected session cookies and hardened request handling.

### Changed

- Reworked the dashboard into a full-viewport navy control-room interface.
- Added dimensional surfaces, perspective depth, lift states and layered shadows to the UI.
- Improved account profile enrichment, Prime classification and status presentation.
- Updated Docker deployment defaults and documented the optional Steam Mail configuration.

### Fixed

- Only plain-text mail parts are fetched for the Steam inbox.
- Active games and matches are not interrupted by scheduled status checks.
- Nullable match scores no longer break match history processing.
- Loadout payloads and desktop publish resources are handled correctly.

## [0.1.1] - 2026-09-28

- Fix unpackaged WinUI desktop publish resources.

## [0.1.0]

- Initial public release.
