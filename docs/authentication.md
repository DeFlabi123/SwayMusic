# Google sign-in and saved sessions

Open **Settings → Sign in with Google**. Sign-in requires Microsoft Edge or Google Chrome installed on Windows and a working connection to Google and YouTube Music. Sway opens a separate browser window with a temporary profile. Enter your credentials only on Google's page, complete any Google verification, and let the browser return to YouTube Music. Sway checks that YouTube Music confirms an account before showing its name and saving the session. Select Home to load recommendations for that account.

The app does not request your Google password, provide its own password form or embed a login WebView. The main window and audio engine remain native. No browser is bundled, launched at ordinary app startup or kept running for playback.

## Cancellation and sign-out

**Cancel sign-in** or closing the sign-in browser ends the attempt. An attempt also has a ten-minute deadline. **Sign out** cancels any pending attempt and removes the saved account; a late browser response cannot sign you back in. Closing Sway cancels the operation and disposes its owned browser process and pipes. Normal completion and cancellation close that browser and remove its temporary profile. If Windows prevents cleanup, the app reports a cleanup failure instead of silently claiming success.

A cancelled or invalid replacement leaves the previously saved account unchanged. A successful sign-in replaces the single active account. The browser's selected YouTube Music account/channel is used; Sway does not provide a separate native account or channel picker.

## Saved data and compatibility

`%LOCALAPPDATA%\SwayMusic\account.dpapi` contains a versioned Windows DPAPI envelope protected for the current Windows user. It stores the Cookie header, visitor data, data-sync identifier, active account index and the confirmed display name/handle/channel ID. The app encrypts the payload before writing its temporary replacement file. Temporary account files and diagnostic messages do not contain raw credentials. Account fields have explicit size limits and reject control characters and multiline values.

The browser uses a unique `BrowserLogin\signin-<guid>` directory beneath SwayMusic's data directory. It does not reuse or modify your regular Edge/Chrome profile. Browser-managed session data can exist in that temporary profile during sign-in; the attempt owns and removes it when disposed. An unexpected machine/process termination can interrupt normal cleanup.

Old DPAPI files containing only a Cookie-header value remain readable. **Advanced: import a session** retains manual Cookie-header import for compatibility. Treat cookies as credentials and do not share them. Sign-out removes either saved format. Migration from the old Metrolist directory preserves its original encrypted bytes; see [data-migration.md](data-migration.md).

## Behavior and limits

Saved sessions are restored without launching a browser. Cookies may expire or be revoked, in which case sign in again. There is no background Google password/OAuth refresh flow. The active account now syncs liked songs, playlists and subscribed artists, with supported edits sent back to YouTube Music. Existing device collections remain separate. See [account-library.md](account-library.md) for scope, account isolation and failure handling.

The external public-stream resolver receives no account cookies. Signing in does not establish private/uploaded, age-restricted, region-restricted or Premium-only playback through that resolver. Unsupported account or playback responses are reported rather than replaced with fabricated data.

## Developer notes and verification

The installed browser is controlled through inherited CDP pipes, with no TCP debugging port. Collection is gated on an exact HTTPS `music.youtube.com` origin and filters cookie domains/paths/expiry. Visitor/configuration data and the selected account index accompany the cookie into a separate `account/account_menu` request. Only a confirmed account is committed under the same serialization gate used for sign-out; cancellation and an operation generation check reject stale results.

This depends on the installed Edge/Chrome CDP implementation and YouTube Music's current page configuration/account API. Browser or service changes can require an update; neither is a stable public integration contract. This compatibility detail belongs to implementation/support documentation, not the sign-in form.

The final Release build passed with zero warnings/errors. DPAPI storage, account-request/browser-data fixtures and integrated headless UI/cancellation/sign-out lifecycle checks passed. A real installed Microsoft Edge 157 blank-page transport/lifecycle smoke test passed in 1.10 seconds, including process/profile cleanup. The full deterministic/native/live regression passed in 56.50 seconds wall time, including sustained public AAC playback and a real ranged download followed by offline native decoding.

A blank-page smoke test verifies browser launch, pipe transport and cleanup only. No automated run claims that a real Google account completed Google's sign-in or MFA challenges; that remains a manual user check. `sway-account-login.png` shows the reviewed Settings entry point.
