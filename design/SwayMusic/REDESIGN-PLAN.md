# SwayMusic visual design specification

This is the approved design specification. Implementation and verification status are recorded in the project's PROGRESS.md. The original mockup uses illustrative titles and abstract cover art; the application must use actual service and library data.

## Direction

Use **Sway** as the short in-app brand and **SwayMusic** as the full product name, executable and distribution name. Tagline: **Find your rhythm.** The personality is calm, tactile and music-first: warm charcoal, pale pear green, generous spacing and clear typography. Album artwork supplies most of the visual variety. Avoid glass, neon glows, gradients on application chrome and decorative animation.

## Identity

The symbol is one continuous S-shaped ribbon: a suggestion of a sound groove and relaxed motion. It works in one color and remains recognizable without a music-note cliché. The lime rounded-square treatment is for the Windows application icon; the ribbon alone works for compact branding. The included SVGs are editable concepts. Final approved assets should include outlined wordmark artwork, monochrome/reversed variants and native multi-resolution ICO exports; inspect the mark at 16, 20, 24, 32, 48 and 256 px.

Use lowercase **sway** for the wordmark. Use **SwayMusic** in Windows Properties, installer/release names and About. Preserve original Metrolist attribution and GPL notices in About and distributions.

## Color tokens

| Role | Dark theme | Light theme |
| --- | --- | --- |
| Window | #101412 | #F5F4EC |
| Sidebar / player | #171D19 | #ECEEE4 |
| Raised / selected neutral | #202922 | #E3E8DA |
| Primary text | #F3F4EB | #18221A |
| Secondary text | #A5AEA5 | #586552 |
| Divider | #303B32 | #CCD4C3 |
| Primary action | #D6F68A | #425B22 |
| Action text | #152019 | #F7FAEF |
| Secondary accent | #C4B5DF | #695684 |
| Error | #F4A69D | #A6352D |

Pear is for play, selected navigation, progress and keyboard focus. Lilac is a restrained secondary accent, not a second competing button color. Use neutral rows for ordinary selections. Never encode liked/downloaded/failed states with color alone. Validate 4.5:1 contrast for normal text and 3:1 for UI boundaries/focus states against their actual surfaces.

## Typography and geometry

**Inter** is the proposed display and UI family, bundled locally for predictable rendering. Use Regular 400 for body text, Medium 500 for navigation/metadata emphasis, and Semibold 600 for track titles, headings and the wordmark. Segoe UI is the Windows fallback. Inter is open source under SIL OFL: https://rsms.me/inter/.

| Element | Size / weight |
| --- | --- |
| Main page heading | 30 px / 600 |
| Section heading | 18 px / 600 |
| Track title / primary button | 14 px / 500–600 |
| Body / navigation | 14 px / 400–500 |
| Artist / album metadata | 12–13 px / 400 |
| Time / small labels | 12 px, tabular numerals for times |

Use an 8 px spacing grid with 4 px subdivisions; 24–32 px page padding, 16 px section gaps, 8 px icon/text gaps. Buttons are 36–40 px high, icon targets at least 32 px for desktop and 44 px where touch is expected. Row heights: comfortable 60 px, compact 44 px. Radii: 8 px controls, 12 px artwork, 16 px large artwork; round only the primary playback button. Keep native window decorations and snapping.

## Main desktop layout

- Left navigation: Home, Search, Library; then Playlists, Downloads and History. Settings/account stays at the bottom. Keep Queue and Lyrics adjacent to playback rather than giving every feature equal sidebar weight.
- Top: persistent search field; page-specific controls stay beside the content they affect.
- Home: a concise Continue listening section and compact recommendation shelves. No oversized greeting banner that pushes music below the fold.
- Library: virtualized song list with artist/album/duration columns; search, sort and optional density selection. Use one row context menu for add to playlist, queue, download and remove. Remove the current permanent wall of six action buttons.
- Right panel: optional Queue or Lyrics, approximately 280–320 px in the native app. Collapse it on narrower windows. The player remains available throughout navigation.
- Bottom player: artwork/title/artist on the left, primary controls and timeline in the center, volume and queue/lyrics access on the right. All controls stay in consistent locations.

Design first for 1280×800, then verify 1024×720 and the existing 850×600 minimum. At narrow widths collapse the secondary panel first, then optional columns. Do not shrink essential text to make a layout fit.

## States and interaction

Show a persistent selected navigation item and a separate current-track indicator. Reveal secondary row actions on hover/focus, while always providing a context menu and keyboard access. Keep Play/Pause, Previous/Next, seek, volume and Queue visible. Space and existing shortcuts remain unchanged. Loading playback must expose Cancel; show actionable errors inline, not as repeated dialogs. Define empty library, no results, offline, expired account and download quota states before implementation. Synchronized lyrics should highlight the active line without a running visualizer or animated background.

## Implementation phases

1. **Approve identity and screen direction.** Review the mockup, symbol, dark/light palette and density. Deliver final vector/icon/font assets and named Avalonia design tokens.
2. **Rename safely.** Update window/product names, executable, solution/namespaces where appropriate, single-instance identifier, release scripts, verification scripts, documentation and generated archives. Rename public artifacts to SwayMusic. Preserve Metrolist's upstream attribution.
3. **Protect existing data.** Plan a one-time, idempotent migration from `%LOCALAPPDATA%\Metrolist` to `%LOCALAPPDATA%\SwayMusic`. Copy and verify library, queue, settings, downloads and protected credentials before switching; retain an untouched backup. Keep Windows DPAPI user scope and test failure/retry. Never silently overwrite a pre-existing SwayMusic library.
4. **Apply the shell.** Add resource dictionaries for colors/typography/spacing; implement navigation, search, adaptive content layout and player bar using existing view models/services.
5. **Refine screens.** Style Home, Library, playlists, downloads, queue, lyrics and settings; add context menus and complete empty/loading/error/keyboard-focus states. Present local-only features clearly.
6. **Verify and release.** Re-run actual streaming/offline/native media tests, keyboard and high-DPI checks, 850×600 resizing, virtualization and data migration. Compare startup, private memory and visible/minimized CPU with the current build. Publish newly named source/binary archives only after the checks pass.

## Performance guardrails

Keep Avalonia, native audio, the current software-rendering choice and virtualized lists. Bound/downsample any added artwork cache; do not decode every library cover at once. Use static vector icons and a single bundled font family. Avoid blur, shadows, continuous animations, visualizers and periodic background discovery. Seek sliders and lyric highlights should update only when needed; preserve paused/minimized idle behavior. Rename/restyle the existing player rather than rewriting its working services.

## Completion criteria

The approved logo is readable at taskbar sizes; all main screens share the same tokens; keyboard focus and error states are clear; existing libraries migrate without loss; releases and startup checks use SwayMusic names; attribution remains intact; real playback still passes; performance shows no material regression against the documented baseline.
