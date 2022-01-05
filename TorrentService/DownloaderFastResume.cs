// Copyright © 2010–2022 Dontnod Entertainment

using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Dontnod.TorrentService
{
    public class DownloaderFastResume
    {
        private static readonly NLog.Logger logger = LogManager.GetLogger(nameof(DownloaderFastResume));

        private readonly Dictionary<string, BEncodedDictionary> fastResumeCollection = new Dictionary<string, BEncodedDictionary>();
        private readonly object fastResumeLock = new object();

        public void TrySave(string fastResumePath, TorrentManager torrentManager)
        {
            try
            {
                lock (fastResumeLock)
                {
                    if (fastResumeCollection.ContainsKey(fastResumePath) == false)
                        fastResumeCollection.Add(fastResumePath, new BEncodedDictionary());

                    BEncodedDictionary fastResume = fastResumeCollection[fastResumePath];
                    fastResume[ConvertToHash(torrentManager)] = torrentManager.SaveFastResume().Encode();
                    File.WriteAllBytes(fastResumePath, fastResume.Encode());
                }
            }
            catch (Exception exception)
            {
                string torrentFile = Path.GetFileName(torrentManager.Torrent.TorrentPath);
                logger.Warn(exception, "Failed to save fast resume for {0}", torrentFile);
            }
        }

        public void TryLoad(string fastResumePath, TorrentManager torrentManager)
        {
            try
            {
                lock (fastResumeLock)
                {
                    if (fastResumeCollection.ContainsKey(fastResumePath) == false)
                    {
                        BEncodedDictionary newFastResume = File.Exists(fastResumePath) ?
                            BEncodedValue.Decode<BEncodedDictionary>(File.ReadAllBytes(fastResumePath)) : new BEncodedDictionary();
                        fastResumeCollection.Add(fastResumePath, newFastResume);
                    }

                    BEncodedDictionary fastResume = fastResumeCollection[fastResumePath];
                    BEncodedString hash = ConvertToHash(torrentManager);
                    if (fastResume.ContainsKey(hash))
                        torrentManager.LoadFastResume(new FastResume((BEncodedDictionary)fastResume[hash]));
                }
            }
            catch (Exception exception)
            {
                string torrentFile = Path.GetFileName(torrentManager.Torrent.TorrentPath);
                logger.Warn(exception, "Failed to load fast resume for {0}", torrentFile);
            }
        }

        public void TryRemove(string fastResumePath, TorrentManager torrentManager)
        {
            try
            {
                lock (fastResumeLock)
                {
                    if (fastResumeCollection.ContainsKey(fastResumePath))
                    {
                        BEncodedDictionary fastResume = fastResumeCollection[fastResumePath];
                        fastResume.Remove(ConvertToHash(torrentManager));
                        File.WriteAllBytes(fastResumePath, fastResume.Encode());
                    }
                }
            }
            catch (Exception exception)
            {
                string torrentFile = Path.GetFileName(torrentManager.Torrent.TorrentPath);
                logger.Warn(exception, "Failed to remove fast resume for {0}", torrentFile);
            }
        }

        public void TryClean(string fastResumePath, IEnumerable<TorrentManager> torrentsToKeep)
        {
            try
            {
                lock (fastResumeLock)
                {
                    if (fastResumeCollection.ContainsKey(fastResumePath))
                    {
                        BEncodedDictionary fastResume = fastResumeCollection[fastResumePath];
                        IEnumerable<BEncodedString> hashesToKeep = torrentsToKeep.Select(ConvertToHash);
                        foreach (BEncodedString hash in fastResume.Keys.Except(hashesToKeep).ToList())
                            fastResume.Remove(hash);
                        File.WriteAllBytes(fastResumePath, fastResume.Encode());
                    }
                }
            }
            catch (Exception exception)
            {
                logger.Warn(exception, "Failed to clean fast resume");
            }
        }

        private BEncodedString ConvertToHash(TorrentManager torrentManager)
        {
            return torrentManager.Torrent.InfoHash.ToHex();
        }
    }
}
