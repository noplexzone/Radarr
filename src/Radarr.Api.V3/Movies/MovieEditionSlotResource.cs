using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Languages;
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
        public List<string> Aliases { get; set; }
        public bool Monitored { get; set; }
        public int? MovieFileId { get; set; }
        public int? QualityProfileId { get; set; }
        public int EffectiveQualityProfileId { get; set; }
        public string EffectiveQualityProfileName { get; set; }
        public bool QualityProfileInherited { get; set; }
        public int? MinimumCustomFormatScore { get; set; }
        public int EffectiveMinimumCustomFormatScore { get; set; }
        public bool MinimumCustomFormatScoreInherited { get; set; }
        public DateTime? LastSearchTime { get; set; }
        public DateTime DateAdded { get; set; }
        public string Status { get; set; }
        public MovieEditionSlotFileResource MovieFile { get; set; }

        // Legacy GET compatibility. Do not accept these fields for writes.
        public QualityModel MovieFileQuality { get; set; }
        public int? MovieFileCustomFormatScore { get; set; }
        public List<CustomFormatResource> MovieFileCustomFormats { get; set; }
    }

    public class MovieEditionSlotFileResource
    {
        public int Id { get; set; }
        public string RelativePath { get; set; }
        public long Size { get; set; }
        public QualityModel Quality { get; set; }
        public List<Language> Languages { get; set; }
        public List<CustomFormatResource> CustomFormats { get; set; }
        public int CustomFormatScore { get; set; }
        public DateTime DateAdded { get; set; }
    }

    public class CreateMovieEditionRequest
    {
        public int MovieId { get; set; }
        public string EditionName { get; set; }
        public string SearchTerm { get; set; }
        public List<string> Aliases { get; set; }
        public bool Monitored { get; set; }
        public int? QualityProfileId { get; set; }
        public int? MinimumCustomFormatScore { get; set; }
    }

    public class UpdateMovieEditionRequest
    {
        public string EditionName { get; set; }
        public string SearchTerm { get; set; }
        public List<string> Aliases { get; set; }
        public bool Monitored { get; set; }
        public int? QualityProfileId { get; set; }
        public int? MinimumCustomFormatScore { get; set; }
    }

    public class AssignMovieEditionFileRequest
    {
        public int MovieId { get; set; }
        public int MovieFileId { get; set; }
    }

    public class ConvertMovieEditionFileToMainRequest
    {
        public ExistingMainFileAction? ExistingMainFileAction { get; set; }
    }

    public class RemoveMovieEditionRequest
    {
        public AttachedEditionFileAction? AttachedFileAction { get; set; }
        public ExistingMainFileAction? ExistingMainFileAction { get; set; }
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
                Aliases = model.Aliases,
                Monitored = model.Monitored,
                QualityProfileId = model.QualityProfileId,
                MinimumCustomFormatScore = model.MinimumCustomFormatScore,
                LastSearchTime = model.LastSearchTime,
                DateAdded = model.DateAdded,
            };
        }

        public static MovieEditionSlot ToModel(this CreateMovieEditionRequest request)
        {
            if (request == null)
            {
                return null;
            }

            return new MovieEditionSlot
            {
                MovieId = request.MovieId,
                EditionName = request.EditionName?.Trim() ?? string.Empty,
                SearchTerm = string.IsNullOrWhiteSpace(request.SearchTerm) ? null : request.SearchTerm.Trim(),
                Aliases = request.Aliases ?? new List<string>(),
                Monitored = request.Monitored,
                QualityProfileId = request.QualityProfileId,
                MinimumCustomFormatScore = request.MinimumCustomFormatScore,
            };
        }

        public static MovieEditionSlot ToModel(this UpdateMovieEditionRequest request, MovieEditionSlot persisted)
        {
            if (request == null || persisted == null)
            {
                return null;
            }

            return new MovieEditionSlot
            {
                Id = persisted.Id,
                MovieId = persisted.MovieId,
                EditionName = request.EditionName?.Trim() ?? string.Empty,
                SearchTerm = string.IsNullOrWhiteSpace(request.SearchTerm) ? null : request.SearchTerm.Trim(),
                Aliases = request.Aliases ?? new List<string>(),
                Monitored = request.Monitored,
                QualityProfileId = request.QualityProfileId,
                MinimumCustomFormatScore = request.MinimumCustomFormatScore,
                LastSearchTime = persisted.LastSearchTime,
                DateAdded = persisted.DateAdded,
            };
        }

        public static List<MovieEditionSlotResource> ToResource(this IEnumerable<MovieEditionSlot> models)
        {
            return models.Select(ToResource).ToList();
        }
    }
}
