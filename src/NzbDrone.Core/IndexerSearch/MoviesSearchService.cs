using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.IndexerSearch
{
    public class MovieSearchService : IExecute<MoviesSearchCommand>, IExecute<MissingMoviesSearchCommand>, IExecute<CutoffUnmetMoviesSearchCommand>, IExecute<MovieEditionSearchCommand>
    {
        private readonly IMovieService _movieService;
        private readonly IMovieCutoffService _movieCutoffService;
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly IQueueService _queueService;
        private readonly IMovieEditionSlotService _movieEditionSlotService;
        private readonly Logger _logger;

        public MovieSearchService(IMovieService movieService,
                                   IMovieCutoffService movieCutoffService,
                                   ISearchForReleases releaseSearchService,
                                   IProcessDownloadDecisions processDownloadDecisions,
                                   IQueueService queueService,
                                   IMovieEditionSlotService movieEditionSlotService,
                                   Logger logger)
        {
            _movieService = movieService;
            _movieCutoffService = movieCutoffService;
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _queueService = queueService;
            _movieEditionSlotService = movieEditionSlotService;
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

            var queue = _queueService.GetQueue().Where(q => q.Movie != null).Select(q => q.Movie.Id);
            var missing = movies.Where(e => !queue.Contains(e.Id)).ToList();

            SearchForBulkMovies(missing, message.Trigger == CommandTrigger.Manual).GetAwaiter().GetResult();
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

            var queue = _queueService.GetQueue().Where(q => q.Movie != null).Select(q => q.Movie.Id);
            var missing = movies.Where(e => !queue.Contains(e.Id)).ToList();

            SearchForBulkMovies(missing, message.Trigger == CommandTrigger.Manual).GetAwaiter().GetResult();
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
                slots = allSlots
                    .Where(s => (s.Monitored || userInvokedSearch) && !s.MovieFileId.HasValue)
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
