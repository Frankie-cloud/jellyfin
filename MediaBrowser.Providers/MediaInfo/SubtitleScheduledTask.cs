﻿using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace MediaBrowser.Providers.MediaInfo
{
    public class SubtitleScheduledTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IServerConfigurationManager _config;
        private readonly ISubtitleManager _subtitleManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly ILogger _logger;
        private readonly IJsonSerializer _json;

        public SubtitleScheduledTask(ILibraryManager libraryManager, IJsonSerializer json, IServerConfigurationManager config, ISubtitleManager subtitleManager, ILogger logger, IMediaSourceManager mediaSourceManager)
        {
            _libraryManager = libraryManager;
            _config = config;
            _subtitleManager = subtitleManager;
            _logger = logger;
            _mediaSourceManager = mediaSourceManager;
            _json = json;
        }

        public string Name
        {
            get { return "Download missing subtitles"; }
        }

        public string Description
        {
            get { return "Searches the internet for missing subtitles based on metadata configuration."; }
        }

        public string Category
        {
            get { return "Library"; }
        }

        private SubtitleOptions GetOptions()
        {
            return _config.GetConfiguration<SubtitleOptions>("subtitles");
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = GetOptions();

            var types = new List<string>();

            if (options.DownloadEpisodeSubtitles)
            {
                types.Add("Episode");
            }
            if (options.DownloadMovieSubtitles)
            {
                types.Add("Movie");
            }

            if (types.Count == 0)
            {
                return;
            }

            var videos = _libraryManager.GetItemList(new InternalItemsQuery
            {
                MediaTypes = new string[] { MediaType.Video },
                IsVirtualItem = false,
                ExcludeLocationTypes = new LocationType[] { LocationType.Remote, LocationType.Virtual },
                IncludeItemTypes = types.ToArray()

            }).OfType<Video>()
                .ToList();

            if (videos.Count == 0)
            {
                return;
            }

            var failHistoryPath = Path.Combine(_config.ApplicationPaths.CachePath, "subtitlehistory.json");
            var history = GetHistory(failHistoryPath);

            var numComplete = 0;
            var hasChanges = false;

            foreach (var video in videos)
            {
                DateTime lastAttempt;
                if (history.TryGetValue(video.Id.ToString("N"), out lastAttempt))
                {
                    if ((DateTime.UtcNow - lastAttempt).TotalDays <= 7)
                    {
                        continue;
                    }
                }

                try
                {
                    var shouldRetry = await DownloadSubtitles(video, options, cancellationToken).ConfigureAwait(false);

                    if (shouldRetry)
                    {
                        history[video.Id.ToString("N")] = DateTime.UtcNow;
                    }
                    else
                    {
                        history.Remove(video.Id.ToString("N"));
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error downloading subtitles for {0}", ex, video.Path);
                    history[video.Id.ToString("N")] = DateTime.UtcNow;
                }

                hasChanges = true;

                // Update progress
                numComplete++;
                double percent = numComplete;
                percent /= videos.Count;

                progress.Report(100 * percent);
            }

            if (hasChanges)
            {
                _json.SerializeToFile(history, failHistoryPath);
            }
        }

        private Dictionary<string,DateTime> GetHistory(string path)
        {
            try
            {
                var result = _json.DeserializeFromFile<Dictionary<string, DateTime>>(path);

                if (result != null)
                {
                    return result;
                }
            }
            catch
            {
            }

            return new Dictionary<string, DateTime>();
        }

        private async Task<bool> DownloadSubtitles(Video video, SubtitleOptions options, CancellationToken cancellationToken)
        {
            if ((options.DownloadEpisodeSubtitles &&
                video is Episode) ||
                (options.DownloadMovieSubtitles &&
                video is Movie))
            {
                var mediaStreams = _mediaSourceManager.GetStaticMediaSources(video, false).First().MediaStreams;

                var downloadedLanguages = await new SubtitleDownloader(_logger,
                    _subtitleManager)
                    .DownloadSubtitles(video,
                    mediaStreams,
                    options.SkipIfEmbeddedSubtitlesPresent,
                    options.SkipIfAudioTrackMatches,
                    options.RequirePerfectMatch,
                    options.DownloadLanguages,
                    cancellationToken).ConfigureAwait(false);

                // Rescan
                if (downloadedLanguages.Count > 0)
                {
                    await video.RefreshMetadata(cancellationToken).ConfigureAwait(false);
                    return false;
                }

                return true;
            }

            return false;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[] { 
            
                // Every so often
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(24).Ticks}
            };
        }

        public string Key
        {
            get { return "DownloadSubtitles"; }
        }
    }
}
