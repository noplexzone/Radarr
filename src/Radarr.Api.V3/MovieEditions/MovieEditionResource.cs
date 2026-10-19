using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Editions;
using Radarr.Api.V3.MovieFiles;
using Radarr.Http.REST;
using Swashbuckle.AspNetCore.Annotations;

namespace Radarr.Api.V3.MovieEditions
{
    public class MovieEditionResource : RestResource
    {
        public int MovieId { get; set; }
        public string Title { get; set; }
        public bool IsDefault { get; set; }
        public bool Monitored { get; set; }

        // 0 uses the movie's quality profile
        public int QualityProfileId { get; set; }
        public DateTime Added { get; set; }
        public DateTime? LastSearchTime { get; set; }

        public bool HasFile { get; set; }
        public int MovieFileId { get; set; }
        public MovieFileResource MovieFile { get; set; }
        public int FileCount { get; set; }
        public bool IsDuplicate { get; set; }

        // Only used when adding an edition
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        [SwaggerIgnore]
        public bool SearchForEdition { get; set; }
    }

    // A short form of an edition, embedded in movies
    public class MovieEditionSummaryResource
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public bool IsDefault { get; set; }
        public bool Monitored { get; set; }
        public bool HasFile { get; set; }
        public int MovieFileId { get; set; }
    }

    public static class MovieEditionResourceMapper
    {
        public static MovieEditionResource ToResource(this MovieEdition model, Movie movie, MovieFile currentFile, IUpgradableSpecification upgradableSpecification, ICustomFormatCalculationService formatCalculationService)
        {
            if (model == null)
            {
                return null;
            }

            return new MovieEditionResource
            {
                Id = model.Id,
                MovieId = model.MovieId,
                Title = model.DisplayTitle,
                IsDefault = model.IsDefault,
                Monitored = model.Monitored,
                QualityProfileId = model.QualityProfileId,
                Added = model.Added,
                LastSearchTime = model.LastSearchTime,
                HasFile = currentFile != null,
                MovieFileId = currentFile?.Id ?? 0,
                MovieFile = currentFile?.ToResource(movie, upgradableSpecification, formatCalculationService),
                FileCount = model.Files.Count,
                IsDuplicate = model.Files.Count > 1
            };
        }

        public static MovieEditionSummaryResource ToSummaryResource(this MovieEdition model, MovieFile currentFile)
        {
            return new MovieEditionSummaryResource
            {
                Id = model.Id,
                Title = model.DisplayTitle,
                IsDefault = model.IsDefault,
                Monitored = model.Monitored,
                HasFile = currentFile != null,
                MovieFileId = currentFile?.Id ?? 0
            };
        }

        public static MovieEdition ToModel(this MovieEditionResource resource)
        {
            return new MovieEdition
            {
                Id = resource.Id,
                MovieId = resource.MovieId,
                Title = resource.Title,
                Monitored = resource.Monitored,
                QualityProfileId = resource.QualityProfileId
            };
        }

        public static List<MovieEditionSummaryResource> ToSummaryResource(this IEnumerable<MovieEdition> editions, Func<MovieEdition, MovieFile> currentFile)
        {
            return editions.OrderByDescending(e => e.IsDefault).ThenBy(e => e.Added).Select(e => e.ToSummaryResource(currentFile(e))).ToList();
        }
    }
}
