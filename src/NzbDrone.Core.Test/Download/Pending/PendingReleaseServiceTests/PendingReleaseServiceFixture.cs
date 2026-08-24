using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.Pending.PendingReleaseServiceTests
{
    [TestFixture]
    public class PendingReleaseServiceFixture : CoreTest<PendingReleaseService>
    {
        private void GivenPendingRelease()
        {
            var movie = new Movie { Id = 1 };
            Mocker.GetMock<IPendingReleaseRepository>()
                  .Setup(v => v.All())
                  .Returns(new List<PendingRelease>
                  {
                      new PendingRelease
                      {
                          MovieId = movie.Id,
                          Release = new ReleaseInfo { IndexerId = 1 },
                          ParsedMovieInfo = new ParsedMovieInfo()
                      }
                  });
            Mocker.GetMock<IMovieService>()
                  .Setup(v => v.GetMovies(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<Movie> { movie });
        }

        [Test]
        public void should_not_ignore_pending_items_from_available_indexer()
        {
            Mocker.GetMock<IIndexerStatusService>()
                .Setup(v => v.GetBlockedProviders())
                .Returns(new List<IndexerStatus>());

            GivenPendingRelease();

            var results = Subject.GetPending();

            results.Should().NotBeEmpty();
            Mocker.GetMock<IMakeDownloadDecision>()
                  .Verify(v => v.GetRssDecision(It.Is<List<ReleaseInfo>>(d => d.Count == 0), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_ignore_pending_items_from_unavailable_indexer()
        {
            Mocker.GetMock<IIndexerStatusService>()
                .Setup(v => v.GetBlockedProviders())
                .Returns(new List<IndexerStatus> { new IndexerStatus { ProviderId = 1, DisabledTill = DateTime.UtcNow.AddHours(2) } });

            GivenPendingRelease();

            var results = Subject.GetPending();

            results.Should().BeEmpty();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_reconstruct_malformed_pending_rows_and_continue_in_persisted_order(bool nullParsedMovieInfo)
        {
            var movie = new Movie { Id = 1 };
            var malformedTarget = MovieAcquisitionTarget.ForEditionSlot(41);
            var additionalInfo = new PendingReleaseAdditionalInfo();
            additionalInfo.SerializeAcquisitionTarget(malformedTarget);
            var malformedParsedMovieInfo = nullParsedMovieInfo
                ? null
                : new ParsedMovieInfo { MovieTitles = new List<string> { " " } };
            var rows = new List<PendingRelease>
            {
                new PendingRelease
                {
                    Id = 1,
                    MovieId = movie.Id,
                    Title = "Malformed.Release",
                    Release = new ReleaseInfo { Title = "Malformed.Release", IndexerId = 1 },
                    ParsedMovieInfo = malformedParsedMovieInfo,
                    AdditionalInfo = additionalInfo
                },
                new PendingRelease
                {
                    Id = 2,
                    MovieId = movie.Id,
                    Title = "Valid.Release",
                    Release = new ReleaseInfo { Title = "Valid.Release", IndexerId = 1 },
                    ParsedMovieInfo = new ParsedMovieInfo { MovieTitles = new List<string> { "Valid" } }
                }
            };

            Mocker.GetMock<IPendingReleaseRepository>().Setup(v => v.All()).Returns(rows);
            Mocker.GetMock<IMovieService>()
                  .Setup(v => v.GetMovies(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<Movie> { movie });
            Mocker.GetMock<IIndexerStatusService>()
                  .Setup(v => v.GetBlockedProviders())
                  .Returns(new List<IndexerStatus>());

            var results = Subject.GetPending();

            results.Should().HaveCount(2);
            results[0].Release.Title.Should().Be("Malformed.Release");
            results[0].RemoteMovie.Movie.Should().BeSameAs(movie);
            results[0].AcquisitionTarget.Should().Be(malformedTarget);
            results[0].RemoteMovie.ParsedMovieInfo.Should().BeSameAs(malformedParsedMovieInfo);
            results[1].Release.Title.Should().Be("Valid.Release");
            rows[0].AdditionalInfo.AcquisitionTargetKind.Should().Be(MovieAcquisitionTargetKind.EditionSlot);
        }
    }
}
