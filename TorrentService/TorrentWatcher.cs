// Copyright © 2017–2022 Dontnod Entertainment

using MonoTorrent;
using MonoTorrent.Client;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Logger = NLog.Logger;

namespace Dontnod.TorrentService
{
    /// <summary>Watches over a set of directories to find and start torrents.</summary>
    public class TorrentWatcher : IDisposable
    {
        private static readonly Logger logger = LogManager.GetLogger("TorrentWatcher");

        private const int loadingLimit = 10; // Limit loading per update to accelerate startup
        private const int hashingLimit = 10; // Limit concurrent hashing to reduce IO utilization
        private static readonly TimeSpan updateTimerPeriod = TimeSpan.FromSeconds(2);

        private readonly ClientEngine torrentEngine;
        private readonly DownloaderFastResume fastResume;
        private readonly object torrentEngineLock = new object();

        private readonly object configurationLock = new object();
        private List<string> pathCollectionField;
        private string fastResumePath;
        private bool skipHashing;

        private readonly Timer updateTimer;
        private readonly object updateEntryLock = new object();
        private readonly object updateLock = new object();
        private bool isUpdating = false;

        public TorrentWatcher(ClientEngine torrentEngine)
        {
            this.torrentEngine = torrentEngine;

            fastResume = new DownloaderFastResume();
            updateTimer = new Timer(_ => Update());
        }

        public void ApplyConfiguration(ApplicationConfiguration configuration)
        {
            lock (configurationLock)
            {
                pathCollectionField = new List<string>(configuration.DirectoriesToWatch.Select(p => Path.GetFullPath(p)));
                fastResumePath = configuration.FastResumePath;
                skipHashing = configuration.SkipHashing;
            }
        }

        public void Start()
        {
            lock (updateLock)
            {
                logger.Info("Starting (WatchedDirectories: {0})", String.Join(", ", pathCollectionField));
                updateTimer.Change(TimeSpan.Zero, updateTimerPeriod);
            }
        }

        public void Stop()
        {
            lock (updateLock)
            {
                logger.Info("Stopping");
                updateTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                lock (torrentEngineLock)
                {
                    torrentEngine.StopAll();
                }
            }
        }

        public void Dispose()
        {
            lock (updateLock)
            {
                lock (torrentEngineLock)
                {
                    torrentEngine.Dispose();
                }
            }
        }

        private void Update()
        {
            lock (updateEntryLock)
            {
                if (isUpdating)
                    return;

                isUpdating = true;
            }

            lock (updateLock)
            {
                try
                {
                    UpdateRegisteredTorrents();
                    UpdateActiveTorrents();
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Update failed");
                }
            }

            lock (updateEntryLock)
            {
                isUpdating = false;
            }
        }

        private ICollection<string> ListTorrentFiles()
        {
            List<string> localPathCollection;

            lock (configurationLock)
            {
                localPathCollection = new List<string>(pathCollectionField);
            }

            List<string> allTorrentPaths = new List<string>();
            foreach (string path in localPathCollection)
                if (Directory.Exists(path))
                    allTorrentPaths.AddRange(Directory.EnumerateFiles(path, "*.torrent", SearchOption.AllDirectories));
            return allTorrentPaths;
        }

        public ICollection<TorrentManager> ListTorrentManagers()
        {
            lock (torrentEngineLock)
            {
                return new List<TorrentManager>(torrentEngine.Torrents);
            }
        }

        private void UpdateRegisteredTorrents()
        {
            ICollection<string> allTorrentPaths = ListTorrentFiles();
            ICollection<TorrentManager> allTorrentManagers = ListTorrentManagers();

            int loadCount = 0;

            foreach (string path in allTorrentPaths.OrderByDescending(p => File.GetCreationTime(p)))
            {
                if (allTorrentManagers.Any(t => t.Torrent.TorrentPath == path) == false)
                {
                    if (TryAddTorrent(path))
                        loadCount += 1;
                }

                if (loadCount >= loadingLimit)
                    break;
            }

            foreach (TorrentManager manager in allTorrentManagers)
            {
                if (allTorrentPaths.Contains(manager.Torrent.TorrentPath) == false)
                    TryRemoveTorrent(manager);
            }
        }

        private void UpdateActiveTorrents()
        {
            ICollection<TorrentManager> allTorrentManagers = ListTorrentManagers();

            int hashingCount = allTorrentManagers.Count(t => t.State == TorrentState.Hashing);

            if (hashingCount < hashingLimit)
            {
                IEnumerable<TorrentManager> torrentsToStart = allTorrentManagers
                    .Where(t => t.State == TorrentState.Stopped)
                    .OrderByDescending(t => t.Torrent.CreationDate)
                    .Take(hashingLimit - hashingCount);

                foreach (TorrentManager manager in torrentsToStart)
                    manager.StartAsync().Wait();
            }
        }

        private bool TryAddTorrent(string path)
        {
            try
            {
                Torrent torrent = Torrent.Load(path);

                lock (torrentEngineLock)
                {
                    if (torrentEngine.Contains(torrent))
                        return false;
                }

                var torrentSettings = new TorrentSettings()
                {
                    MaximumConnections = 256, // default is 60
                    UploadSlots = 64,         // default is 8
                };
                TorrentManager torrentManager = new TorrentManager(torrent, Path.GetDirectoryName(path), torrentSettings);
                torrentManager.TorrentStateChanged += OnTorrentStateChanged;

                if (string.IsNullOrEmpty(fastResumePath) || !fastResume.TryLoad(fastResumePath, torrentManager))
                {
                    // If no fast resume is available, the skipHashing option may create a fake one for us that
                    // causes only the first and last chunks to get hashed.
                    if (skipHashing)
                    {
                        var length = torrentManager.Bitfield.Length;
                        var bitfield = new BitField(length);
                        var unhashed = new BitField(length);
                        for (int i = 0; i < length; ++i)
                        {
                            bool ok = i != 0 && i != length - 1;
                            ok = true;
                            bitfield.Set(i, ok);
                            unhashed.Set(i, !ok);
                        }
                        torrentManager.LoadFastResume(new FastResume(torrentManager.InfoHash, bitfield, unhashed));
                    }
                }

                lock (torrentEngineLock)
                {
                    torrentEngine.Register(torrentManager);
                }

                logger.Info("Added torrent {0}", path);

                return true;
            }
            catch (Exception exception)
            {
                logger.Warn(exception, "Failed to add torrent {0}", path);
                return false;
            }
        }

        private bool TryRemoveTorrent(TorrentManager torrentManager)
        {
            try
            {
                if (torrentManager.State == TorrentState.Stopped)
                {
                    lock (torrentEngineLock)
                    {
                        torrentEngine.Unregister(torrentManager);
                    }

                    torrentManager.TorrentStateChanged -= OnTorrentStateChanged;

                    logger.Info("Removed torrent {0}", torrentManager.Torrent.TorrentPath);

                    return true;
                }
                else
                {
                    torrentManager.StopAsync().Wait();
                    return false;
                }
            }
            catch (Exception exception)
            {
                logger.Warn(exception, "Failed to remove torrent {0}", torrentManager.Torrent.TorrentPath);
                return false;
            }
        }

        private void OnTorrentStateChanged(object sender, TorrentStateChangedEventArgs eventArgs)
        {
            TorrentManager torrentManager = (TorrentManager)sender;
            string torrentPath = torrentManager.Torrent.TorrentPath;

            logger.Trace("{0} changed (State: {1} => {2}, Progress: {3:0.0}%)", torrentPath, eventArgs.OldState, eventArgs.NewState, torrentManager.Progress);

            // Save fast resume state
            switch (eventArgs.NewState)
            {
                case TorrentState.Seeding:
                case TorrentState.Stopped:
                    if (string.IsNullOrEmpty(fastResumePath) == false)
                        fastResume.TrySave(fastResumePath, torrentManager);
                    break;
                default: break;
            }

            // Log torrent state change
            switch (eventArgs.NewState)
            {
                case TorrentState.Starting: logger.Info("Starting {0}", torrentPath); break;
                //case TorrentState.Downloading: logger.Info("Downloading {0} (Progress: {1:0.0}%)", torrentPath, torrentManager.Progress); break;
                case TorrentState.Hashing: logger.Info("Hashing {0}", torrentPath); break;
                case TorrentState.Seeding: logger.Info("Seeding {0}", torrentPath); break;
                case TorrentState.Error: logger.Warn(torrentManager.Error.Exception, "{0} error for torrent {1}", torrentManager.Error.Reason, torrentPath); break;
                default: break;
            }
        }
    }
}
