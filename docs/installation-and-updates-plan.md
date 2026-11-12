# SwayMusic installer and in-app updates

Design plan, 12 November 2026. Implementation and release configuration are now documented in [updates.md](updates.md); this document retains the intended design and acceptance criteria.

Use **Velopack** to package the existing self-contained Windows x64 build and manage updates. Host the installer and update packages in [DeFlabi123/SwayMusic Releases](https://github.com/DeFlabi123/SwayMusic/releases). The repository is public, so users can receive updates without a GitHub account or access token. Velopack supplies both an installer and a C# update SDK, making it a good fit for the current Avalonia application. See the [Velopack overview](https://docs.velopack.io/).

**1. Define the installation layout**

Keep Windows 10 build 19041 or later, x64, as the initial target. Continue bundling .NET and the existing playback helpers so users do not need to install a runtime separately.

| Item | Proposed location or behavior |
| --- | --- |
| Download | A clearly named `SwayMusic-Setup.exe` release asset |
| Permanent package ID | `SwayMusicDesktop` |
| Display name / executable | SwayMusic / `SwayMusic.exe` |
| Installed application | `%LOCALAPPDATA%\SwayMusicDesktop` |
| Existing user data | `%LOCALAPPDATA%\SwayMusic`, unchanged |
| Update channel | `win-x64-stable` |
| Windows integration | Start menu and desktop shortcuts; Installed apps uninstall entry |

Velopack's normal Windows setup installs under `%LOCALAPPDATA%\{packId}`. Using `SwayMusicDesktop` keeps its installation tree separate from the existing library, downloaded music and DPAPI credentials. Uninstalling should preserve that data. This separation must be verified with an actual uninstall test. See [installer behavior](https://docs.velopack.io/packaging/installer) and [preserving files](https://docs.velopack.io/integrating/preserved-files).

The existing folder distribution needs a one-time installer run to adopt the new update system. Close the old copy first; the installed app then uses the same user data. Retain the existing single-instance mutex so an old folder copy and the installed copy cannot write to the library simultaneously. Update old shortcuts to launch the installed app. Development and ordinary unpackaged builds should show that automatic updates require an installed copy.

**2. Make updates part of Settings**

Add an **Updates** section to Settings & account showing the installed version, last successful check, available version, release notes and download progress. Actions should include **Check for updates**, **Download update**, **Restart and update**, and **Retry**, as appropriate to the current state.

Use these modes, persisted in settings:

| Mode | Behavior |
| --- | --- |
| Automatic, default | Check daily, download in the background, install after a successful normal quit. Offer Restart and update when ready. |
| Notify only | Check daily and show availability; downloading and installing require the user's buttons. |
| Manual | Check only when the user selects Check for updates. |

Start automatic checks about 30 seconds after successful application initialization. Save the last check time so reopening the app does not make another request each time; schedule another daily check if it stays open. A manual check can bypass this interval. Handle timeouts, offline use and GitHub rate limits with bounded retries and backoff. Public GitHub API access has a shared per-IP limit; see [update sources](https://docs.velopack.io/integrating/update-sources).

Music continues during checking and downloading. Never restart in the middle of a listening session automatically. A small update indicator can link to Settings without taking over the playback status area. After restarting, restore the queue and position paused, matching today's behavior.

**3. Integrate startup, downloading and shutdown**

Pin a tested stable Velopack SDK and compatible `vpk` packaging tool. Add the SDK bootstrap at the beginning of `Program.Main`, before the mutex, Avalonia initialization or library access, so installer lifecycle invocations can exit promptly. Explicitly disable Velopack's default startup auto-apply behavior; the app's update mode and verification must govern installation. See [startup integration](https://docs.velopack.io/getting-started/csharp) and [apply behavior](https://docs.velopack.io/integrating/overview).

Introduce an injectable `IAppUpdateService` and a Velopack implementation in the desktop project. It should own checking, downloading, verification, pending-update state and applying. Expose a small state model: idle, checking, available, downloading, ready, applying and failed. Serialize operations and provide cancellation. Use a separate update network client that carries no Google session cookies.

Integrate through `MainViewModel.Updates.cs` and the existing Settings view. Network checks must remain outside window construction. Headless tests, isolated data-directory fixtures and `--verify-release` must not contact the production feed, apply updates or alter a real installation.

Refactor shutdown into an awaited preparation step that captures queue/position, cancels and drains ongoing work, and confirms persistence before disposing playback and handing control to the updater. The current window closing handler catches disposal/save errors and still closes, so directly calling an updater from it would not establish successful saving. For an update request, a save failure must prevent applying and leave a usable app with a retry message.

For Restart and update, finish preparation before invoking Velopack's restart operation. For automatic installation on normal quit, schedule its wait-for-exit operation only at the final successful exit stage. That operation has a forced-termination deadline; never launch it while saves are still running. Verify the restarted process can acquire the mutex. Reevaluate the selected update mode at apply time. Forced termination or power loss should leave a pending download for a later controlled attempt.

**4. Establish update trust and recovery**

Use HTTPS and verify package size and SHA-256. Velopack's download integrity checks are useful, but a hash in a downloaded feed alone does not authenticate the publisher. For unattended updates, add signed release metadata containing package ID, version, channel, architecture, filename and SHA-256, verified with a public key embedded in the app. Verify the downloaded full package against that metadata before any new updater executable can run. This is an additional application feature, not an assumed Velopack guarantee.

Start with full-package updates to keep this verification path simple. Add delta downloads later once signed metadata, reconstruction and final-package verification work together. Reject invalid signatures, incompatible targets and unexpected older versions. Never fall back to an unverified package. Recheck a pending package before applying it, including after relaunch. Plan signing-key rotation with an overlap release trusted by existing installations.

For public distribution, configure Windows Authenticode signing through Velopack for the app, updater and setup, with timestamping. Choose the signing provider before public launch; unsigned local packages can be used for development. Keep private keys and publishing credentials out of the repository and app. Signing can reduce Windows trust friction, but does not guarantee an immediate absence of SmartScreen warnings. See [Velopack signing](https://docs.velopack.io/packaging/signing) and [Microsoft's SmartScreen guidance](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation).

On failed download or verification, retain the running version and expose Retry. Test the pinned updater's replacement-failure recovery rather than promising rollback after every kind of failure. For an application regression, publish a higher-version repair release; simply removing a release does not downgrade affected users. Keep older installers available. Future library-schema changes need backups and compatibility planning before any older binary can be used safely.

Ship reviewed, pinned yt-dlp and QuickJS versions inside the app update. They should continue to avoid independent executable self-updates. Update the README's current no-runtime-executable-updates wording to explain this new application update policy.

**5. Add a repeatable release process in this repository**

Use a tag such as `v1.0.0` to trigger a Windows GitHub Actions workflow. Treat this version as an example; choose the first public version during implementation. Keep the tag, assembly version, installer version and in-app display aligned through one version input. Replace the hard-coded `SwayMusic/1.0` user agent with the actual product version.

The workflow should perform these steps in order:

1. Check out the exact tag, install the SDK pinned by `global.json`, restore pinned packaging tools, and fetch checksum-verified stream helpers.
2. Run deterministic, storage and headless checks. Run published-output verification; keep native audio and interactive shell checks in a Windows VM where the required facilities exist. Live provider checks remain separate from deterministic release validation.
3. Publish a clean self-contained `win-x64` payload, preserving the existing exclusions for test assemblies, debug symbols and old Metrolist binaries.
4. Package with permanent ID `SwayMusicDesktop`, main executable `SwayMusic.exe`, display name SwayMusic, and channel `win-x64-stable`. Apply Authenticode signing during packaging, then sign metadata for the final full package.
5. Produce corresponding source, license notices, release notes and checksums. Include the new `.github` workflow and `.config` tool manifest in `Source-Archive.ps1`, whose current allowlist omits them.
6. Upload everything to a draft GitHub Release for that tag. Verify all required assets exist before making it public. Keep publishing permissions limited to the release job; clients require no GitHub token.

Required release assets are the installer, full `.nupkg`, matching `releases.win-x64-stable.json`, signed metadata, source archive and checksums. A setup executable alone does not provide an update feed. Use Velopack's GitHub deployment support to keep package/feed contents consistent. Drafts stay invisible to normal update checks until published. See [packaging outputs](https://docs.velopack.io/packaging/overview) and [GitHub deployment](https://docs.velopack.io/distributing/self-hosting).

Keep `artifacts/SwayMusic-win-x64` as the single current unpacked local build. Generate installer/update assets in a separate packaging output directory only for explicit release packaging. Revise the publishing documentation to distinguish these required distribution assets from optional portable ZIPs.

**6. Deliver and verify in stages**

| Stage | Deliverable and completion check |
| --- | --- |
| Installer | Install, launch, shortcuts, reinstall and uninstall work under a standard Windows user without a separately installed .NET runtime. Existing data survives. |
| Manual in-app update | Install test version A, discover B from a local test feed, download, verify, save and restart into B. Queue, settings, downloads and account credentials survive. |
| Automatic updates | Background checks/downloads work during playback; normal quit installs; mode changes and interrupted downloads behave correctly. |
| GitHub release workflow | Repeat the A-to-B test through actual published GitHub release assets in an isolated test channel, then prepare the first stable release. |

Add meaningful tests for version/channel selection, settings persistence, invalid signatures/hashes, wrong architecture, cancellation, offline/rate-limited responses, concurrent clicks, low disk space, save failure and pending updates across restarts. Use fake services for UI tests and a local update source for integration tests; Velopack provides [testing support](https://docs.velopack.io/integrating/testing).

Complete an installed A-to-B update and uninstall in a clean Windows VM, including a non-ASCII Windows username and locked-file failure. Confirm stream helpers, local audio, online playback and media controls work after updating. Record actual results before describing installer/updater support as shipped.

Implementation can start with the local installer and test feed. Publishing access and a signing identity are needed for the public release stage; writing this plan does not publish a release or change the running app.
