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
using NzbDrone.Core.Movies.Editions;
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
        private readonly IMovieEditionService _movieEditionService;
        private readonly Logger _logger;

        public MovieSearchService(IMovieService movieService,
                                   IMovieCutoffService movieCutoffService,
                                   ISearchForReleases releaseSearchService,
                                   IProcessDownloadDecisions processDownloadDecisions,
                                   IQueueService queueService,
                                   IMovieEditionService movieEditionService,
                                   Logger logger)
        {
            _movieService = movieService;
            _movieCutoffService = movieCutoffService;
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _queueService = queueService;
            _movieEditionService = movieEditionService;
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

            // Movies that have a file but miss another monitored edition, the queue is checked per edition when releases are evaluated
            var missingEditionMovieIds = _movieEditionService.GetMovieIdsWithMissingEditions().Except(movies.Select(m => m.Id)).ToList();

            if (missingEditionMovieIds.Any())
            {
                missing.AddRange(_movieService.GetMovies(missingEditionMovieIds).Where(m => m.Monitored && m.IsAvailable()));
            }

            SearchForBulkMovies(missing, message.Trigger == CommandTrigger.Manual).GetAwaiter().GetResult();
        }

        public void Execute(MovieEditionSearchCommand message)
        {
            var edition = _movieEditionService.GetEditions(message.MovieId).FirstOrDefault(e => e.Id == message.MovieEditionId);

            if (edition == null)
            {
                _logger.Warn("Edition {0} of movie {1} wasn't found, not searching", message.MovieEditionId, message.MovieId);
                return;
            }

            var userInvokedSearch = message.Trigger == CommandTrigger.Manual;
            var decisions = _releaseSearchService.MovieEditionSearch(message.MovieId, edition.Id, userInvokedSearch, false).GetAwaiter().GetResult();
            var processed = _processDownloadDecisions.ProcessDecisions(decisions).GetAwaiter().GetResult();

            _logger.ProgressInfo("Edition search completed. {0} reports downloaded.", processed.Grabbed.Count);
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

        private async Task SearchForBulkMovies(List<Movie> movies, bool userInvokedSearch)
        {
            _logger.ProgressInfo("Performing search for {0} movies", movies.Count);
            var downloadedCount = 0;
            var newEditionCount = 0;

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

                // Grabbing a release for a new edition creates it, so note which editions were new beforehand
                var newEditions = decisions
                    .Where(d => d.RemoteMovie.MovieEdition is { IsNew: true })
                    .GroupBy(d => d.RemoteMovie.MovieEdition.Key)
                    .Select(g => (Title: g.First().RemoteMovie.MovieEdition.DisplayTitle, Decisions: g.ToList()))
                    .ToList();

                var processDecisions = await _processDownloadDecisions.ProcessDecisions(decisions);

                downloadedCount += processDecisions.Grabbed.Count;
                newEditionCount += ReportEditionDiscovery(movieId.First(), decisions, newEditions, processDecisions, userInvokedSearch && movies.Count == 1);
            }

            if (newEditionCount > 0)
            {
                _logger.ProgressInfo("Completed search for {0} movies. {1} reports downloaded. {2} new editions found.", movies.Count, downloadedCount, newEditionCount);
            }
            else
            {
                _logger.ProgressInfo("Completed search for {0} movies. {1} reports downloaded.", movies.Count, downloadedCount);
            }
        }

        // Says which editions a movie doesn't have yet were found in the results and what happened to them,
        // so it's visible in System > Events why an edition was or wasn't added
        private int ReportEditionDiscovery(Movie movie, List<DownloadDecision> decisions, List<(string Title, List<DownloadDecision> Decisions)> newEditions, ProcessedDecisions processed, bool reportNothingFound)
        {
            var name = $"{movie.Title} ({movie.Year})";

            foreach (var (title, editionDecisions) in newEditions)
            {
                var grabbed = editionDecisions.FirstOrDefault(d => processed.Grabbed.Contains(d));

                if (grabbed != null)
                {
                    _logger.Info("New edition '{0}' found for {1} in {2}, added and grabbed {3}", title, name, Releases(editionDecisions.Count), grabbed.RemoteMovie.Release.Title);
                    continue;
                }

                var rejections = editionDecisions.SelectMany(d => d.Rejections).ToList();

                if (rejections.Any(r => r.Reason == DownloadRejectionReason.EditionNotTracked))
                {
                    _logger.Info("New edition '{0}' found for {1} in {2}, not added because Auto-add new editions is off for this movie", title, name, Releases(editionDecisions.Count));
                    continue;
                }

                var reasons = rejections.Select(r => r.Message).Distinct().Take(3);
                _logger.Info("New edition '{0}' found for {1} in {2}, not added: {3}", title, name, Releases(editionDecisions.Count), string.Join("; ", reasons));
            }

            var unclear = decisions.Count(d => d.RemoteMovie.MovieEdition == null && d.RemoteMovie.MovieEditionMatch is { IsResolved: false });

            if (unclear > 0)
            {
                _logger.Info("{0} for {1} skipped because their edition couldn't be determined", Releases(unclear), name);
            }

            if (newEditions.Count == 0 && reportNothingFound)
            {
                _logger.Info("No new editions found for {0} in {1}", name, Releases(decisions.Count));
            }

            return newEditions.Count;
        }

        private static string Releases(int count)
        {
            return count == 1 ? "1 release" : $"{count} releases";
        }
    }
}
