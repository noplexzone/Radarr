using System.Collections.Generic;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Movies.MovieEditionSlots;
using Radarr.Http;
using Radarr.Http.REST;
using Radarr.Http.REST.Attributes;

namespace Radarr.Api.V3.Movies
{
    [V3ApiController("movieeditionslot")]
    public class MovieEditionSlotController : RestController<MovieEditionSlotResource>
    {
        private readonly IMovieEditionSlotService _slotService;

        public MovieEditionSlotController(IMovieEditionSlotService slotService)
        {
            _slotService = slotService;

            SharedValidator.RuleFor(r => r.MovieId).GreaterThan(0);
            SharedValidator.RuleFor(r => r.EditionName).NotEmpty();
        }

        protected override MovieEditionSlotResource GetResourceById(int id)
        {
            return _slotService.GetById(id).ToResource();
        }

        [HttpGet]
        public List<MovieEditionSlotResource> GetSlots([FromQuery] int movieId)
        {
            return _slotService.GetForMovie(movieId).ToResource();
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
            _slotService.Delete(id);
        }
    }
}
