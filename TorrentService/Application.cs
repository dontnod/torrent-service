// Copyright © 2017–2022 Dontnod Entertainment

using Mono.Unix;
using Mono.Unix.Native;
using MonoTorrent.Client;
using Newtonsoft.Json;
using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using Logger = NLog.Logger;

namespace Dontnod.TorrentService
{
    /// <summary>Main class for the application.</summary>
    public class Application
    {
        private static readonly Logger logger = LogManager.GetLogger("Application");
        private static readonly TimeSpan statusTimerPeriod = TimeSpan.FromMinutes(1);

        public static string ApplicationFullName { get { return Assembly.GetExecutingAssembly().GetName().Name; } }
        public static Version Version { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

        [STAThread]
        private static void Main(string[] arguments)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => logger.Fatal((Exception)e.ExceptionObject, "Unhandled exception");

            InitializeLogging();

            if (arguments.Length != 1)
            {
                throw new ArgumentException("A configuration file is required");
            }

            ApplicationConfiguration configuration = LoadConfiguration(arguments[0]);
            Application application = new Application(configuration);
            application.Run();
        }

        private static void InitializeLogging()
        {
            LoggingConfiguration nlogConfiguration = new LoggingConfiguration();
            string logLayout = "${date:format=yyyy-MM-dd HH\\:mm\\:ss} [${level}][${logger}] ${message}${onexception:${newline}}${exception:format=tostring}";

#if DEBUG
            DebuggerTarget debuggerTarget = new DebuggerTarget("Debugger") { Layout = logLayout };
            nlogConfiguration.AddTarget(debuggerTarget);
            nlogConfiguration.AddRule(LogLevel.Debug, LogLevel.Fatal, debuggerTarget);
#endif

            ConsoleTarget consoleTarget = new ConsoleTarget("Console") { Layout = logLayout };
            nlogConfiguration.AddTarget(consoleTarget);
            nlogConfiguration.AddRule(LogLevel.Info, LogLevel.Fatal, consoleTarget);

            LogManager.Configuration = nlogConfiguration;
        }

        private static ApplicationConfiguration LoadConfiguration(string configurationPath)
        {
            JsonSerializer serializer = new JsonSerializer();
            using (StreamReader streamReader = new StreamReader(configurationPath))
            using (JsonReader jsonReader = new JsonTextReader(streamReader))
                return serializer.Deserialize<ApplicationConfiguration>(jsonReader);
        }

        public Application(ApplicationConfiguration configuration)
        {
            this.configuration = configuration;

            var torrentSettings = new EngineSettings()
            {
                ListenPort = configuration.Port,
            };
            if (configuration.ReportedAddress != null)
                torrentSettings.ReportedAddress = new IPEndPoint(configuration.ReportedAddress, configuration.Port);

            torrentEngine = new ClientEngine(torrentSettings);
            torrentWatcher = new TorrentWatcher(torrentEngine);
            torrentWatcher.ApplyConfiguration(configuration);
            statusTimer = new Timer(_ => LogStatus(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        private readonly ApplicationConfiguration configuration;
        private readonly ClientEngine torrentEngine;
        private readonly TorrentWatcher torrentWatcher;
        private readonly Timer statusTimer;

        public void Run()
        {
            logger.Info($"Starting (Version: {Version}, Port: {configuration.Port})");

            torrentWatcher.Start();
            statusTimer.Change(statusTimerPeriod, statusTimerPeriod);

            bool shouldExit = false;
            Console.CancelKeyPress += (s, e) => { shouldExit = true; e.Cancel = true; };
            List<UnixSignal> terminationSignals = new List<UnixSignal>();

            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                terminationSignals.Add(new UnixSignal(Signum.SIGINT));
                terminationSignals.Add(new UnixSignal(Signum.SIGTERM));
            }

            while (shouldExit == false)
            {
                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    var timeout = TimeSpan.FromSeconds(1);
                    if (UnixSignal.WaitAny(terminationSignals.ToArray(), timeout) != timeout.TotalMilliseconds)
                        shouldExit = true;
                }
                else
                {
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }
            }

            logger.Info("Stopping torrents");
            statusTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            torrentWatcher.Stop();
            torrentWatcher.Dispose();

            logger.Info("Exiting");
        }

        private void LogStatus()
        {
            try
            {
                if (String.IsNullOrEmpty(configuration.StatusFilePath))
                {
                    LogStatusToLogger();
                }
                else
                {
                    LogStatusToFile(configuration.StatusFilePath);
                }
            }
            catch (Exception exception)
            {
                logger.Warn(exception, "Failed to log status");
            }
        }

        public void LogStatusToLogger()
        {
            IEnumerable<TorrentManager> torrentManagers = torrentWatcher.ListTorrentManagers();

            Dictionary<string, int> countByState = Enum.GetValues(typeof(TorrentState)).Cast<TorrentState>()
                .ToDictionary(state => state.ToString(), state => torrentManagers.Count(torrentManager => torrentManager.State == state));

            if (torrentManagers.Any())
            {
                logger.Info("{0} torrents ({1})", torrentManagers.Count(),
                    String.Join(", ", countByState.Where(kvp => kvp.Value != 0).Select(kvp => kvp.Key + ": " + kvp.Value)));
            }
            else
            {
                logger.Info("No torrents");
            }

            ShowStatus(torrentManagers.Where(torrentManager => torrentManager.State == TorrentState.Error));
            ShowStatus(torrentManagers.Where(torrentManager => torrentManager.State == TorrentState.Downloading));
            ShowStatus(torrentManagers.Where(torrentManager => (torrentManager.State == TorrentState.Seeding) && torrentManager.Peers.Available > 0));
        }

        public void ShowTorrentStatus(string filter = null)
        {
            if (filter == null)
                filter = "";
            ShowStatus(torrentWatcher.ListTorrentManagers().Where(torrentManager => torrentManager.Torrent.TorrentPath.Contains(filter)));
        }

        public void ShowSeedingStatus()
        {
            ShowStatus(torrentWatcher.ListTorrentManagers().Where(torrentManager => (torrentManager.State == TorrentState.Seeding) && torrentManager.Peers.Available > 0));
        }

        private void ShowStatus(IEnumerable<TorrentManager> torrents)
        {
            foreach (TorrentManager torrentManager in torrents)
            {
                logger.Info("Status for torrent {0} (State: {1}, Progress: {2:0.0}%, Peers: [{3}])",
                    torrentManager.Torrent.TorrentPath, torrentManager.State, torrentManager.Progress,
                    // FIXME: list of peers is no longer directly available
                    //String.Join(", ", torrentManager.Peers.GetPeers().Select(peer => peer.Uri)));
                    torrentManager.Peers.Available);
            }
        }

        private void LogStatusToFile(string statusFilePath)
        {
            ICollection<TorrentManager> allTorrents = torrentWatcher.ListTorrentManagers();

            string statusText = String.Format("{0} {1} - Status - {2}", ApplicationFullName, Version, DateTime.UtcNow);
            statusText += Environment.NewLine + Environment.NewLine;

            statusText += "Watched directories:" + Environment.NewLine;
            foreach (string path in configuration.DirectoriesToWatch)
                statusText += "  " + path + Environment.NewLine;
            statusText += Environment.NewLine;

            statusText += "Torrent engine:" + Environment.NewLine;
            statusText += String.Format("  DownloadSpeed: {0} B/s", torrentEngine.TotalDownloadSpeed) + Environment.NewLine;
            statusText += String.Format("  UploadSpeed: {0} B/s", torrentEngine.TotalUploadSpeed) + Environment.NewLine;
            // FIXME: these functions no longer exist
            //statusText += String.Format("  Open connections: {0} / {1}", torrentEngine.ConnectionManager.OpenConnections, torrentEngine.ConnectionManager.MaxOpenConnections) + Environment.NewLine;
            //statusText += String.Format("  Half open connections: {0} / {1}", torrentEngine.ConnectionManager.HalfOpenConnections, torrentEngine.ConnectionManager.MaxHalfOpenConnections) + Environment.NewLine;
            statusText += Environment.NewLine;

            statusText += "Torrents:" + Environment.NewLine;
            foreach (TorrentManager torrentManager in allTorrents.OrderBy(t => t.Torrent.TorrentPath))
            {
                statusText += "  " + String.Format("{0} ({1})", torrentManager.Torrent.TorrentPath, torrentManager.State) + Environment.NewLine;

                // FIXME: these functions no longer exist
                //ICollection<PeerId> peers = torrentManager.GetPeers();
                //if (peers.Any())
                //    statusText += "    Peers: " + String.Join(", ", peers) + Environment.NewLine;
            }

            statusText += Environment.NewLine;

            File.WriteAllText(statusFilePath + ".tmp", statusText);
            File.Delete(statusFilePath);
            File.Move(statusFilePath + ".tmp", statusFilePath);
        }
    }
}
