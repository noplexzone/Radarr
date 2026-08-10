using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Profiles.Qualities;
using Radarr.Api.V3.CustomFormats;
using Radarr.Http;
using Radarr.Http.REST;
using Radarr.Http.REST.Attributes;

namespace Radarr.Api.V3.Movies
{
    [V3ApiController("movieeditionslot")]
    public class MovieEditionSlotController : RestController<MovieEditionSlotResource>
    {
        private readonly IMovieEditionSlotService _slotService;
        private readonly IMovieFileAssignmentService _fileAssignmentService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMovieService _movieService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly ICustomFormatCalculationService _formatCalculationService;

        public MovieEditionSlotController(
            IMovieEditionSlotService slotService,
            IMovieFileAssignmentService fileAssignmentService,
            IMediaFileService mediaFileService,
            IMovieService movieService,
            IQualityProfileService qualityProfileService,
            ICustomFormatCalculationService formatCalculationService)
        {
            _slotService = slotService;
            _fileAssignmentService = fileAssignmentService;
            _mediaFileService = mediaFileService;
            _movieService = movieService;
            _qualityProfileService = qualityProfileService;
            _formatCalculationService = formatCalculationService;

            SharedValidator.RuleFor(r => r.MovieId).GreaterThan(0);
            SharedValidator.RuleFor(r => r.EditionName).NotEmpty();
        }

        protected override MovieEditionSlotResource GetResourceById(int id)
        {
            return _slotService.GetById(id).ToResource();
        }

        [HttpGet]
        public ActionResult<List<MovieEditionSlotResource>> GetSlots([FromQuery] int movieId)
        {
            if (movieId <= 0)
            {
                return BadRequest("movieId query parameter is required and must be a positive integer.");
            }

            var slots = _slotService.GetForMovie(movieId);
            var resources = slots.Select(s => s.ToResource()).ToList();

            var filesBySlotId = _mediaFileService.GetFilesByMovie(movieId)
                .Where(f => f.MovieEditionSlotId.HasValue)
                .ToDictionary(f => f.MovieEditionSlotId.Value);

            if (filesBySlotId.Count == 0)
            {
                return resources;
            }

            var movie = _movieService.GetMovie(movieId);

            var resourcesById = resources.ToDictionary(r => r.Id);

            foreach (var slot in slots)
            {
                if (!filesBySlotId.TryGetValue(slot.Id, out var file))
                {
                    continue;
                }

                if (!resourcesById.TryGetValue(slot.Id, out var resource))
                {
                    continue;
                }

                resource.MovieFileId = file.Id;
                resource.MovieFileQuality = file.Quality;

                var effectiveProfile = slot.QualityProfileId.HasValue
                    ? _qualityProfileService.Get(slot.QualityProfileId.Value)
                    : movie.QualityProfile;

                file.Movie = movie;
                var customFormats = _formatCalculationService.ParseCustomFormat(file, movie);
                resource.MovieFileCustomFormatScore = effectiveProfile.CalculateCustomFormatScore(customFormats);
                resource.MovieFileCustomFormats = customFormats.ToResource(false);
            }

            return resources;
        }

        [RestPostById]
        public ActionResult<MovieEditionSlotResource> Create([FromBody] MovieEditionSlotResource resource)
        {
            var slot = _slotService.Add(resource.ToModel());
            return Created(slot.Id);
        }

        [RestPutById]
        public ActionResult<MovieEditionSlotResource> Update([FromBody] MovieEditionSlotResource resource)
        {
            _slotService.Update(resource.ToModel());
            return Accepted(resource.Id);
        }

        [RestDeleteById]
        public void Delete(int id)
        {
            _fileAssignmentService.RemoveEdition(id, AttachedEditionFileAction.KeepUnassigned);
        }
    }
}
