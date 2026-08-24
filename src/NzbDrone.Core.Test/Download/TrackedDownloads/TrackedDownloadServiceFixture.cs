using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.TorrentRss;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class TrackedDownloadServiceFixture : CoreTest<TrackedDownloadService>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(1))
                .Returns(new List<MovieEditionSlot> { new MovieEditionSlot { Id = 42, MovieId = 1, QualityProfileId = 7, MinimumCustomFormatScore = 50 } });
        }

        private void GivenDownloadHistory()
        {
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(It.Is<string>(sr => sr == "35238")))
                .Returns(new List<MovieHistory>()
                {
                    new MovieHistory()
                    {
                        DownloadId = "35238",
                        SourceTitle = "TV Series S01",
                        MovieId = 3,
                    }
                });
        }

        [Test]
        public void should_track_downloads_using_the_source_title_if_it_cannot_be_found_using_the_download_title()
        {
            GivenDownloadHistory();

            var remoteMovie = new RemoteMovie
            {
                Movie = new Movie() { Id = 3 },

                ParsedMovieInfo = new ParsedMovieInfo()
                {
                    MovieTitles = new List<string> { "A Movie" },
                    Year = 1998
                }
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.Is<ParsedMovieInfo>(i => i.PrimaryMovieTitle == "A Movie"), It.IsAny<string>(), It.IsAny<int>(), null))
                  .Returns(remoteMovie);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem()
            {
                Title = "A Movie 1998",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.Should().NotBeNull();
            trackedDownload.RemoteMovie.Should().NotBeNull();
            trackedDownload.RemoteMovie.Movie.Should().NotBeNull();
            trackedDownload.RemoteMovie.Movie.Id.Should().Be(3);
        }

        [Test]
        public void should_set_indexer()
        {
            var episodeHistory = new MovieHistory()
            {
                DownloadId = "35238",
                SourceTitle = "TV Series S01",
                MovieId = 3,
                EventType = MovieHistoryEventType.Grabbed,
            };
            episodeHistory.Data.Add("indexer", "MyIndexer (Prowlarr)");
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(It.Is<string>(sr => sr == "35238")))
                .Returns(new List<MovieHistory>()
                {
                    episodeHistory
                });

            var indexerDefinition = new IndexerDefinition
            {
                Id = 1,
                Name = "MyIndexer (Prowlarr)",
                Settings = new TorrentRssIndexerSettings { MultiLanguages = new List<int> { Language.Original.Id, Language.French.Id } }
            };
            Mocker.GetMock<IIndexerFactory>()
                .Setup(v => v.Get(indexerDefinition.Id))
                .Returns(indexerDefinition);
            Mocker.GetMock<IIndexerFactory>()
                .Setup(v => v.All())
                .Returns(new List<IndexerDefinition>() { indexerDefinition });

            var remoteEpisode = new RemoteMovie
            {
                Movie = new Movie() { Id = 3 },
                ParsedMovieInfo = new ParsedMovieInfo()
                {
                    MovieTitles = new List<string> { "A Movie" },
                    Year = 1998
                }
            };

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null))
                .Returns(remoteEpisode);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem()
            {
                Title = "A Movie 1998",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.Should().NotBeNull();
            trackedDownload.RemoteMovie.Should().NotBeNull();
            trackedDownload.RemoteMovie.Release.Should().NotBeNull();
            trackedDownload.RemoteMovie.Release.Indexer.Should().Be("MyIndexer (Prowlarr)");
        }

        [Test]
        public void should_unmap_tracked_download_if_movie_deleted()
        {
            GivenDownloadHistory();

            var remoteMovie = new RemoteMovie
            {
                Movie = new Movie() { Id = 3 },

                ParsedMovieInfo = new ParsedMovieInfo()
                {
                    MovieTitles = { "A Movie" },
                    Year = 1998
                }
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null))
                  .Returns(remoteMovie);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<MovieHistory>());

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem()
            {
                Title = "A Movie 1998",
                DownloadId = "12345",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 1,
                    Type = "Blackhole",
                    Name = "Blackhole Client",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            Subject.TrackDownload(client, item);
            Subject.GetTrackedDownloads().Should().HaveCount(1);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null))
                  .Returns(default(RemoteMovie));

            Subject.Handle(new MoviesDeletedEvent(new List<Movie> { remoteMovie.Movie }, false, false));

            var trackedDownloads = Subject.GetTrackedDownloads();
            trackedDownloads.Should().HaveCount(1);
            trackedDownloads.First().RemoteMovie.Should().BeNull();
        }

        [Test]
        public void should_set_remote_movie_edition_slot_id_from_download_history_grab()
        {
            var movieHistory = new MovieHistory
            {
                DownloadId = "slot-download",
                SourceTitle = "Movie.2024.Directors.Cut.1080p",
                MovieId = 1,
                EventType = MovieHistoryEventType.Grabbed,
            };
            movieHistory.Data.Add("indexer", "TestIndexer");
            MovieAcquisitionTargetSerializer.Write(movieHistory.Data, MovieAcquisitionTarget.ForEditionSlot(42));

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId("slot-download"))
                .Returns(new List<MovieHistory> { movieHistory });

            var downloadHistory = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                MovieId = 1,
                DownloadId = "slot-download",
            };
            MovieAcquisitionTargetSerializer.Write(downloadHistory.Data, MovieAcquisitionTarget.ForEditionSlot(42));

            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetLatestGrab("slot-download"))
                .Returns(downloadHistory);

            var slotProfile = new QualityProfile { Id = 7, Name = "Slot" };
            var slotFile = new MovieFile { Id = 9, MovieId = 1, MovieEditionSlotId = 42 };
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.Get(7)).Returns(slotProfile);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.FindByEditionSlotId(42)).Returns(slotFile);
            var remoteMovie = new RemoteMovie
            {
                Movie = new Movie { Id = 1 },
                ParsedMovieInfo = new ParsedMovieInfo { MovieTitles = new List<string> { "Movie" }, Year = 2024 }
            };

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null))
                .Returns(remoteMovie);

            var client = new DownloadClientDefinition { Id = 1, Protocol = DownloadProtocol.Torrent };
            var item = new DownloadClientItem
            {
                Title = "Movie.2024.Directors.Cut.1080p",
                DownloadId = "slot-download",
                DownloadClientInfo = new DownloadClientItemClientInfo { Protocol = client.Protocol, Id = client.Id, Name = client.Name }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.Should().NotBeNull();
            trackedDownload.RemoteMovie.Should().NotBeNull();
            trackedDownload.RemoteMovie.MovieEditionSlotId.Should().Be(42);
            trackedDownload.RemoteMovie.SlotQualityProfile.Should().BeSameAs(slotProfile);
            trackedDownload.RemoteMovie.SlotMinimumCustomFormatScore.Should().Be(50);
            trackedDownload.RemoteMovie.SlotMovieFile.Should().BeSameAs(slotFile);
            Mocker.GetMock<ICustomFormatCalculationService>().Verify(s => s.ParseCustomFormat(trackedDownload.RemoteMovie, item.TotalSize), Times.Once());
        }

        [Test]
        public void should_preserve_exact_slot_when_movie_is_refreshed()
        {
            var history = new MovieHistory { DownloadId = "refresh-slot", MovieId = 1, EventType = MovieHistoryEventType.Grabbed, SourceTitle = "Movie.2024.Directors.Cut.1080p" };
            history.Data.Add(MovieHistory.MOVIE_EDITION_SLOT_ID, "42");
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId("refresh-slot")).Returns(new List<MovieHistory> { history });
            var downloadGrab = new DownloadHistory { DownloadId = "refresh-slot", MovieId = 1, EventType = DownloadHistoryEventType.DownloadGrabbed };
            downloadGrab.Data.Add(MovieHistory.MOVIE_EDITION_SLOT_ID, "42");
            Mocker.GetMock<IDownloadHistoryService>().Setup(s => s.GetLatestGrab("refresh-slot")).Returns(downloadGrab);
            var remoteMovie = new RemoteMovie { Movie = new Movie { Id = 1, TmdbId = 10 }, ParsedMovieInfo = new ParsedMovieInfo { MovieTitles = new List<string> { "Movie" }, Year = 2024 } };
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null)).Returns(remoteMovie);
            var client = new DownloadClientDefinition { Id = 1, Protocol = DownloadProtocol.Torrent };
            var item = new DownloadClientItem { Title = "Movie.2024.Directors.Cut.1080p", DownloadId = "refresh-slot", DownloadClientInfo = new DownloadClientItemClientInfo() };
            Subject.TrackDownload(client, item).RemoteMovie.MovieEditionSlotId.Should().Be(42);

            Subject.Handle(new MovieEditedEvent(new Movie { Id = 1, TmdbId = 10 }, remoteMovie.Movie));
            Subject.GetTrackedDownloads().Single().RemoteMovie.MovieEditionSlotId.Should().Be(42);
            Subject.Handle(new MoviesBulkEditedEvent(new List<Movie> { new Movie { Id = 1, TmdbId = 10 } }));
            Subject.GetTrackedDownloads().Single().RemoteMovie.MovieEditionSlotId.Should().Be(42);


            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetForMovie(1)).Returns(new List<MovieEditionSlot>());
            Subject.Handle(new MovieEditedEvent(new Movie { Id = 1, TmdbId = 10 }, remoteMovie.Movie));
            Subject.GetTrackedDownloads().Single().RemoteMovie.Should().BeNull();
            Subject.GetTrackedDownloads().Single().MovieEditionSlotId.Should().Be(42);

            Subject.Handle(new MovieAddedEvent(new Movie { Id = 1, TmdbId = 10 }));
            Subject.GetTrackedDownloads().Single().RemoteMovie.Should().BeNull();
            Subject.GetTrackedDownloads().Single().MovieEditionSlotId.Should().Be(42);
        }

        [Test]
        public void should_leave_remote_movie_edition_slot_id_null_when_not_in_download_history()
        {
            var movieHistory = new MovieHistory
            {
                DownloadId = "no-slot-download",
                SourceTitle = "Movie.2024.1080p",
                MovieId = 1,
                EventType = MovieHistoryEventType.Grabbed,
            };
            movieHistory.Data.Add("indexer", "TestIndexer");

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId("no-slot-download"))
                .Returns(new List<MovieHistory> { movieHistory });

            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetLatestGrab("no-slot-download"))
                .Returns((DownloadHistory)null);

            var remoteMovie = new RemoteMovie
            {
                Movie = new Movie { Id = 1 },
                ParsedMovieInfo = new ParsedMovieInfo { MovieTitles = new List<string> { "Movie" }, Year = 2024 }
            };

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null))
                .Returns(remoteMovie);

            var client = new DownloadClientDefinition { Id = 1, Protocol = DownloadProtocol.Torrent };
            var item = new DownloadClientItem
            {
                Title = "Movie.2024.1080p",
                DownloadId = "no-slot-download",
                DownloadClientInfo = new DownloadClientItemClientInfo { Protocol = client.Protocol, Id = client.Id, Name = client.Name }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.Should().NotBeNull();
            trackedDownload.RemoteMovie.Should().NotBeNull();
            trackedDownload.RemoteMovie.MovieEditionSlotId.Should().BeNull();
        }

        [Test]
        public void should_not_throw_when_processing_deleted_movie()
        {
            GivenDownloadHistory();

            var remoteMovie = new RemoteMovie
            {
                Movie = new Movie() { Id = 3 },

                ParsedMovieInfo = new ParsedMovieInfo()
                {
                    MovieTitles = { "A Movie" },
                    Year = 1998
                }
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null))
                  .Returns(default(RemoteMovie));

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<MovieHistory>());

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem()
            {
                Title = "A Movie 1998",
                DownloadId = "12345",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 1,
                    Type = "Blackhole",
                    Name = "Blackhole Client",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            Subject.TrackDownload(client, item);
            Subject.GetTrackedDownloads().Should().HaveCount(1);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null))
                  .Returns(default(RemoteMovie));

            Subject.Handle(new MoviesDeletedEvent(new List<Movie> { remoteMovie.Movie }, false, false));

            var trackedDownloads = Subject.GetTrackedDownloads();
            trackedDownloads.Should().HaveCount(1);
            trackedDownloads.First().RemoteMovie.Should().BeNull();
        }
    }
}
