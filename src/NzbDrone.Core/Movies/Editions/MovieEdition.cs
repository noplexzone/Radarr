using System;
using System.Collections.Generic;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Movies.Editions
{
    public class MovieEdition : ModelBase
    {
        public const string DefaultTitle = "Standard";

        public MovieEdition()
        {
            Files = new List<MovieFile>();
        }

        public int MovieId { get; set; }

        // Null for an unnamed default edition
        public string Title { get; set; }
        public string Key { get; set; }
        public bool IsDefault { get; set; }
        public bool Monitored { get; set; }

        // 0 inherits the movie's quality profile
        public int QualityProfileId { get; set; }
        public DateTime Added { get; set; }
        public DateTime? LastSearchTime { get; set; }

        // Not persisted
        public List<MovieFile> Files { get; set; }

        public bool IsNew => Id == 0;

        public string DisplayTitle => Title.IsNullOrWhiteSpace() ? DefaultTitle : Title;

        public static MovieEdition CreateDefault(int movieId, bool monitored = true)
        {
            return new MovieEdition
            {
                MovieId = movieId,
                Title = null,
                Key = string.Empty,
                IsDefault = true,
                Monitored = monitored,
                Added = DateTime.UtcNow
            };
        }

        public static MovieEdition CreateNew(int movieId, string title, bool monitored)
        {
            return new MovieEdition
            {
                MovieId = movieId,
                Title = title?.Trim(),
                Key = EditionNormalizer.Normalize(title),
                IsDefault = false,
                Monitored = monitored,
                Added = DateTime.UtcNow
            };
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1}", Id, DisplayTitle);
        }
    }
}
