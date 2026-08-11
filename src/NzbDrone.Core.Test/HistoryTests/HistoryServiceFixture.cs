using System.Collections.Generic;
using System.IO;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Test.Qualities;

namespace NzbDrone.Core.Test.HistoryTests
{
    public class HistoryServiceFixture : CoreTest<HistoryService>
    {
        private QualityProfile _profile;
        private QualityProfile _profileCustom;

        [SetUp]
        public void Setup()
        {
            _profile = new QualityProfile { Cutoff = Quality.WEBDL720p.Id, Items = QualityFixture.GetDefaultQualities() };
            _profileCustom = new QualityProfile { Cutoff = Quality.WEBDL720p.Id, Items = QualityFixture.GetDefaultQualities(Quality.DVD) };
        }

        [Test]
        public void should_return_null_if_no_history()
        {
            Mocker.GetMock<IHistoryRepository>()
                .Setup(v => v.GetBestQualityInHistory(2))
                .Returns(new List<QualityModel>());

            var quality = Subject.GetBestQualityInHistory(_profile, 2);

            quality.Should().BeNull();
        }

        [Test]
        public void should_return_best_quality()
        {
            Mocker.GetMock<IHistoryRepository>()
                .Setup(v => v.GetBestQualityInHistory(2))
                .Returns(new List<QualityModel> { new QualityModel(Quality.DVD), new QualityModel(Quality.Bluray1080p) });

            var quality = Subject.GetBestQualityInHistory(_profile, 2);

            quality.Should().Be(new QualityModel(Quality.Bluray1080p));
        }

        [Test]
        public void should_return_best_quality_with_custom_order()
        {
            Mocker.GetMock<IHistoryRepository>()
                .Setup(v => v.GetBestQualityInHistory(2))
                .Returns(new List<QualityModel> { new QualityModel(Quality.DVD), new QualityModel(Quality.Bluray1080p) });

            var quality = Subject.GetBestQualityInHistory(_profileCustom, 2);

            quality.Should().Be(new QualityModel(Quality.DVD));
        }

        [Test]
        public void should_use_file_name_for_source_title_if_scene_name_is_null()
        {
            var movie = Builder<Movie>.CreateNew().Build();
            var movieFile = Builder<MovieFile>.CreateNew()
                                                  .With(f => f.SceneName = null)
                                                  .Build();

            var localMovie = new LocalMovie()
            {
                Movie = movie,
                Path = @"C:\Test\Unsorted\Movie.2011.mkv"
            };

            var downloadClientItem = new DownloadClientItem
            {
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = DownloadProtocol.Usenet,
                    Id = 1,
                    Name = "sab"
                },
                DownloadId = "abcd"
            };

            Subject.Handle(new MovieFileImportedEvent(localMovie, movieFile, new List<DeletedMovieFile>(), true, downloadClientItem));

            Mocker.GetMock<IHistoryRepository>()
                .Verify(v => v.Insert(It.Is<MovieHistory>(h => h.SourceTitle == Path.GetFileNameWithoutExtension(localMovie.Path))));
        }

        [Test]
        public void should_store_movie_edition_slot_id_for_grabbed_release()
        {
            var remoteMovie = new RemoteMovie
            {
                Movie = Builder<Movie>.CreateNew().With(m => m.Id = 12).Build(),
                ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(Quality.WEBDL1080p) },
                Release = new ReleaseInfo { Title = "Movie.2024.Directors.Cut.1080p", Indexer = "TestIndexer" },
                MovieEditionSlotId = 42
            };

            Subject.Handle(new MovieGrabbedEvent(remoteMovie) { DownloadId = "download-1" });

            Mocker.GetMock<IHistoryRepository>()
                .Verify(v => v.Insert(It.Is<MovieHistory>(h =>
                    h.EventType == MovieHistoryEventType.Grabbed &&
                    h.Data[MovieHistory.MOVIE_EDITION_SLOT_ID] == "42")));
        }

        [Test]
        public void should_not_store_movie_edition_slot_id_for_normal_grabbed_release()
        {
            var remoteMovie = new RemoteMovie
            {
                Movie = Builder<Movie>.CreateNew().With(m => m.Id = 12).Build(),
                ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(Quality.WEBDL1080p) },
                Release = new ReleaseInfo { Title = "Movie.2024.1080p", Indexer = "TestIndexer" }
            };

            Subject.Handle(new MovieGrabbedEvent(remoteMovie) { DownloadId = "download-1" });

            Mocker.GetMock<IHistoryRepository>()
                .Verify(v => v.Insert(It.Is<MovieHistory>(h =>
                    h.EventType == MovieHistoryEventType.Grabbed &&
                    !h.Data.ContainsKey(MovieHistory.MOVIE_EDITION_SLOT_ID))));
        }

        [Test]
        public void should_store_exact_movie_edition_slot_id_for_ignored_download()
        {
            var tracked = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownload
            {
                RemoteMovie = new RemoteMovie { MovieEditionSlotId = 42, ParsedMovieInfo = new ParsedMovieInfo(), Release = new ReleaseInfo() },
                DownloadItem = new DownloadClientItem { DownloadClientInfo = new DownloadClientItemClientInfo(), TotalSize = 1 }
            };
            var ignored = new DownloadIgnoredEvent
            {
                MovieId = 1,
                DownloadClientInfo = new DownloadClientItemClientInfo(),
                TrackedDownload = tracked
            };

            Subject.Handle(ignored);

            Mocker.GetMock<IHistoryRepository>().Verify(v => v.Insert(It.Is<MovieHistory>(h => h.EventType == MovieHistoryEventType.DownloadIgnored && h.Data[MovieHistory.MOVIE_EDITION_SLOT_ID] == "42")));
        }

        [Test]
        public void should_store_exact_movie_edition_slot_id_for_failed_download_without_tracked_state()
        {
            Subject.Handle(new DownloadFailedEvent { MovieId = 1, MovieEditionSlotId = 42 });

            Mocker.GetMock<IHistoryRepository>().Verify(v => v.Insert(It.Is<MovieHistory>(h => h.EventType == MovieHistoryEventType.DownloadFailed && h.Data[MovieHistory.MOVIE_EDITION_SLOT_ID] == "42")));
        }

        [Test]
        public void should_store_durable_movie_edition_slot_id_for_import()
        {
            var movie = Builder<Movie>.CreateNew().Build();
            Mocker.GetMock<IHistoryRepository>().Setup(r => r.FindDownloadHistory(movie.Id, It.IsAny<QualityModel>())).Returns(new List<MovieHistory>());
            var movieFile = Builder<MovieFile>.CreateNew().With(f => f.MovieEditionSlotId = 42).Build();
            var localMovie = new LocalMovie { Movie = movie, Path = @"C:\Test\Movie.mkv" };
            Subject.Handle(new MovieFileImportedEvent(localMovie, movieFile, new List<DeletedMovieFile>(), true, null));
            Mocker.GetMock<IHistoryRepository>().Verify(v => v.Insert(It.Is<MovieHistory>(h => h.EventType == MovieHistoryEventType.DownloadFolderImported && h.Data[MovieHistory.MOVIE_EDITION_SLOT_ID] == "42")));
        }
    }
}
