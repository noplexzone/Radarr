using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.SignalR;
using Radarr.Http;
using Radarr.Http.REST;
using Radarr.Http.REST.Attributes;
using BadRequestException = Radarr.Http.REST.BadRequestException;

namespace Radarr.Api.V3.MovieEditions
{
    [V3ApiController]
    public class MovieEditionController : RestControllerWithSignalR<MovieEditionResource, MovieEdition>
    {
        private readonly IMovieEditionService _movieEditionService;
        private readonly IMovieService _movieService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IDeleteMediaFiles _mediaFileDeletionService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IUpgradableSpecification _upgradableSpecification;
        private readonly ICustomFormatCalculationService _formatCalculator;

        public MovieEditionController(IBroadcastSignalRMessage signalRBroadcaster,
                                      IMovieEditionService movieEditionService,
                                      IMovieService movieService,
                                      IMediaFileService mediaFileService,
                                      IDeleteMediaFiles mediaFileDeletionService,
                                      IManageCommandQueue commandQueueManager,
                                      IUpgradableSpecification upgradableSpecification,
                                      ICustomFormatCalculationService formatCalculator)
            : base(signalRBroadcaster)
        {
            _movieEditionService = movieEditionService;
            _movieService = movieService;
            _mediaFileService = mediaFileService;
            _mediaFileDeletionService = mediaFileDeletionService;
            _commandQueueManager = commandQueueManager;
            _upgradableSpecification = upgradableSpecification;
            _formatCalculator = formatCalculator;

            SharedValidator.RuleFor(e => e.MovieId).GreaterThan(0);
            PostValidator.RuleFor(e => e.Title).NotEmpty();
        }

        protected override MovieEditionResource GetResourceById(int id)
        {
            var edition = _movieEditionService.GetEdition(id);

            return GetEditions(edition.MovieId).Single(e => e.Id == id);
        }

        [HttpGet]
        [Produces("application/json")]
        public List<MovieEditionResource> GetMovieEditions([FromQuery] int movieId)
        {
            if (movieId <= 0)
            {
                throw new BadRequestException("movieId must be provided");
            }

            return GetEditions(movieId);
        }

        [RestPostById]
        [Consumes("application/json")]
        public ActionResult<MovieEditionResource> AddEdition([FromBody] MovieEditionResource resource)
        {
            var movie = _movieService.GetMovie(resource.MovieId);
            var edition = MovieEdition.CreateNew(movie.Id, resource.Title, resource.Monitored);
            edition.QualityProfileId = resource.QualityProfileId;

            try
            {
                edition = _movieEditionService.AddEdition(edition);
            }
            catch (MovieEditionExistsException ex)
            {
                throw new BadRequestException(ex.Message);
            }

            if (resource.SearchForEdition && movie.Monitored && edition.Monitored)
            {
                _commandQueueManager.Push(new MovieEditionSearchCommand { MovieId = movie.Id, MovieEditionId = edition.Id }, trigger: CommandTrigger.Manual);
            }

            return Created(edition.Id);
        }

        [RestPutById]
        [Consumes("application/json")]
        public ActionResult<MovieEditionResource> UpdateEdition([FromBody] MovieEditionResource resource)
        {
            var stored = _movieEditionService.GetEdition(resource.Id);
            var edition = resource.ToModel();
            edition.MovieId = stored.MovieId;

            // The default edition shows as "Standard" until it's given a name of its own
            if (stored.IsDefault && edition.Title == MovieEdition.DefaultTitle && stored.Title == null)
            {
                edition.Title = null;
            }

            try
            {
                _movieEditionService.UpdateEdition(edition);
            }
            catch (MovieEditionExistsException ex)
            {
                throw new BadRequestException(ex.Message);
            }

            return Accepted(resource.Id);
        }

        [RestDeleteById]
        public void DeleteEdition(int id, [FromQuery] bool deleteFiles = false)
        {
            var edition = _movieEditionService.GetEdition(id);

            if (edition.IsDefault)
            {
                throw new BadRequestException("The default edition can't be removed, only renamed");
            }

            if (deleteFiles)
            {
                var movie = _movieService.GetMovie(edition.MovieId);
                var files = _mediaFileService.GetFilesByMovie(movie.Id).Where(f => f.MovieEditionId == edition.Id).ToList();

                foreach (var file in files)
                {
                    _mediaFileDeletionService.DeleteMovieFile(movie, file);
                }
            }

            _movieEditionService.DeleteEdition(edition);
        }

        private List<MovieEditionResource> GetEditions(int movieId)
        {
            var movie = _movieService.GetMovie(movieId);
            var editions = _movieEditionService.GetEditions(movieId, true);

            return editions.OrderByDescending(e => e.IsDefault)
                .ThenBy(e => e.Added)
                .Select(e => e.ToResource(movie, _movieEditionService.GetCurrentFile(movie, e, e.Files), _upgradableSpecification, _formatCalculator))
                .ToList();
        }
    }
}
