# Installation, updates and GitHub releases

SwayMusic uses Velopack 1.2.161 for its per-user Windows x64 installer and update handoff. Download `SwayMusic-Setup.exe` from a published [GitHub Release](https://github.com/DeFlabi123/SwayMusic/releases). The installer bundles .NET, creates Start menu/desktop shortcuts and registers an uninstaller. An existing folder distribution requires this one-time installation to enable updating.

Application files live under `%LOCALAPPDATA%\SwayMusicDesktop`. Libraries, downloads, caches and Windows-user-protected account sessions remain in `%LOCALAPPDATA%\SwayMusic`, outside the installation tree. Closing the old folder copy before installation avoids running two copies. Uninstalling the installed application preserves the separate data directory.

In Settings & account, Updates shows the installed version, last successful check, release notes and download progress. The sidebar links to Settings when an update is available.

| Mode | Behavior |
| --- | --- |
| Automatic, default | Check daily, download in the background and install after the next successful normal quit. Restart and update is also available. |
| Notify only | Check daily; the user chooses Download update and Restart and update. |
| Manual | Check only with Check for updates. |

The first automatic check waits 30 seconds after initialization. A failed check backs off for an hour, persisted across restarts. While the app stays open, the schedule is examined every five minutes. Checking and downloading do not stop playback. An update restart restores the saved queue paused. Download cancellation removes incomplete content. A pending package is verified again after relaunch and before installation; a failed save prevents an update restart and leaves the app usable. Forced termination leaves the package pending for a later controlled attempt. Ordinary folder/development builds and isolated verification fixtures cannot update the production installation.

**GitHub configuration**

Actions must be enabled for `DeFlabi123/SwayMusic`, with GitHub's official actions allowed. `.github/workflows/release.yml` grants read access to the build and `contents: write` only to the draft-release job. GitHub supplies its own token; no personal token is embedded in the application.

Add one repository secret at Settings > Secrets and variables > Actions:

| Secret | Value |
| --- | --- |
| `UPDATE_SIGNING_PRIVATE_KEY` | The complete PEM contents of the private update-signing key. |

For this workspace the key was generated outside the repository, at `%LOCALAPPDATA%\SwayMusic-ReleaseKeys\update-signing-private-key.pem`. Keep a secure backup and use the same key for future releases. The matching public key is embedded from `src/SwayMusic.Desktop/Updates/update-public-key.pem`. The private key is never bundled in the installer or source archive.

`New-UpdateSigningKey.ps1` is a first-time setup tool and refuses to replace an existing key. Do not run it for each release. Rotating the trusted public key requires a compatible application rollout; this version trusts one key.

Windows Authenticode signing is separate from signed update metadata. For local testing the installer can be unsigned. Before wider public distribution, choose a Windows signing provider, configure its credentials, and set `WINDOWS_SIGN_TEMPLATE` to the Velopack signing command containing `{{file}}`. Once configured, set the repository variable `REQUIRE_WINDOWS_SIGNING` to `true` to reject unsigned release builds. Provider-specific credentials must be exposed only to the packaging step. `Pack-Release.ps1` also supports Velopack's `VPK_AZURE_TRUSTED_SIGN_FILE` environment setting for local Azure signing configuration. Windows signing must happen during Velopack packaging, before metadata hashes are generated. Signing does not guarantee an immediate absence of SmartScreen warnings.

**Create a release**

Update `RELEASE_NOTES.md` and push a stable version tag such as `v1.0.0`. The workflow derives all application/package versions from that tag, runs the deterministic and headless suites, publishes self-contained binaries, creates authenticated packages, verifies the published executable and uploads the result as an Actions artifact. It then creates a draft release and attaches the tested assets. An existing release is not overwritten automatically. A manual Actions run accepts a version and creates a build artifact without a GitHub Release.

Review the installer and publish the completed draft to expose it to the app. Keep these assets together:

- `SwayMusic-Setup.exe` for first installation.
- The versioned full `.nupkg` and `releases.win-x64-stable.json` feed.
- `sway-update.json` and `sway-update.sig`, containing authenticated package metadata and its detached signature.
- `SwayMusic-Windows-source.zip` and `SHA256SUMS.txt`.

The updater considers published, non-prerelease `vMAJOR.MINOR.PATCH` releases with signed metadata, selects the highest version in the latest 30 GitHub releases, and rejects older or equal versions. Keep stable releases within this discovery window. The fixed channel is `win-x64-stable`; beta/channel switching and delta packages are not implemented.

For a local build, use PowerShell 7:

```powershell
pwsh -File scripts/Pack-Release.ps1 -Version 1.0.0
```

It uses the local private-key file unless `UPDATE_SIGNING_PRIVATE_KEY` is supplied as an environment variable. It checks that the private key matches the embedded public key, reuses the single current unpacked build at `artifacts/SwayMusic-win-x64`, and puts installer/update assets in `artifacts/installer`. Packaging explicitly creates the corresponding source archive. The installer is approximately 88 MiB in the initial local build; subsequent sizes depend on the release.

**Verification and trust**

The updater uses a separate cookie-free HTTP client. ECDSA P-256/SHA-256 authenticates the exact metadata bytes. Signed fields bind the product ID, stable channel, architecture, version, safe package filename, byte count, SHA-256 and release notes. Full downloads have size limits and must pass verification before Velopack can extract a new updater. Cached packages are independently checked because the pinned SDK can skip validation of an existing download. Pending metadata and packages are rechecked offline after launch. Automatic SDK startup application is disabled, so mode selection and successful saving govern installation.

The deterministic suite tests signature/target/path rejection, corrupt and cancelled downloads, rate limits and offline pending verification. Headless tests exercise the actual Settings bindings, mode persistence, download/restart controls, queue saving and a locked-library failure. The opt-in installed test uses a real setup, a locally signed second version and the production update service with simulated GitHub transport:

```powershell
pwsh -File scripts/Verify-Installer.ps1
```

Run it in a clean Windows user or VM. It refuses to replace an existing installation, temporarily installs into artifacts, preserves existing shortcuts, checks real shortcuts and launches, applies an A-to-B update and uninstalls the fixture. It leaves a report at `artifacts/installer-verification.json` after success. This is a local transport test; publishing/downloading from a live GitHub Release remains a separate end-to-end check. A clean Windows 10 machine, Windows trust prompts, non-ASCII usernames and network interruption during real downloads remain manual platform checks.
