# Spec: Dashboard UI refresh

## Objective

Replace the current card-heavy dashboard presentation with a polished, desktop-first control-room UI for managing multiple CS2 accounts. The redesign must preserve existing behavior and API contracts while improving hierarchy, density, empty states, responsiveness, and accessibility.

## Scope

- New dashboard shell with compact sidebar and clearer topbar actions.
- Account list presented as dense, scannable rows with search and filters.
- Selected-account inspector with grouped information and actions.
- Useful no-selection, loading, empty, and error states.
- Consistent design tokens, status colors, focus states, and reduced-motion behavior.
- Responsive layouts at approximately 1440px, 1024px, 768px, and 390px widths.
- No backend behavior changes.

## Tech Stack and Structure

- Static HTML in `src/Backend/wwwroot/index.html`.
- Plain JavaScript rendering/state in `src/Backend/wwwroot/app.js`.
- CSS design system and responsive layout in `src/Backend/wwwroot/styles.css`.
- No new frontend framework or build pipeline.

## Design Contract

- Direction: dark slate control room; restrained blue accent; green/amber/red only for semantics.
- Avoid: excessive gradients, oversized empty cards, heavy shadows, and decorative UI that competes with account status.
- Use consistent spacing and a small radius scale; prefer inline SVG icons over Unicode symbols for controls.
- Preserve German UI copy and current domain terminology.

## Testing Strategy

- Verify existing JavaScript rendering paths manually in a real browser.
- Verify keyboard navigation, visible focus, dialog focus, and reduced motion.
- Verify no account/profile/timeline text can create HTML through the renderer.
- Verify responsive layout at the four target widths.
- Run the repository build when a .NET SDK is available.

## Boundaries

- Always: preserve current API behavior, escape untrusted values, keep interactive controls keyboard accessible.
- Ask first: changing backend contracts or removing an existing dashboard capability.
- Never: add a frontend dependency solely for styling, commit credentials, or silently remove functionality.

## Success Criteria

- The dashboard has a clear primary action and a useful first viewport.
- Account rows are scannable without opening every account.
- The right-side inspector is useful when selected and does not dominate the empty state.
- Existing account actions, settings, timeline, authentication, and dialogs remain usable.
- The UI remains usable on mobile without horizontal page overflow.
