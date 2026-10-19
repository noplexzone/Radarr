using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Movies.Editions
{
    public interface IMovieEditionService
    {
        MovieEdition GetEdition(int editionId);
        List<MovieEdition> GetEditions(int movieId, bool includeFiles = false);
        Dictionary<int, List<MovieEdition>> GetEditions(IEnumerable<int> movieIds, bool includeFiles = false);
        MovieEdition GetDefaultEdition(int movieId);
        MovieEdition FindByKey(int movieId, string key);
        MovieEdition AddEdition(MovieEdition edition);
        MovieEdition EnsureEdition(MovieEdition edition);
        MovieEdition UpdateEdition(MovieEdition edition);
        void UpdateEditions(List<MovieEdition> editions);
        void DeleteEdition(MovieEdition edition);
        QualityProfile GetQualityProfile(Movie movie, MovieEdition edition);
        MovieFile GetCurrentFile(Movie movie, MovieEdition edition);
        MovieFile GetCurrentFile(Movie movie, MovieEdition edition, IEnumerable<MovieFile> movieFiles);
        int GetRepresentativeFileId(Movie movie);
        bool AnyMovieHasMultipleEditions();
        List<int> GetMovieIdsWithMissingEditions();
    }

    public class MovieEditionService : IMovieEditionService,
                                       IHandle<MovieAddedEvent>,
                                       IHandle<MoviesImportedEvent>,
                                       IHandleAsync<MoviesDeletedEvent>
    {
        private readonly IMovieEditionRepository _editionRepository;
        private readonly IMediaFileRepository _mediaFileRepository;
        private readonly IQualityProfileRepository _qualityProfileRepository;
        private readonly Logger _logger;

        public MovieEditionService(IMovieEditionRepository editionRepository,
                                   IMediaFileRepository mediaFileRepository,
                                   IQualityProfileRepository qualityProfileRepository,
                                   Logger logger)
        {
            _editionRepository = editionRepository;
            _mediaFileRepository = mediaFileRepository;
            _qualityProfileRepository = qualityProfileRepository;
            _logger = logger;
        }

        public MovieEdition GetEdition(int editionId)
        {
            return _editionRepository.Get(editionId);
        }

        public List<MovieEdition> GetEditions(int movieId, bool includeFiles = false)
        {
            var editions = _editionRepository.GetByMovieId(movieId);

            if (!editions.Any(e => e.IsDefault))
            {
                editions.Add(CreateDefaultEdition(movieId, editions.Any()));
            }

            if (includeFiles)
            {
                AttachFiles(editions, _mediaFileRepository.GetFilesByMovie(movieId));
            }

            return OrderEditions(editions);
        }

        public Dictionary<int, List<MovieEdition>> GetEditions(IEnumerable<int> movieIds, bool includeFiles = false)
        {
            var ids = movieIds.Distinct().ToList();
            var editionsByMovie = _editionRepository.GetByMovieIds(ids)
                .GroupBy(e => e.MovieId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var filesByMovie = includeFiles
                ? _mediaFileRepository.GetFilesByMovies(ids).GroupBy(f => f.MovieId).ToDictionary(g => g.Key, g => g.ToList())
                : new Dictionary<int, List<MovieFile>>();

            var result = new Dictionary<int, List<MovieEdition>>();

            foreach (var movieId in ids)
            {
                var editions = editionsByMovie.GetValueOrDefault(movieId) ?? new List<MovieEdition>();

                if (!editions.Any(e => e.IsDefault))
                {
                    editions.Add(CreateDefaultEdition(movieId, editions.Any()));
                }

                if (includeFiles)
                {
                    AttachFiles(editions, filesByMovie.GetValueOrDefault(movieId) ?? new List<MovieFile>());
                }

                result[movieId] = OrderEditions(editions);
            }

            return result;
        }

        public MovieEdition GetDefaultEdition(int movieId)
        {
            return GetEditions(movieId).Single(e => e.IsDefault);
        }

        public MovieEdition FindByKey(int movieId, string key)
        {
            key ??= string.Empty;

            return GetEditions(movieId).FirstOrDefault(e => e.Key == key);
        }

        public MovieEdition AddEdition(MovieEdition edition)
        {
            if (edition.Title.IsNullOrWhiteSpace() && !edition.IsDefault)
            {
                throw new ArgumentException("A name is required for an edition");
            }

            edition.Key = EditionNormalizer.Normalize(edition.Title);

            var existing = FindByKey(edition.MovieId, edition.Key);

            if (existing != null)
            {
                throw new MovieEditionExistsException(existing);
            }

            if (edition.Added == default)
            {
                edition.Added = DateTime.UtcNow;
            }

            _logger.Info("Adding edition '{0}' to movie {1}", edition.DisplayTitle, edition.MovieId);

            return _editionRepository.Insert(edition);
        }

        public MovieEdition EnsureEdition(MovieEdition edition)
        {
            if (!edition.IsNew)
            {
                return edition;
            }

            var existing = FindByKey(edition.MovieId, EditionNormalizer.Normalize(edition.Title));

            if (existing != null)
            {
                return existing;
            }

            var added = AddEdition(edition);

            edition.Id = added.Id;
            edition.Key = added.Key;

            return edition;
        }

        public MovieEdition UpdateEdition(MovieEdition edition)
        {
            var stored = _editionRepository.Get(edition.Id);

            if (!stored.IsDefault && edition.Title.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("A name is required for an edition");
            }

            var key = EditionNormalizer.Normalize(edition.Title);
            var conflict = _editionRepository.GetByMovieId(stored.MovieId).FirstOrDefault(e => e.Id != stored.Id && e.Key == key);

            if (conflict != null)
            {
                throw new MovieEditionExistsException(conflict);
            }

            stored.Title = edition.Title.IsNullOrWhiteSpace() ? null : edition.Title.Trim();
            stored.Key = key;
            stored.Monitored = edition.Monitored;
            stored.QualityProfileId = edition.QualityProfileId;

            _editionRepository.Update(stored);

            // Keep the edition text on files in sync so {Edition Tags} naming follows the edition's name
            var files = _mediaFileRepository.GetFilesByMovie(stored.MovieId).Where(f => f.MovieEditionId == stored.Id).ToList();

            foreach (var file in files)
            {
                file.Edition = stored.Title;
            }

            if (files.Any())
            {
                _mediaFileRepository.UpdateMany(files);
            }

            return stored;
        }

        public void UpdateEditions(List<MovieEdition> editions)
        {
            _editionRepository.UpdateMany(editions);
        }

        public void DeleteEdition(MovieEdition edition)
        {
            if (edition.IsDefault)
            {
                throw new InvalidOperationException("The default edition can't be removed");
            }

            _logger.Info("Removing edition '{0}' from movie {1}", edition.DisplayTitle, edition.MovieId);

            _editionRepository.Delete(edition.Id);
        }

        public QualityProfile GetQualityProfile(Movie movie, MovieEdition edition)
        {
            if (edition != null && edition.QualityProfileId > 0 && edition.QualityProfileId != movie.QualityProfileId)
            {
                var profile = _qualityProfileRepository.Find(edition.QualityProfileId);

                if (profile != null)
                {
                    return profile;
                }
            }

            return movie.QualityProfile ?? _qualityProfileRepository.Find(movie.QualityProfileId);
        }

        public MovieFile GetCurrentFile(Movie movie, MovieEdition edition)
        {
            if (edition == null || edition.IsNew)
            {
                return null;
            }

            return GetCurrentFile(movie, edition, _mediaFileRepository.GetFilesByMovie(movie.Id));
        }

        public MovieFile GetCurrentFile(Movie movie, MovieEdition edition, IEnumerable<MovieFile> movieFiles)
        {
            if (edition == null || edition.IsNew)
            {
                return null;
            }

            var files = movieFiles.Where(f => BelongsTo(f, edition)).ToList();

            return SelectBest(files, GetQualityProfile(movie, edition));
        }

        public int GetRepresentativeFileId(Movie movie)
        {
            var files = _mediaFileRepository.GetFilesByMovie(movie.Id);

            if (!files.Any())
            {
                return 0;
            }

            var editions = GetEditions(movie.Id);
            var defaultEdition = editions.Single(e => e.IsDefault);

            var file = GetCurrentFile(movie, defaultEdition, files) ??
                       editions.Where(e => !e.IsDefault)
                           .OrderBy(e => e.Id)
                           .Select(e => GetCurrentFile(movie, e, files))
                           .FirstOrDefault(f => f != null) ??
                       files.OrderBy(f => f.Id).First();

            return file.Id;
        }

        public List<int> GetMovieIdsWithMissingEditions()
        {
            return _editionRepository.GetMovieIdsWithMissingEditions();
        }

        public bool AnyMovieHasMultipleEditions()
        {
            return _editionRepository.AnyMovieHasMultipleEditions();
        }

        public void Handle(MovieAddedEvent message)
        {
            EnsureDefaultEditions(new List<Movie> { message.Movie });
        }

        public void Handle(MoviesImportedEvent message)
        {
            EnsureDefaultEditions(message.Movies);
        }

        public void HandleAsync(MoviesDeletedEvent message)
        {
            _editionRepository.DeleteForMovies(message.Movies.Select(m => m.Id).ToList());
        }

        private void EnsureDefaultEditions(List<Movie> movies)
        {
            var movieIds = movies.Select(m => m.Id).ToList();
            var existing = _editionRepository.GetByMovieIds(movieIds).Where(e => e.IsDefault).Select(e => e.MovieId).ToHashSet();

            var toAdd = movieIds.Where(id => !existing.Contains(id))
                .Distinct()
                .Select(id => MovieEdition.CreateDefault(id))
                .ToList();

            if (toAdd.Any())
            {
                _editionRepository.InsertMany(toAdd);
            }
        }

        private MovieEdition CreateDefaultEdition(int movieId, bool hasOtherEditions)
        {
            _logger.Debug("Movie {0} has no default edition, creating one", movieId);

            // A movie that already has named editions but no default one must not suddenly report a missing edition
            return _editionRepository.Insert(MovieEdition.CreateDefault(movieId, !hasOtherEditions));
        }

        private static void AttachFiles(List<MovieEdition> editions, List<MovieFile> files)
        {
            foreach (var edition in editions)
            {
                edition.Files = files.Where(f => BelongsTo(f, edition)).ToList();
            }
        }

        private static bool BelongsTo(MovieFile file, MovieEdition edition)
        {
            // Files that haven't been assigned to an edition yet belong to the default edition
            return file.MovieEditionId == edition.Id || (file.MovieEditionId == 0 && edition.IsDefault);
        }

        private static List<MovieEdition> OrderEditions(List<MovieEdition> editions)
        {
            return editions.OrderByDescending(e => e.IsDefault).ThenBy(e => e.Id).ToList();
        }

        private static MovieFile SelectBest(List<MovieFile> files, QualityProfile profile)
        {
            if (!files.Any())
            {
                return null;
            }

            if (files.Count == 1)
            {
                return files.First();
            }

            if (profile?.Items == null || !profile.Items.Any())
            {
                return files.OrderByDescending(f => f.Size).ThenBy(f => f.Id).First();
            }

            var comparer = new QualityModelComparer(profile);

            return files.OrderByDescending(f => f.Quality ?? new QualityModel(Quality.Unknown), comparer)
                .ThenByDescending(f => f.Size)
                .ThenBy(f => f.Id)
                .First();
        }
    }
}
