using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Housekeeping.Housekeepers
{
    [TestFixture]
    public class CleanupOrphanedMovieFilesFixture : DbTest<CleanupOrphanedMovieFiles, MovieFile>
    {
        [Test]
        public void should_delete_orphaned_episode_files()
        {
            var movieFile = Builder<MovieFile>.CreateNew()
                                                  .With(h => h.Quality = new QualityModel())
                                                  .With(h => h.Languages = new List<Language> { Language.English })
                                                  .BuildNew();

            Db.Insert(movieFile);
            Subject.Clean();
            AllStoredModels.Should().BeEmpty();
        }

        [Test]
        public void should_not_delete_unorphaned_movie_files()
        {
            var movie = Builder<Movie>.CreateNew().BuildNew();
            Db.Insert(movie);

            var movieFiles = Builder<MovieFile>.CreateListOfSize(2)
                                                   .All()
                                                   .With(h => h.Quality = new QualityModel())
                                                   .With(h => h.Languages = new List<Language> { Language.English })
                                                   .TheFirst(1)
                                                   .With(h => h.MovieId = movie.Id)
                                                   .TheNext(1)
                                                   .With(h => h.MovieId = movie.Id + 1)
                                                   .BuildListOfNew();

            Db.InsertMany(movieFiles);

            Subject.Clean();
            AllStoredModels.Should().ContainSingle(f => f.MovieId == movie.Id);
        }

        [Test]
        public void should_not_delete_files_of_other_editions()
        {
            var movie = Builder<Movie>.CreateNew().BuildNew();
            Db.Insert(movie);

            var movieFiles = Builder<MovieFile>.CreateListOfSize(2)
                                                   .All()
                                                   .With(h => h.MovieId = movie.Id)
                                                   .With(h => h.Quality = new QualityModel())
                                                   .With(h => h.Languages = new List<Language> { Language.English })
                                                   .BuildListOfNew();

            Db.InsertMany(movieFiles);

            // Only the default edition's file is the movie's main file
            movie.MovieFileId = movieFiles.First().Id;
            Db.Update(movie);

            Subject.Clean();
            AllStoredModels.Should().HaveCount(2);
        }
    }
}
