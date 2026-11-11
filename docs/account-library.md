# YouTube Music collection sync

When signed in, Liked songs, Playlists and Artists use the active YouTube Music account. Artists is under Collection. The app syncs after normal startup and sign-in, refreshes collections when opened after their 30-second freshness window, and offers Sync library in collections and Settings. There is no recurring background polling loop. Cancel sync ends a pending refresh.

Liked songs shows the account's liked songs while signed in. Existing device likes are preserved in the device library and remain accessible through Your library or signed-out Liked songs. The Playlists view includes account-owned/saved playlists and separately labelled device playlists. Existing device collections are never automatically uploaded or overwritten. Artist rows open artist music; Subscribe/Unsubscribe changes the account subscription.

Signed-in song likes/unlikes are sent to YouTube Music. Playlist creation makes a private account playlist. Owned account playlists support rename, delete, adding songs and removing individual playlist entries, including duplicate songs distinguished by playlist entry ID. Removing a saved playlist owned by someone else removes it from the library instead of deleting the original. Device playlist actions continue to affect only the device. The add-to-playlist picker labels account and device targets, and excludes read-only account playlists. Destructive playlist actions require their existing confirmation dialog; confirmation is tied to the original target and account.

## Data and failure handling

The three collections are read from the source-derived FEmusic_liked_videos, FEmusic_liked_playlists and FEmusic_library_corpus_artists endpoints. Selected-tab shelves, grid/list renderers and all continuation pages are read before the new snapshot is committed. Duplicate/repeating continuation tokens, unsupported shapes, logged-out responses and item/page bounds fail visibly instead of treating an incomplete response as an empty library. Refresh has a 60-second deadline.

Metadata snapshots live under `%LOCALAPPDATA%\SwayMusic\account-library`, in files keyed by a hash of the active channel/data-sync identity (or session when neither is available) and account index. They contain collection metadata, not account cookies. They are ordinary local metadata files protected by the Windows profile's filesystem permissions. Credentials remain separately DPAPI encrypted. A cache is loaded only for the matching signed-in identity. Network/parse failures keep the last complete snapshot; startup can display that snapshot while retrying. Signing out clears remote views and cancels operations. Late results cannot restore the previous account's library.

Account HTTP transport disables automatic cookies; the active session's explicit Cookie and authorization headers are used for each request, preventing a previous account's HTTP cookie jar from carrying over. Successful outgoing changes update the confirmed snapshot; rejected mutations leave it unchanged. Non-idempotent mutations are not blindly retried after server/network failures. If a mutation times out after reaching Google, refresh before retrying to avoid duplicate creation/addition. Sync never automatically mutates the remote account.

This implementation syncs these three collections, not the complete Android library schema. Automatic merging/upload of legacy local likes, remote playlist reordering, uploaded/private playback and multi-account management are not included. Large playlist contents still load on opening, with Load more for further track pages. Account/API availability depends on YouTube Music and the saved session.

## Verification

Core fixtures cover paginated collections, edit ownership, exact mutation bodies, subscription parameters, duplicate entry IDs, logged-out rejection, pagination loops and no automatic mutation retries. Native UI fixtures cover Artists, account/device playlists, likes and remote edits, cached restart/offline failure, account isolation and late sync after sign-out. Original local state remains intact in these tests.

A read-only request against the saved signed-in account confirmed 9 liked songs, 3 playlists and 2 subscribed artists. No account mutation was performed by that check. Remote edit operations are source-contract/fixture tested; automated tests do not create, delete, like or unsubscribe items in the real user's account. The full deterministic/native/public-stream suite also passed, including sustained playback and offline download decoding.
