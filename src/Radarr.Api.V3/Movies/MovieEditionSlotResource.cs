using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Qualities;
using Radarr.Api.V3.CustomFormats;
using Radarr.Http.REST;

namespace Radarr.Api.V3.Movies
{
    public class MovieEditionSlotResource : RestResource
    {
        public int MovieId { get; set; }
        public string EditionName { get; set; }
        public string SearchTerm { get; set; }
        public bool Monitored { get; set; }
        public int? MovieFileId { get; set; }
        public int? QualityProfileId { get; set; }
        public int? MinimumCustomFormatScore { get; set; }
        public DateTime? LastSearchTime { get; set; }
        public DateTime DateAdded { get; set; }

        // Enriched file data — populated when MovieFileId is set
        public QualityModel MovieFileQuality { get; set; }
        public int? MovieFileCustomFormatScore { get; set; }
        public List<CustomFormatResource> MovieFileCustomFormats { get; set; }
    }

    public static class MovieEditionSlotResourceMapper
    {
        public static MovieEditionSlotResource ToResource(this MovieEditionSlot model)
        {
            if (model == null)
            {
                return null;
            }

            return new MovieEditionSlotResource
            {
                Id = model.Id,
                MovieId = model.MovieId,
                EditionName = model.EditionName,
                SearchTerm = model.SearchTerm,
                Monitored = model.Monitored,
                MovieFileId = model.MovieFileId,
                QualityProfileId = model.QualityProfileId,
                MinimumCustomFormatScore = model.MinimumCustomFormatScore,
                LastSearchTime = model.LastSearchTime,
                DateAdded = model.DateAdded,
            };
        }

        public static MovieEditionSlot ToModel(this MovieEditionSlotResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            return new MovieEditionSlot
            {
                Id = resource.Id,
                MovieId = resource.MovieId,
                EditionName = resource.EditionName?.Trim() ?? string.Empty,
                SearchTerm = string.IsNullOrWhiteSpace(resource.SearchTerm) ? null : resource.SearchTerm.Trim(),
                Monitored = resource.Monitored,
                MovieFileId = resource.MovieFileId,
                QualityProfileId = resource.QualityProfileId,
                MinimumCustomFormatScore = resource.MinimumCustomFormatScore,
                LastSearchTime = resource.LastSearchTime,
                DateAdded = resource.DateAdded,
            };
        }

        public static List<MovieEditionSlotResource> ToResource(this IEnumerable<MovieEditionSlot> models)
        {
            return models.Select(ToResource).ToList();
        }
    }
}
