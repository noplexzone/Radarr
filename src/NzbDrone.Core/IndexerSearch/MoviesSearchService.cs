using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.IndexerSearch
{
    public class MovieSearchService : IExecute<MoviesSearchCommand>, IExecute<MissingMoviesSearchCommand>, IExecute<CutoffUnmetMoviesSearchCommand>, IExecute<MovieEditionSearchCommand>, IExecute<MissingEditionSlotsSearchCommand>, IExecute<CutoffUnmetEditionSlotsSearchCommand>
    {
        private readonly IMovieService _movieService;
        private readonly IMovieCutoffService _movieCutoffService;
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly IQueueService _queueService;
        private readonly IMovieEditionSlotService _movieEditionSlotService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly ICustomFormatCalculationService _customFormatCalculationService;
        private readonly Logger _logger;

        public MovieSearchService(IMovieService movieService,
                                   IMovieCutoffService movieCutoffService,
                                   ISearchForReleases releaseSearchService,
                                   IProcessDownloadDecisions processDownloadDecisions,
                                   IQueueService queueService,
                                   IMovieEditionSlotService movieEditionSlotService,
                                   IMediaFileService mediaFileService,
                                   IQualityProfileService qualityProfileService,
                                   ICustomFormatCalculationService customFormatCalculationService,
                                   Logger logger)
        {
            _movieService = movieService;
            _movieCutoffService = movieCutoffService;
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _queueService = queueService;
            _movieEditionSlotService = movieEditionSlotService;
            _mediaFileService = mediaFileService;
            _qualityProfileService = qualityProfileService;
            _customFormatCalculationService = customFormatCalculationService;
            _logger = logger;
        }

        public void Execute(MoviesSearchCommand message)
        {
            var userInvokedSearch = message.Trigger == CommandTrigger.Manual;

            var movies = _movieService.GetMovies(message.MovieIds)
                .Where(m => (m.Monitored && m.IsAvailable()) || userInvokedSearch)
                .ToList();

            SearchForBulkMovies(movies, userInvokedSearch).GetAwaiter().GetResult();
        }

        public void Execute(MissingMoviesSearchCommand message)
        {
            var pagingSpec = new PagingSpec<Movie>
            {
                Page = 1,
                PageSize = 100000,
                SortDirection = SortDirection.Ascending,
                SortKey = "Id"
            };

            pagingSpec.FilterExpressions.Add(v => v.Monitored == true);

            var movies = _movieService.MoviesWithoutFiles(pagingSpec).Records.ToList();

            var queue = _queueService.GetQueue();
            var queuedMovieIds = queue.Where(q => q.Movie != null && q.AcquisitionTarget.Kind == MovieAcquisitionTargetKind.Main).Select(q => q.Movie.Id).ToHashSet();
            var queuedEditionSlotIds = queue.Where(q => q.AcquisitionTarget.Kind == MovieAcquisitionTargetKind.EditionSlot).Select(q => q.AcquisitionTarget.EditionSlotId.Value).ToHashSet();
            var missing = movies.Where(e => !queuedMovieIds.Contains(e.Id)).ToList();

            SearchForBulkMovies(missing, message.Trigger == CommandTrigger.Manual).GetAwaiter().GetResult();

            // Edition slots are independent: only an exact queued slot suppresses its search.
            SearchMissingEditionSlots(queuedEditionSlotIds, message.Trigger == CommandTrigger.Manual).GetAwaiter().GetResult();
        }

        public void Execute(CutoffUnmetMoviesSearchCommand message)
        {
            var pagingSpec = new PagingSpec<Movie>
            {
                Page = 1,
                PageSize = 100000,
                SortDirection = SortDirection.Ascending,
                SortKey = "Id"
            };

            pagingSpec.FilterExpressions.Add(v => v.Monitored == true);

            var movies = _movieCutoffService.MoviesWhereCutoffUnmet(pagingSpec).Records.ToList();

            var queue = _queueService.GetQueue();
            var queuedMovieIds = queue.Where(q => q.Movie != null && q.AcquisitionTarget.Kind == MovieAcquisitionTargetKind.Main).Select(q => q.Movie.Id).ToHashSet();
            var queuedEditionSlotIds = queue.Where(q => q.AcquisitionTarget.Kind == MovieAcquisitionTargetKind.EditionSlot).Select(q => q.AcquisitionTarget.EditionSlotId.Value).ToHashSet();
            var missing = movies.Where(e => !queuedMovieIds.Contains(e.Id)).ToList();

            SearchForBulkMovies(missing, message.Trigger == CommandTrigger.Manual).GetAwaiter().GetResult();
            SearchCutoffUnmetEditionSlots(queuedEditionSlotIds, message.Trigger == CommandTrigger.Manual);
        }


        public void Execute(MissingEditionSlotsSearchCommand message)
        {
            var queuedEditionSlotIds = _queueService.GetQueue()
                .Where(q => q.AcquisitionTarget.Kind == MovieAcquisitionTargetKind.EditionSlot)
                .Select(q => q.AcquisitionTarget.EditionSlotId.Value)
                .ToHashSet();

            SearchMissingEditionSlots(queuedEditionSlotIds, message.Trigger == CommandTrigger.Manual).GetAwaiter().GetResult();
        }

        public void Execute(CutoffUnmetEditionSlotsSearchCommand message)
        {
            var queuedEditionSlotIds = _queueService.GetQueue()
                .Where(q => q.AcquisitionTarget.Kind == MovieAcquisitionTargetKind.EditionSlot)
                .Select(q => q.AcquisitionTarget.EditionSlotId.Value)
                .ToHashSet();

            SearchCutoffUnmetEditionSlots(queuedEditionSlotIds, message.Trigger == CommandTrigger.Manual);
        }

        private void SearchCutoffUnmetEditionSlots(HashSet<int> queuedEditionSlotIds, bool userInvokedSearch)
        {
            var allSlots = _movieEditionSlotService.GetMonitoredSlotsWithFiles()
                .Where(s => !queuedEditionSlotIds.Contains(s.Id))
                .ToList();

            if (!allSlots.Any())
            {
                return;
            }

            var movieIds = allSlots.Select(s => s.MovieId).Distinct().ToList();
            var filesBySlotId = _mediaFileService.GetFilesByMovies(movieIds)
                .Where(f => f.MovieEditionSlotId.HasValue)
                .ToDictionary(f => f.MovieEditionSlotId.Value);
            var moviesById = _movieService.GetMovies(movieIds).ToDictionary(m => m.Id);
            var profiles = _qualityProfileService.All().ToDictionary(p => p.Id);

            // Group slots by movie, find those where slot file is below the effective profile cutoff
            var slotsToSearch = new List<(Movie Movie, List<MovieEditionSlot> Slots)>();

            foreach (var group in allSlots.GroupBy(s => s.MovieId))
            {
                if (!moviesById.TryGetValue(group.Key, out var movie) || !movie.Monitored)
                {
                    continue;
                }

                var cutoffUnmetSlots = new List<MovieEditionSlot>();

                foreach (var slot in group)
                {
                    if (!filesBySlotId.TryGetValue(slot.Id, out var file))
                    {
                        continue;
                    }

                    // Resolve effective quality profile: slot override takes precedence.
                    QualityProfile effectiveProfile;
                    if (slot.QualityProfileId.HasValue && profiles.TryGetValue(slot.QualityProfileId.Value, out var slotProfile))
                    {
                        effectiveProfile = slotProfile;
                    }
                    else if (movie.QualityProfile != null)
                    {
                        effectiveProfile = movie.QualityProfile;
                    }
                    else if (profiles.TryGetValue(movie.QualityProfileId, out var movieProfile))
                    {
                        effectiveProfile = movieProfile;
                    }
                    else
                    {
                        continue;
                    }

                    if (!effectiveProfile.UpgradeAllowed)
                    {
                        continue;
                    }

                    var cutoff = effectiveProfile.UpgradeAllowed ? effectiveProfile.Cutoff : effectiveProfile.FirststAllowedQuality().Id;
                    var cutoffIndex = effectiveProfile.GetIndex(cutoff);
                    var fileQualityIndex = effectiveProfile.GetIndex(file.Quality.Quality.Id);

                    var customFormats = _customFormatCalculationService.ParseCustomFormat(file, movie);
                    var customFormatScore = effectiveProfile.CalculateCustomFormatScore(customFormats);
                    var requiredCustomFormatScore = Math.Max(effectiveProfile.CutoffFormatScore, slot.MinimumCustomFormatScore ?? int.MinValue);

                    if (fileQualityIndex.Index < cutoffIndex.Index || customFormatScore < requiredCustomFormatScore)
                    {
                        cutoffUnmetSlots.Add(slot);
                    }
                }

                if (cutoffUnmetSlots.Any())
                {
                    slotsToSearch.Add((movie, cutoffUnmetSlots));
                }
            }

            _logger.ProgressInfo("Searching cutoff-unmet edition slots for {0} movie(s)", slotsToSearch.Count);

            foreach (var (movie, slots) in slotsToSearch)
            {
                SearchForEditionSlots(movie, slots, userInvokedSearch).GetAwaiter().GetResult();
            }
        }

        public void Execute(MovieEditionSearchCommand message)
        {
            var userInvokedSearch = message.Trigger == CommandTrigger.Manual;
            var movie = _movieService.GetMovie(message.MovieId);

            var allSlots = _movieEditionSlotService.GetForMovie(message.MovieId);

            List<MovieEditionSlot> slots;

            var explicitIds = new HashSet<int>();
            if (message.MovieEditionSlotId.HasValue)
            {
                explicitIds.Add(message.MovieEditionSlotId.Value);
            }

            if (message.MovieEditionSlotIds?.Any() == true)
            {
                foreach (var id in message.MovieEditionSlotIds)
                {
                    explicitIds.Add(id);
                }
            }

            if (explicitIds.Any())
            {
                slots = allSlots
                    .Where(s => explicitIds.Contains(s.Id) && (s.Monitored || userInvokedSearch))
                    .ToList();
            }
            else
            {
                // Automatic: monitored slots that are still missing a file. Manual trigger also includes unmonitored missing slots.
                var attachedSlotIds = _mediaFileService.GetFilesByMovie(message.MovieId)
                    .Where(f => f.MovieEditionSlotId.HasValue)
                    .Select(f => f.MovieEditionSlotId.Value)
                    .ToHashSet();
                slots = allSlots
                    .Where(s => (s.Monitored || userInvokedSearch) && !attachedSlotIds.Contains(s.Id))
                    .ToList();
            }

            SearchForEditionSlots(movie, slots, userInvokedSearch).GetAwaiter().GetResult();
        }

        private async Task SearchForEditionSlots(Movie movie, List<MovieEditionSlot> slots, bool userInvokedSearch)
        {
            _logger.ProgressInfo("Performing edition search for {0} slot(s) under movie [{1}]", slots.Count, movie.Title);
            var downloadedCount = 0;

            foreach (var slot in slots.OrderBy(s => s.LastSearchTime ?? DateTime.MinValue))
            {
                List<DownloadDecision> decisions;

                try
                {
                    decisions = await _releaseSearchService.MovieEditionSearch(movie, slot, userInvokedSearch, false);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unable to search for edition slot [{0}] of movie [{1}]", slot.Id, movie.Title);
                    continue;
                }

                slot.LastSearchTime = DateTime.UtcNow;
                _movieEditionSlotService.Update(slot);

                var processDecisions = await _processDownloadDecisions.ProcessDecisions(decisions);
                downloadedCount += processDecisions.Grabbed.Count;
            }

            _logger.ProgressInfo("Completed edition search for {0} slot(s). {1} reports downloaded.", slots.Count, downloadedCount);
        }

        private async Task SearchMissingEditionSlots(HashSet<int> queuedEditionSlotIds, bool userInvokedSearch)
        {
            var missingSlots = _movieEditionSlotService.GetMonitoredMissingSlots();
            if (!missingSlots.Any())
            {
                return;
            }

            var eligibleSlots = missingSlots.Where(s => !queuedEditionSlotIds.Contains(s.Id)).ToList();
            if (!eligibleSlots.Any())
            {
                return;
            }

            var movieIds = eligibleSlots.Select(s => s.MovieId).Distinct().ToList();
            var moviesById = _movieService.GetMovies(movieIds).ToDictionary(m => m.Id);

            foreach (var group in eligibleSlots.GroupBy(s => s.MovieId))
            {
                if (!moviesById.TryGetValue(group.Key, out var movie) || !movie.Monitored)
                {
                    continue;
                }

                await SearchForEditionSlots(movie, group.ToList(), userInvokedSearch);
            }
        }

        private async Task SearchForBulkMovies(List<Movie> movies, bool userInvokedSearch)
        {
            _logger.ProgressInfo("Performing search for {0} movies", movies.Count);
            var downloadedCount = 0;

            foreach (var movieId in movies.GroupBy(e => e.Id).OrderBy(g => g.Min(m => m.LastSearchTime ?? DateTime.MinValue)))
            {
                List<DownloadDecision> decisions;

                try
                {
                    decisions = await _releaseSearchService.MovieSearch(movieId.Key, userInvokedSearch, false);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unable to search for movie: [{0}]", movieId.Key);
                    continue;
                }

                var processDecisions = await _processDownloadDecisions.ProcessDecisions(decisions);

                downloadedCount += processDecisions.Grabbed.Count;
            }

            _logger.ProgressInfo("Completed search for {0} movies. {1} reports downloaded.", movies.Count, downloadedCount);
        }
    }
}
