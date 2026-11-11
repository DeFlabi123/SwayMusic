# Metrolist to SwayMusic data migration

On first startup using the default data directory, SwayMusic checks `%LOCALAPPDATA%\Metrolist` before loading its own library. Explicit data directories used by tests and release verification do not trigger migration.

When SwayMusic has no existing data, migration streams a copy into a uniquely named sibling staging directory. Every file is SHA256 checked against its source, and the source file list, sizes and timestamps are checked again to detect changes during copying. Linked directories/files are rejected rather than followed. Close the old application before the first migration if it is still writing data.

The copied JSON metadata is parsed and validated. Track `localPath` values pointing inside the old application directory are remapped to the new one; external music-file paths are retained. Library settings, liked songs, playlists, listening history, queue position, downloads, lyrics and other cached files are included. DPAPI credential bytes are copied unchanged; decryption remains scoped to the same Windows account. JSON metadata has a 64 MiB per-file migration cap; unsupported schemas or malformed files are reported without overwriting the original.

Only after validation does a same-volume directory rename commit the new SwayMusic folder. The old Metrolist directory remains untouched as the backup. The migration marker records provenance and completion. Cancellation or validation/copy failure removes only the owned staging directory and leaves the migration retryable. The caller must not save a fresh empty library after a failed migration.

Any nonempty pre-existing SwayMusic folder is preserved, including folders created by a previous successful migration. It is never automatically merged with or replaced by Metrolist data. A conflicting separate library is reported; a completed migration is a quiet no-op on later startups.

The migration tests operate only on isolated temporary directories. They cover exact media/credential preservation, DPAPI readability, internal/external path handling, queue/settings preservation, existing-destination protection, idempotence, cancellation, concurrent calls, malformed-data failure and successful retry. A locked-source regression verifies failure without partial commit and a successful retry after the old process releases its files; root/nested directory inputs are rejected before enumeration. No development test migrates the real user's AppData.

These checks passed on the stable .NET SDK 10.0.404 in both the standalone SwayMusic.StorageTests project and the full deterministic/native/live suite. The final suite completed in 52.12 seconds and also verified real YouTube streaming, ranged download and offline native playback after the project rename and redesign.
