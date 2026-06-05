using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation.Aggregators;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport.Aggregation.Aggregators
{
    [TestFixture]
    public class AggregateReleaseInfoFixture : CoreTest<AggregateReleaseInfo>
    {
        private DownloadClientItem _downloadClientItem;

        [SetUp]
        public void Setup()
        {
            _downloadClientItem = new DownloadClientItem { DownloadId = "abc123" };
        }

        private MovieHistory GrabHistory(Dictionary<string, string> data = null)
        {
            return new MovieHistory
            {
                MovieId = 1,
                EventType = MovieHistoryEventType.Grabbed,
                Date = DateTime.UtcNow,
                SourceTitle = "Movie.2020.mkv",
                Data = data ?? new Dictionary<string, string>()
            };
        }

        [Test]
        public void should_return_local_movie_unchanged_when_no_download_client_item()
        {
            var localMovie = new LocalMovie();

            Subject.Aggregate(localMovie, null);

            localMovie.Release.Should().BeNull();
            localMovie.MovieEditionSlotId.Should().BeNull();
        }

        [Test]
        public void should_return_local_movie_unchanged_when_no_grab_history()
        {
            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(_downloadClientItem.DownloadId))
                  .Returns(new List<MovieHistory>());

            var localMovie = new LocalMovie();

            Subject.Aggregate(localMovie, _downloadClientItem);

            localMovie.Release.Should().BeNull();
            localMovie.MovieEditionSlotId.Should().BeNull();
        }

        [Test]
        public void should_populate_release_from_grab_history()
        {
            var history = GrabHistory(new Dictionary<string, string>
            {
                { "indexer", "NZBGeek" },
                { "size", "1024" },
                { "indexerFlags", "0" }
            });

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(_downloadClientItem.DownloadId))
                  .Returns(new List<MovieHistory> { history });

            var localMovie = new LocalMovie();

            Subject.Aggregate(localMovie, _downloadClientItem);

            localMovie.Release.Should().NotBeNull();
            localMovie.Release.Indexer.Should().Be("NZBGeek");
        }

        [Test]
        public void should_set_movie_edition_slot_id_when_present_in_grab_history()
        {
            var history = GrabHistory(new Dictionary<string, string>
            {
                { MovieHistory.MOVIE_EDITION_SLOT_ID, "42" }
            });

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(_downloadClientItem.DownloadId))
                  .Returns(new List<MovieHistory> { history });

            var localMovie = new LocalMovie();

            Subject.Aggregate(localMovie, _downloadClientItem);

            localMovie.MovieEditionSlotId.Should().Be(42);
        }

        [Test]
        public void should_leave_movie_edition_slot_id_null_when_not_in_grab_history()
        {
            var history = GrabHistory(new Dictionary<string, string>
            {
                { "indexer", "SomeIndexer" }
            });

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(_downloadClientItem.DownloadId))
                  .Returns(new List<MovieHistory> { history });

            var localMovie = new LocalMovie();

            Subject.Aggregate(localMovie, _downloadClientItem);

            localMovie.MovieEditionSlotId.Should().BeNull();
        }

        [Test]
        public void should_use_most_recent_grab_history_for_slot_id()
        {
            var older = GrabHistory(new Dictionary<string, string> { { MovieHistory.MOVIE_EDITION_SLOT_ID, "1" } });
            older.Date = DateTime.UtcNow.AddMinutes(-10);

            var newer = GrabHistory(new Dictionary<string, string> { { MovieHistory.MOVIE_EDITION_SLOT_ID, "7" } });
            newer.Date = DateTime.UtcNow;

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(_downloadClientItem.DownloadId))
                  .Returns(new List<MovieHistory> { older, newer });

            var localMovie = new LocalMovie();

            Subject.Aggregate(localMovie, _downloadClientItem);

            localMovie.MovieEditionSlotId.Should().Be(7);
        }
    }
}
