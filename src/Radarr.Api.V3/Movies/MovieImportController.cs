using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Movies;
using Radarr.Http;
using Radarr.Http.REST;

namespace Radarr.Api.V3.Movies
{
    [V3ApiController("movie/import")]
    public class MovieImportController : RestController<MovieResource>
    {
        private readonly IAddMovieService _addMovieService;
        private readonly IConfigService _configService;

        public MovieImportController(IAddMovieService addMovieService, IConfigService configService)
        {
            _addMovieService = addMovieService;
            _configService = configService;
        }

        [NonAction]
        public override ActionResult<MovieResource> GetResourceByIdWithErrorHandler(int id)
        {
            throw new NotImplementedException();
        }

        protected override MovieResource GetResourceById(int id)
        {
            throw new NotImplementedException();
        }

        [HttpPost]
        [Consumes("application/json")]
        [Produces("application/json")]
        public IEnumerable<MovieResource> Import([FromBody] List<MovieResource> resource)
        {
            var newMovies = resource.ToModel();

            for (var i = 0; i < newMovies.Count; i++)
            {
                newMovies[i].AutoAddEditions = resource[i].AutoAddEditions ?? _configService.AutoAddEditionsDefault;
            }

            return _addMovieService.AddMovies(newMovies).ToResource(0);
        }
    }
}
