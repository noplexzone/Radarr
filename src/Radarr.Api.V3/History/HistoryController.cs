using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using Radarr.Api.V3.Movies;
using Radarr.Http;
using Radarr.Http.Extensions;

namespace Radarr.Api.V3.History
{
    [V3ApiController]
    public class HistoryController : Controller
    {
        private readonly IHistoryService _historyService;
        private readonly IMovieService _movieService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IUpgradableSpecification _upgradableSpecification;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IMovieEditionSlotService _movieEditionSlotService;

        public HistoryController(IHistoryService historyService,
                             IMovieService movieService,
                             ICustomFormatCalculationService formatCalculator,
                             IUpgradableSpecification upgradableSpecification,
                             IFailedDownloadService failedDownloadService,
                             IMovieEditionSlotService movieEditionSlotService)
        {
            _historyService = historyService;
            _movieService = movieService;
            _formatCalculator = formatCalculator;
            _upgradableSpecification = upgradableSpecification;
            _failedDownloadService = failedDownloadService;
            _movieEditionSlotService = movieEditionSlotService;
        }

        protected HistoryResource MapToResource(MovieHistory model, bool includeMovie)
        {
            if (model.Movie == null)
            {
                model.Movie = _movieService.GetMovie(model.MovieId);
            }

            var resource = model.ToResource(_formatCalculator);

            if (includeMovie)
            {
                resource.Movie = model.Movie.ToResource(0);
            }

            if (model.Movie != null)
            {
                resource.QualityCutoffNotMet = _upgradableSpecification.QualityCutoffNotMet(model.Movie.QualityProfile, model.Quality);
            }

            return resource;
        }

        [HttpGet]
        [Produces("application/json")]
        public PagingResource<HistoryResource> GetHistory([FromQuery] PagingRequestResource paging, bool includeMovie, [FromQuery(Name = "eventType")] int[] eventTypes, string downloadId, [FromQuery] int[] movieIds = null, [FromQuery] int[] languages = null, [FromQuery] int[] quality = null)
        {
            var pagingResource = new PagingResource<HistoryResource>(paging);
            var pagingSpec = pagingResource.MapToPagingSpec<HistoryResource, MovieHistory>(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "date",
                    "languages",
                    "movieMetadata.sortTitle",
                    "quality"
                },
                "date",
                SortDirection.Descending);

            if (eventTypes != null && eventTypes.Any())
            {
                 pagingSpec.FilterExpressions.Add(v => eventTypes.Contains((int)v.EventType));
            }

            if (downloadId.IsNotNullOrWhiteSpace())
            {
                pagingSpec.FilterExpressions.Add(h => h.DownloadId == downloadId);
            }

            if (movieIds != null && movieIds.Any())
            {
                pagingSpec.FilterExpressions.Add(h => movieIds.Contains(h.MovieId));
            }

            var page = pagingSpec.ApplyToPage(h => _historyService.Paged(pagingSpec, languages, quality), h => MapToResource(h, includeMovie));
            EnrichMovieEditionSlotNames(page.Records);

            return page;
        }

        [HttpGet("since")]
        [Produces("application/json")]
        public List<HistoryResource> GetHistorySince(DateTime date, MovieHistoryEventType? eventType = null, bool includeMovie = false)
        {
            var resources = _historyService.Since(date, eventType).Select(h => MapToResource(h, includeMovie)).ToList();
            EnrichMovieEditionSlotNames(resources);

            return resources;
        }

        [HttpGet("movie")]
        [Produces("application/json")]
        public List<HistoryResource> GetMovieHistory(int movieId, MovieHistoryEventType? eventType = null, bool includeMovie = false)
        {
            var resources = _historyService.GetByMovieId(movieId, eventType).Select(h => MapToResource(h, includeMovie)).ToList();
            EnrichMovieEditionSlotNames(resources);

            return resources;
        }

        private void EnrichMovieEditionSlotNames(List<HistoryResource> resources)
        {
            var slotIds = resources
                .Where(r => r.MovieEditionSlotId.HasValue)
                .Select(r => r.MovieEditionSlotId.Value)
                .Distinct()
                .ToList();

            if (!slotIds.Any())
            {
                return;
            }

            var slotsById = _movieEditionSlotService.GetByIds(slotIds);

            foreach (var resource in resources.Where(r => r.MovieEditionSlotId.HasValue))
            {
                resource.MovieEditionSlotName = slotsById.TryGetValue(resource.MovieEditionSlotId.Value, out var slot)
                    ? slot.EditionName
                    : $"Edition slot #{resource.MovieEditionSlotId.Value}";
            }
        }

        [HttpPost("failed/{id}")]
        public object MarkAsFailed([FromRoute] int id)
        {
            _failedDownloadService.MarkAsFailed(id);
            return new { };
        }
    }
}
