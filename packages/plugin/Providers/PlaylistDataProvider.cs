using System;
using System.Collections.Generic;
using System.IO;
using MusicBeePlugin.Providers;
using MusicBeePlugin.Models;
using MusicBeePlugin.Ffi;

namespace MusicBeePlugin.Providers
{
    /// <summary>
    ///     Data provider implementation for playlist and queue operations.
    ///     Contains all MusicBee-specific API logic including queue management and playlist operations.
    /// </summary>
    public class PlaylistDataProvider : IPlaylistDataProvider
    {
        private readonly Plugin.MusicBeeApiInterface _api;

        public PlaylistDataProvider(Plugin.MusicBeeApiInterface api)
        {
            _api = api;
        }

        #region Now Playing List Management

        public bool MoveNowPlayingTrack(int fromIndex, int toIndex)
        {
            // Both positions are zero-based slots in the queue. MusicBee's API
            // takes the index of the item to move after, so moving a track upward
            // requires the preceding slot. Keep the wire contract independent of
            // this host-specific index conversion.
            if (fromIndex < 0 || toIndex < 0)
                return false;

            if (fromIndex == toIndex)
                return true;

            LogCurrentTrackState("BEFORE", fromIndex, toIndex);

            int[] fromIndices = { fromIndex };
            int destinationIndex = fromIndex > toIndex
                ? toIndex - 1
                : toIndex;

            var moved = _api.NowPlayingList_MoveFiles(fromIndices, destinationIndex);
            LogCurrentTrackState("AFTER", fromIndex, toIndex, moved);
            return moved;
        }

        /// <summary>
        /// Writes diagnostic snapshots around a queue move. This deliberately logs
        /// both MusicBee's current-playing URL and the URL at its reported queue index,
        /// so we can tell whether the host index moved independently of the playing file.
        /// </summary>
        private void LogCurrentTrackState(string phase, int fromIndex, int toIndex, bool? moveResult = null)
        {
            try
            {
                var playingUrl = _api.NowPlaying_GetFileUrl() ?? string.Empty;
                var querySucceeded = _api.NowPlayingList_QueryFiles(null);
                var currentIndex = querySucceeded ? _api.NowPlayingList_GetCurrentIndex() : -1;
                var indexedUrl = querySucceeded && currentIndex >= 0
                    ? _api.NowPlayingList_GetListFileUrl(currentIndex) ?? string.Empty
                    : string.Empty;
                var logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MusicBeeRemote");
                Directory.CreateDirectory(logDirectory);
                var logPath = Path.Combine(logDirectory, "queue-move-debug.log");
                var line = string.Format(
                    "{0:O} phase={1} from={2} to={3} moveResult={4} queryOk={5} currentIndex={6} playingUrl=\"{7}\" indexedUrl=\"{8}\"{9}",
                    DateTime.Now, phase, fromIndex, toIndex,
                    moveResult.HasValue ? moveResult.Value.ToString() : "n/a",
                    querySucceeded, currentIndex, playingUrl, indexedUrl, Environment.NewLine);
                File.AppendAllText(logPath, line);
            }
            catch
            {
                // Diagnostics must never break playback or queue operations.
            }
        }

        public bool RemoveFromNowPlayingList(int index)
        {
            return _api.NowPlayingList_RemoveAt(index);
        }

        public bool ClearNowPlayingList()
        {
            return _api.NowPlayingList_Clear();
        }

        public bool PlayNowPlayingTrack(string fileUrl)
        {
            return _api.NowPlayingList_PlayNow(fileUrl);
        }

        public bool PlayNowPlayingByIndex(int index)
        {
            _api.NowPlayingList_QueryFiles(null);
            var trackToPlay = _api.NowPlayingList_GetListFileUrl(index);
            if (!string.IsNullOrEmpty(trackToPlay))
                return _api.NowPlayingList_PlayNow(trackToPlay);
            return false;
        }

        #endregion

        #region Queue Operations

        public bool QueueFiles(QueueType queueType, string[] fileUrls, string playFileUrl = null)
        {
            switch (queueType)
            {
                case QueueType.Next:
                    return _api.NowPlayingList_QueueFilesNext(fileUrls);

                case QueueType.Last:
                    return _api.NowPlayingList_QueueFilesLast(fileUrls);

                case QueueType.PlayNow:
                    _api.NowPlayingList_Clear();
                    _api.NowPlayingList_QueueFilesLast(fileUrls);
                    return _api.NowPlayingList_PlayNow(fileUrls[0]);

                case QueueType.AddAndPlay:
                    _api.NowPlayingList_Clear();
                    _api.NowPlayingList_QueueFilesLast(fileUrls);
                    return !string.IsNullOrEmpty(playFileUrl)
                        ? _api.NowPlayingList_PlayNow(playFileUrl)
                        : _api.NowPlayingList_PlayNow(fileUrls[0]);

                default:
                    return false;
            }
        }

        public bool PlayAllLibrary(bool shuffle = false)
        {
            if (shuffle)
                return _api.NowPlayingList_PlayLibraryShuffled();

            // End AutoDJ if enabled
            if (_api.Player_GetAutoDjEnabled())
                _api.Player_EndAutoDj();

            // Set shuffle to false
            _api.Player_SetShuffle(false);

            // Get all library files
            string[] songsList = null;
            _api.Library_QueryFilesEx(null, out songsList);

            if (songsList != null && songsList.Length > 0)
            {
                // Clear now playing list and queue all files
                _api.NowPlayingList_Clear();
                var success = _api.NowPlayingList_QueueFilesNext(songsList);
                if (success)
                    _api.Player_PlayNextTrack();
                return success;
            }

            return false;
        }

        #endregion

        #region Playlist Management

        public bool PlayPlaylist(string playlistUrl)
        {
            return _api.Playlist_PlayNow(playlistUrl);
        }

        /// <summary>
        ///     One playlist's files, in playlist order. <c>Playlist_QueryFilesEx</c>
        ///     answers the whole playlist in a single call, so the core reads tags
        ///     for the page it serves rather than for every file here.
        /// </summary>
        public PlaylistFiles GetPlaylistFiles(string playlistUrl)
        {
            string[] files = null;
            var ok = _api.Playlist_QueryFilesEx(playlistUrl, out files);
            return new PlaylistFiles
            {
                name = _api.Playlist_GetName(playlistUrl) ?? string.Empty,
                paths = ok && files != null ? new List<string>(files) : new List<string>(),
                kind = _api.Playlist_GetType(playlistUrl).ToString()
            };
        }

        public IEnumerable<PlaylistEntry> GetPlaylistCatalog()
        {
            _api.Playlist_QueryPlaylists();

            while (true)
            {
                var url = _api.Playlist_QueryGetNextPlaylist();
                if (string.IsNullOrEmpty(url))
                    yield break;

                yield return new PlaylistEntry
                {
                    name = _api.Playlist_GetName(url) ?? string.Empty,
                    url = url,
                    kind = _api.Playlist_GetType(url).ToString()
                };
            }
        }

        public string CreatePlaylist(string folder, string name, string[] files)
        {
            return _api.Playlist_CreatePlaylist(folder ?? string.Empty, name, files) ?? string.Empty;
        }

        public bool DeletePlaylist(string playlistUrl)
        {
            return _api.Playlist_DeletePlaylist(playlistUrl);
        }

        public bool AppendToPlaylist(string playlistUrl, string[] files)
        {
            return _api.Playlist_AppendFiles(playlistUrl, files);
        }

        public bool SetPlaylistFiles(string playlistUrl, string[] files)
        {
            return _api.Playlist_SetFiles(playlistUrl, files);
        }

        public IEnumerable<Playlist> GetPlaylists()
        {
            _api.Playlist_QueryPlaylists();

            while (true)
            {
                var url = _api.Playlist_QueryGetNextPlaylist();
                if (string.IsNullOrEmpty(url))
                    yield break;

                var name = _api.Playlist_GetName(url);
                yield return new Playlist
                {
                    name = name ?? string.Empty,
                    url = url ?? string.Empty
                };
            }
        }

        #endregion
    }
}
