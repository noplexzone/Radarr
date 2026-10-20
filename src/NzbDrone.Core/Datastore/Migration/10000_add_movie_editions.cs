using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // Fork migrations are numbered from 10000 so they never collide with upstream's sequential migrations
    [Migration(10000)]
    public class add_movie_editions : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.TableForModel("MovieEditions")
                .WithColumn("MovieId").AsInt32().NotNullable()
                .WithColumn("Title").AsString().Nullable()
                .WithColumn("Key").AsString().NotNullable()
                .WithColumn("IsDefault").AsBoolean().NotNullable()
                .WithColumn("Monitored").AsBoolean().NotNullable()
                .WithColumn("QualityProfileId").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("Added").AsDateTimeOffset().NotNullable()
                .WithColumn("LastSearchTime").AsDateTimeOffset().Nullable();

            Create.Index().OnTable("MovieEditions")
                .OnColumn("MovieId").Ascending()
                .OnColumn("Key").Ascending()
                .WithOptions().Unique();

            Alter.Table("MovieFiles").AddColumn("MovieEditionId").AsInt32().NotNullable().WithDefaultValue(0);
            Create.Index().OnTable("MovieFiles").OnColumn("MovieEditionId");

            Alter.Table("Movies").AddColumn("AutoAddEditions").AsBoolean().NotNullable().WithDefaultValue(false);

            Execute.WithConnection(CreateEditions);
        }

        private void CreateEditions(IDbConnection conn, IDbTransaction tran)
        {
            var movies = conn.Query<Movie10000>("SELECT \"Id\", \"MovieFileId\" FROM \"Movies\"", transaction: tran).ToList();
            var files = conn.Query<MovieFile10000>("SELECT \"Id\", \"MovieId\", \"Edition\", \"Size\" FROM \"MovieFiles\"", transaction: tran)
                .GroupBy(f => f.MovieId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var now = DateTime.UtcNow;
            var editions = new List<Edition10000>();
            var fileKeys = new Dictionary<int, string>();

            foreach (var movie in movies)
            {
                var movieFiles = files.GetValueOrDefault(movie.Id) ?? new List<MovieFile10000>();

                if (movieFiles.Count == 1)
                {
                    // The only file a movie has keeps being its main edition, whatever its edition tag says
                    var file = movieFiles.Single();
                    var key = Normalize(file.Edition);

                    editions.Add(new Edition10000(movie.Id, CleanTitle(file.Edition), key, true, true, now));
                    fileKeys[file.Id] = key;

                    continue;
                }

                var groups = movieFiles.GroupBy(f => Normalize(f.Edition)).ToList();
                var defaultHasFiles = groups.Any(g => g.Key.Length == 0);

                // Never create new wanted items: an empty default edition next to named editions starts unmonitored
                editions.Add(new Edition10000(movie.Id, null, string.Empty, true, defaultHasFiles || !groups.Any(), now));

                foreach (var group in groups)
                {
                    if (group.Key.Length > 0)
                    {
                        var title = group.Select(f => CleanTitle(f.Edition)).First(t => t != null);
                        editions.Add(new Edition10000(movie.Id, title, group.Key, false, true, now));
                    }

                    foreach (var file in group)
                    {
                        fileKeys[file.Id] = group.Key;
                    }
                }
            }

            conn.Execute("INSERT INTO \"MovieEditions\" (\"MovieId\", \"Title\", \"Key\", \"IsDefault\", \"Monitored\", \"QualityProfileId\", \"Added\") " +
                         "VALUES (@MovieId, @Title, @Key, @IsDefault, @Monitored, 0, @Added)",
                         editions,
                         transaction: tran);

            var editionIds = conn.Query<Edition10000>("SELECT \"Id\", \"MovieId\", \"Key\", \"IsDefault\" FROM \"MovieEditions\"", transaction: tran)
                .ToList();

            var editionIdByKey = editionIds.ToDictionary(e => (e.MovieId, e.Key), e => e.Id);
            var defaultEditionIds = editionIds.Where(e => e.IsDefault).Select(e => e.Id).ToHashSet();

            var fileUpdates = new List<object>();
            var movieUpdates = new List<object>();

            foreach (var movie in movies)
            {
                var movieFiles = files.GetValueOrDefault(movie.Id) ?? new List<MovieFile10000>();

                foreach (var file in movieFiles)
                {
                    file.MovieEditionId = editionIdByKey[(movie.Id, fileKeys[file.Id])];
                    fileUpdates.Add(new { file.Id, file.MovieEditionId });
                }

                var movieFileId = GetRepresentativeFileId(movie, movieFiles, defaultEditionIds);

                if (movieFileId != movie.MovieFileId)
                {
                    movieUpdates.Add(new { movie.Id, MovieFileId = movieFileId });
                }
            }

            conn.Execute("UPDATE \"MovieFiles\" SET \"MovieEditionId\" = @MovieEditionId WHERE \"Id\" = @Id", fileUpdates, transaction: tran);
            conn.Execute("UPDATE \"Movies\" SET \"MovieFileId\" = @MovieFileId WHERE \"Id\" = @Id", movieUpdates, transaction: tran);
        }

        private static int GetRepresentativeFileId(Movie10000 movie, List<MovieFile10000> movieFiles, HashSet<int> defaultEditionIds)
        {
            if (!movieFiles.Any())
            {
                return 0;
            }

            var current = movieFiles.FirstOrDefault(f => f.Id == movie.MovieFileId);

            if (current != null && defaultEditionIds.Contains(current.MovieEditionId))
            {
                return current.Id;
            }

            var defaultFile = movieFiles.Where(f => defaultEditionIds.Contains(f.MovieEditionId))
                .OrderByDescending(f => f.Size)
                .FirstOrDefault();

            return defaultFile?.Id ?? current?.Id ?? movieFiles.OrderBy(f => f.Id).First().Id;
        }

        // Frozen copy of EditionTitle.Format as of this migration: "UNRATED" becomes "Unrated", "Directors Cut" becomes "Director's Cut"
        private static string CleanTitle(string edition)
        {
            if (string.IsNullOrWhiteSpace(edition))
            {
                return null;
            }

            var acronyms = new HashSet<string> { "3D", "4K", "DC", "HD", "HDR", "IMAX", "SDR", "TV", "UHD", "UK", "US" };
            var smallWords = new HashSet<string> { "a", "and", "of", "the" };

            var title = Regex.Replace(edition, @"[._\s]+", " ").Trim();
            title = Regex.Replace(title, @"\bextcut\b", "Extended Cut", RegexOptions.IgnoreCase);

            var words = title.Split(' ').Select((word, index) =>
            {
                var upper = word.ToUpperInvariant();
                var lower = word.ToLowerInvariant();

                if (acronyms.Contains(upper))
                {
                    return upper;
                }

                if (Regex.IsMatch(word, @"^\d+(st|nd|rd|th|in\d+)$", RegexOptions.IgnoreCase))
                {
                    return lower;
                }

                if (word != upper && word != lower)
                {
                    return word;
                }

                if (index > 0 && smallWords.Contains(lower))
                {
                    return lower;
                }

                return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
            });

            return Regex.Replace(string.Join(" ", words), @"\b(Director|Collector)s\b", "$1's");
        }

        // Frozen copy of EditionNormalizer as of this migration, so later normaliser changes can't alter migrated keys
        private static string Normalize(string edition)
        {
            if (string.IsNullOrWhiteSpace(edition))
            {
                return string.Empty;
            }

            var key = edition.ToLowerInvariant().Replace('’', '\'').Replace('`', '\'');

            key = Regex.Replace(key, @"[._\-]+", " ");
            key = Regex.Replace(key, @"\s*&\s*", " and ");
            key = Regex.Replace(key, @"[^\p{L}\p{N}' ]+", " ");
            key = Regex.Replace(key, @"\b(director|collector)'?s\b", "$1's");
            key = Regex.Replace(key, @"\s{2,}", " ").Trim();
            key = Regex.Replace(key, @"^the\s+(?=\S)", string.Empty);
            key = Regex.Replace(key, @"(?<=\S)\s+(cut|edition|version)$", string.Empty);

            return key;
        }

        private class Movie10000
        {
            public int Id { get; set; }
            public int MovieFileId { get; set; }
        }

        private class MovieFile10000
        {
            public int Id { get; set; }
            public int MovieId { get; set; }
            public string Edition { get; set; }
            public long Size { get; set; }
            public int MovieEditionId { get; set; }
        }

        private class Edition10000
        {
            public Edition10000()
            {
            }

            public Edition10000(int movieId, string title, string key, bool isDefault, bool monitored, DateTime added)
            {
                MovieId = movieId;
                Title = title;
                Key = key;
                IsDefault = isDefault;
                Monitored = monitored;
                Added = added;
            }

            public int Id { get; set; }
            public int MovieId { get; set; }
            public string Title { get; set; }
            public string Key { get; set; }
            public bool IsDefault { get; set; }
            public bool Monitored { get; set; }
            public DateTime Added { get; set; }
        }
    }
}
