using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Profiles.Qualities;
using Radarr.Api.V3.CustomFormats;
using Radarr.Http;
using Radarr.Http.REST;
using Radarr.Http.REST.Attributes;
using HttpStatusCode = System.Net.HttpStatusCode;

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
        private readonly IManageCommandQueue _commandQueueManager;

        public MovieEditionSlotController(
            IMovieEditionSlotService slotService,
            IMovieFileAssignmentService fileAssignmentService,
            IMediaFileService mediaFileService,
            IMovieService movieService,
            IQualityProfileService qualityProfileService,
            ICustomFormatCalculationService formatCalculationService,
            IManageCommandQueue commandQueueManager)
        {
            _slotService = slotService;
            _fileAssignmentService = fileAssignmentService;
            _mediaFileService = mediaFileService;
            _movieService = movieService;
            _qualityProfileService = qualityProfileService;
            _formatCalculationService = formatCalculationService;
            _commandQueueManager = commandQueueManager;
        }

        protected override MovieEditionSlotResource GetResourceById(int id)
        {
            var slot = _slotService.GetById(id);
            var movie = _movieService.GetMovie(slot.MovieId);
            var file = _fileAssignmentService.GetEditionFile(id);
            return BuildResource(movie, slot, file);
        }

        [HttpGet]
        public ActionResult<List<MovieEditionSlotResource>> GetSlots([FromQuery] int movieId)
        {
            if (movieId <= 0)
            {
                return BadRequest("movieId query parameter is required and must be a positive integer.");
            }

            var movie = _movieService.GetMovie(movieId);
            var slots = _slotService.GetForMovie(movieId);
            var filesBySlotId = _mediaFileService.GetFilesByMovie(movieId)
                .Where(f => f.MovieEditionSlotId.HasValue)
                .ToDictionary(f => f.MovieEditionSlotId.Value);

            return slots.Select(slot => BuildResource(movie, slot, filesBySlotId.GetValueOrDefault(slot.Id))).ToList();
        }

        [RestPostById]
        [Consumes("application/json")]
        public ActionResult<MovieEditionSlotResource> Create([FromBody] CreateMovieEditionRequest request)
        {
            if (request == null || request.MovieId <= 0)
            {
                return BadRequest("movieId is required and must be a positive integer.");
            }

            ValidateRequest(request.EditionName, request.SearchTerm, request.Aliases, request.QualityProfileId);
            _movieService.GetMovie(request.MovieId);

            var slot = _slotService.Add(request.ToModel());
            return Created(slot.Id);
        }

        [RestPutById]
        [Consumes("application/json")]
        public ActionResult<MovieEditionSlotResource> Update(int id, [FromBody] UpdateMovieEditionRequest request)
        {
            if (request == null)
            {
                return BadRequest("Request body is required.");
            }

            ValidateRequest(request.EditionName, request.SearchTerm, request.Aliases, request.QualityProfileId);

            var persisted = _slotService.GetById(id);
            var slot = request.ToModel(persisted);
            _slotService.Update(slot);
            return Accepted(id);
        }

        [HttpPost("{id:int}/search")]
        public ActionResult SearchEdition(int id)
        {
            var slot = _slotService.GetById(id);
            _commandQueueManager.Push(new MovieEditionSearchCommand
            {
                MovieId = slot.MovieId,
                MovieEditionSlotId = id
            }, CommandPriority.Normal, CommandTrigger.Manual);

            return Accepted();
        }

        [HttpPost("searchmissing")]
        public ActionResult SearchMissingMonitoredEditions()
        {
            _commandQueueManager.Push(new MissingEditionSlotsSearchCommand(), CommandPriority.Normal, CommandTrigger.Manual);
            return Accepted();
        }

        [HttpPost("searchcutoffunmet")]
        public ActionResult SearchCutoffUnmetEditions()
        {
            _commandQueueManager.Push(new CutoffUnmetEditionSlotsSearchCommand(), CommandPriority.Normal, CommandTrigger.Manual);
            return Accepted();
        }

        [HttpPost("{id:int}/assignfile")]
        [Consumes("application/json")]
        public ActionResult<MovieEditionSlotResource> AssignFile(int id, [FromBody] AssignMovieEditionFileRequest request)
        {
            ValidateId(id);
            if (request == null || request.MovieId <= 0 || request.MovieFileId <= 0)
            {
                return BadRequest("movieId and movieFileId are required and must be positive integers.");
            }

            ExecuteAssignmentAction(() => _fileAssignmentService.AssignFileToEdition(request.MovieId, request.MovieFileId, id));
            return Accepted(id);
        }

        [HttpPost("{id:int}/unassignfile")]
        public ActionResult<MovieEditionSlotResource> UnassignFile(int id)
        {
            ValidateId(id);
            ExecuteAssignmentAction(() => _fileAssignmentService.UnassignEditionFile(id));
            return Accepted(id);
        }

        [HttpPost("{id:int}/converttomain")]
        [Consumes("application/json")]
        public ActionResult ConvertToMain(int id, [FromBody] ConvertMovieEditionFileToMainRequest request)
        {
            ValidateId(id);
            if (request?.ExistingMainFileAction == null)
            {
                return BadRequest("existingMainFileAction is required.");
            }

            ExecuteAssignmentAction(() => _fileAssignmentService.RemoveEdition(id, AttachedEditionFileAction.ConvertToMain, request.ExistingMainFileAction.Value));
            return Accepted();
        }

        [RestDeleteById]
        public void Delete(int id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RemoveMovieEditionRequest request)
        {
            var action = request?.AttachedFileAction ?? AttachedEditionFileAction.KeepUnassigned;
            ExecuteAssignmentAction(() => _fileAssignmentService.RemoveEdition(id, action, request?.ExistingMainFileAction));
        }

        private static void ExecuteAssignmentAction(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, ex.Message);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, ex.Message);
            }
        }

        private void ValidateRequest(string editionName, string searchTerm, List<string> aliases, int? qualityProfileId)
        {
            var failures = new List<ValidationFailure>();
            if (string.IsNullOrWhiteSpace(editionName))
            {
                failures.Add(new ValidationFailure("EditionName", "Edition name is required."));
            }

            if (qualityProfileId.HasValue)
            {
                _qualityProfileService.Get(qualityProfileId.Value);
            }

            if (failures.Any())
            {
                throw new ValidationException(failures);
            }
        }

        private MovieEditionSlotResource BuildResource(Movie movie, MovieEditionSlot slot, MovieFile file)
        {
            var resource = slot.ToResource();
            var effectiveProfile = slot.QualityProfileId.HasValue
                ? _qualityProfileService.Get(slot.QualityProfileId.Value)
                : movie.QualityProfile;

            resource.EffectiveQualityProfileId = effectiveProfile.Id;
            resource.EffectiveQualityProfileName = effectiveProfile.Name;
            resource.QualityProfileInherited = !slot.QualityProfileId.HasValue;
            resource.EffectiveMinimumCustomFormatScore = slot.MinimumCustomFormatScore ?? effectiveProfile.MinFormatScore;
            resource.MinimumCustomFormatScoreInherited = !slot.MinimumCustomFormatScore.HasValue;
            resource.Status = file == null ? "missing" : "assigned";

            if (file == null)
            {
                return resource;
            }

            file.Movie = movie;
            var customFormats = _formatCalculationService.ParseCustomFormat(file, movie);
            var customFormatScore = effectiveProfile.CalculateCustomFormatScore(customFormats);

            resource.MovieFileId = file.Id;
            resource.MovieFileQuality = file.Quality;
            resource.MovieFileCustomFormatScore = customFormatScore;
            resource.MovieFileCustomFormats = customFormats.ToResource(false);
            resource.MovieFile = new MovieEditionSlotFileResource
            {
                Id = file.Id,
                RelativePath = file.RelativePath,
                Size = file.Size,
                Quality = file.Quality,
                Languages = file.Languages,
                CustomFormats = customFormats.ToResource(false),
                CustomFormatScore = customFormatScore,
                DateAdded = file.DateAdded
            };

            return resource;
        }
    }
}
