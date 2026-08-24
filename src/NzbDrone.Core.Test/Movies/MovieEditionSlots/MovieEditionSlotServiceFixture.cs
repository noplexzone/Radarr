using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieEditionSlots
{
    [TestFixture]
    public class MovieEditionSlotServiceFixture : CoreTest<MovieEditionSlotService>
    {
        private const int MovieId = 12;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Setup(r => r.FindByMovieId(It.IsAny<int>()))
                .Returns(new List<MovieEditionSlot>());
            Mocker.GetMock<IMovieEditionSlotAliasRepository>()
                .Setup(r => r.FindBySlotIds(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<MovieEditionSlotAlias>());
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovie(It.IsAny<int>()))
                .Returns(new List<MovieFile>());
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.FindByEditionSlotId(It.IsAny<int>()))
                .Returns((MovieFile)null);
            Mocker.GetMock<IMovieEditionSlotMutationStore>()
                .Setup(s => s.Add(It.IsAny<MovieEditionSlot>()))
                .Returns<MovieEditionSlot>(slot =>
                {
                    slot.Id = 7;
                    return slot;
                });
            Mocker.GetMock<IMovieEditionSlotMutationStore>()
                .Setup(s => s.Update(It.IsAny<MovieEditionSlot>()))
                .Returns<MovieEditionSlot>(slot => slot);
        }

        [Test]
        public void add_should_persist_normalized_canonical_key_and_durable_aliases()
        {
            var result = Subject.Add(new MovieEditionSlot
            {
                MovieId = MovieId,
                EditionName = "  Director's Cut ",
                Aliases = new List<string> { "Directors.Cut", " directors cut ", "DC" }
            });

            result.CanonicalEditionKey.Should().Be("directorscut");
            result.Aliases.Should().Equal("Directors.Cut", "DC");
            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(r => r.Add(It.Is<MovieEditionSlot>(candidate =>
                candidate.CanonicalEditionKey == "directorscut" && candidate.Aliases.SequenceEqual(new[] { "Directors.Cut", "DC" }))), Times.Once);
        }

        [Test]
        public void get_should_hydrate_aliases_from_durable_rows()
        {
            var slot = new MovieEditionSlot { Id = 3, MovieId = MovieId, EditionName = "IMAX" };
            Mocker.GetMock<IMovieEditionSlotRepository>().Setup(r => r.FindByMovieId(MovieId)).Returns(new List<MovieEditionSlot> { slot });
            Mocker.GetMock<IMovieEditionSlotAliasRepository>()
                .Setup(r => r.FindBySlotIds(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 3 }))))
                .Returns(new List<MovieEditionSlotAlias>
                {
                    new MovieEditionSlotAlias { MovieEditionSlotId = 3, Alias = "IMAX Enhanced", NormalizedAlias = "imaxenhanced" }
                });

            Subject.GetForMovie(MovieId).Single().Aliases.Should().Equal("IMAX Enhanced");
        }

        [Test]
        public void add_should_reject_alias_that_conflicts_with_an_existing_slot_term()
        {
            var existing = new MovieEditionSlot { Id = 1, MovieId = MovieId, EditionName = "Director's Cut" };
            Mocker.GetMock<IMovieEditionSlotRepository>().Setup(r => r.FindByMovieId(MovieId)).Returns(new List<MovieEditionSlot> { existing });

            Assert.Throws<ValidationException>(() => Subject.Add(new MovieEditionSlot
            {
                MovieId = MovieId,
                EditionName = "Extended",
                Aliases = new List<string> { "Directors.Cut" }
            }));
        }


        [TestCase("---", null, null)]
        [TestCase("IMAX", "...", null)]
        [TestCase("IMAX", null, "___")]
        public void add_should_reject_terms_that_normalize_to_empty(string editionName, string searchTerm, string alias)
        {
            Assert.Throws<ValidationException>(() => Subject.Add(new MovieEditionSlot
            {
                MovieId = MovieId,
                EditionName = editionName,
                SearchTerm = searchTerm,
                Aliases = alias == null ? new List<string>() : new List<string> { alias }
            }));

            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(r => r.Add(It.IsAny<MovieEditionSlot>()), Times.Never);
        }

        [Test]
        public void update_should_reject_moving_slot_to_another_movie()
        {
            var persisted = new MovieEditionSlot { Id = 5, MovieId = MovieId, EditionName = "IMAX" };
            Mocker.GetMock<IMovieEditionSlotRepository>().Setup(r => r.Get(5)).Returns(persisted);

            Assert.Throws<ValidationException>(() => Subject.Update(new MovieEditionSlot
            {
                Id = 5,
                MovieId = MovieId + 1,
                EditionName = "IMAX"
            }));

            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(r => r.Update(It.IsAny<MovieEditionSlot>()), Times.Never);
        }

        [Test]
        public void update_should_reject_a_cross_movie_durable_file_link()
        {
            var persisted = new MovieEditionSlot { Id = 5, MovieId = MovieId, EditionName = "IMAX" };
            Mocker.GetMock<IMovieEditionSlotRepository>().Setup(r => r.Get(5)).Returns(persisted);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.FindByEditionSlotId(5)).Returns(new MovieFile
            {
                Id = 8,
                MovieId = MovieId + 1,
                MovieEditionSlotId = 5
            });

            Assert.Throws<InvalidOperationException>(() => Subject.Update(new MovieEditionSlot
            {
                Id = 5,
                MovieId = MovieId,
                EditionName = "IMAX"
            }));

            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(r => r.Update(It.IsAny<MovieEditionSlot>()), Times.Never);
        }

        [Test]
        public void movie_file_events_should_not_infer_or_change_assignments_from_parser_text()
        {
            var file = new MovieFile { Id = 5, MovieId = MovieId, Edition = "IMAX" };

            Subject.Handle(new MovieFileAddedEvent(file));
            Subject.Handle(new MovieFileUpdatedEvent(file));
            Subject.Handle(new MovieFileDeletedEvent(file, DeleteMediaFileReason.Manual));
            Subject.ReconcileForMovie(MovieId, new List<MovieFile> { file });

            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(r => r.Add(It.IsAny<MovieEditionSlot>()), Times.Never);
            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(r => r.Update(It.IsAny<MovieEditionSlot>()), Times.Never);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(It.IsAny<MovieFile>()), Times.Never);
        }

        [Test]
        public void delete_should_reject_attached_file_so_lifecycle_cannot_be_bypassed()
        {
            var slot = new MovieEditionSlot { Id = 4, MovieId = MovieId, EditionName = "IMAX" };
            Mocker.GetMock<IMovieEditionSlotRepository>().Setup(r => r.Get(4)).Returns(slot);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.FindByEditionSlotId(4)).Returns(new MovieFile
            {
                Id = 8,
                MovieId = MovieId + 1,
                MovieEditionSlotId = 4
            });

            Assert.Throws<InvalidOperationException>(() => Subject.Delete(4));
            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(r => r.Delete(It.IsAny<int>()), Times.Never);
        }

        [Test]
        public void status_summary_should_use_durable_file_assignments()
        {
            Mocker.GetMock<IMovieEditionSlotRepository>().Setup(r => r.FindByMovieIds(It.IsAny<IEnumerable<int>>())).Returns(new List<MovieEditionSlot>
            {
                new MovieEditionSlot { Id = 1, MovieId = MovieId, Monitored = true },
                new MovieEditionSlot { Id = 2, MovieId = MovieId, Monitored = true }
            });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovies(It.IsAny<IEnumerable<int>>())).Returns(new List<MovieFile>
            {
                new MovieFile { Id = 9, MovieId = MovieId, MovieEditionSlotId = 2 }
            });

            Subject.GetSlotStatusSummary(new[] { MovieId })[MovieId].Should().Be((2, 1));
        }
    }
}
