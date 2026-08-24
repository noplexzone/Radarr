using System;
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
using NzbDrone.Core.Messaging.Events;
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

            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs(It.IsAny<string>(), It.IsAny<int>()))
                .Returns((string downloadId, int downloadClientId) => (Mocker.GetMock<IHistoryService>().Object.FindByDownloadId(downloadId) ?? new List<MovieHistory>())
                    .Where(h => h.EventType == MovieHistoryEventType.Grabbed)
                    .Select(h => DownloadGrab(
                        downloadId,
                        h.MovieId,
                        downloadClientId,
                        ReadTarget(h),
                        h.SourceTitle,
                        h.Date,
                        h.Data.GetValueOrDefault(MovieHistory.INDEXER),
                        Enum.TryParse(h.Data.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags) ? flags : (IndexerFlags)0))
                    .ToList());
        }

        private static MovieAcquisitionTarget ReadTarget(MovieHistory history)
        {
            return MovieAcquisitionTargetSerializer.ReadLegacyHistory(history.Data);
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
                        EventType = MovieHistoryEventType.Grabbed,
                    }
                });
        }

        private static MovieHistory Grab(string downloadId, int movieId, MovieAcquisitionTarget target, DateTime date, string sourceTitle = "Movie.2024.1080p")
        {
            var history = new MovieHistory
            {
                DownloadId = downloadId,
                MovieId = movieId,
                EventType = MovieHistoryEventType.Grabbed,
                Date = date,
                SourceTitle = sourceTitle
            };
            MovieAcquisitionTargetSerializer.Write(history.Data, target);
            return history;
        }

        private static DownloadClientItem Item(DownloadClientDefinition client, string downloadId, string title = "Movie.2024.1080p")
        {
            return new DownloadClientItem
            {
                Title = title,
                DownloadId = downloadId,
                DownloadClientInfo = new DownloadClientItemClientInfo { Protocol = client.Protocol, Id = client.Id, Name = client.Name }
            };
        }

        private static DownloadHistory DownloadGrab(
            string downloadId,
            int movieId,
            int downloadClientId,
            MovieAcquisitionTarget target,
            string sourceTitle = "Movie.2024.1080p",
            DateTime? date = null,
            string indexer = "TestIndexer",
            IndexerFlags indexerFlags = (IndexerFlags)0,
            int indexerId = 123)
        {
            var history = new DownloadHistory
            {
                DownloadId = downloadId,
                MovieId = movieId,
                DownloadClientId = downloadClientId,
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                SourceTitle = sourceTitle,
                Date = date ?? DateTime.UtcNow,
                IndexerId = indexerId,
                Release = new ReleaseInfo
                {
                    Title = sourceTitle,
                    Indexer = indexer,
                    IndexerFlags = indexerFlags,
                    IndexerId = indexerId
                }
            };
            MovieAcquisitionTargetSerializer.Write(history.Data, target);
            return history;
        }

        private void GivenMappedMovie(int movieId)
        {
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null))
                .Returns(new RemoteMovie
                {
                    Movie = new Movie { Id = movieId },
                    ParsedMovieInfo = new ParsedMovieInfo { MovieTitles = new List<string> { "Movie" }, Year = 2024 }
                });
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), movieId))
                .Returns(new RemoteMovie
                {
                    Movie = new Movie { Id = movieId },
                    ParsedMovieInfo = new ParsedMovieInfo { MovieTitles = new List<string> { "Movie" }, Year = 2024 }
                });
        }

        [Test]
        public void should_reconstruct_main_and_each_slot_as_distinct_logical_downloads()
        {
            var now = DateTime.UtcNow;
            var main = MovieAcquisitionTarget.Main;
            var slotA = MovieAcquisitionTarget.ForEditionSlot(42);
            var slotB = MovieAcquisitionTarget.ForEditionSlot(43);
            var movieGrabs = new List<MovieHistory>
            {
                Grab("shared", 1, slotB, now),
                Grab("shared", 1, slotA, now.AddMinutes(-1)),
                Grab("shared", 1, main, now.AddMinutes(-2))
            };
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId("shared")).Returns(movieGrabs);
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetForMovie(1)).Returns(new List<MovieEditionSlot>
            {
                new MovieEditionSlot { Id = 42, MovieId = 1 },
                new MovieEditionSlot { Id = 43, MovieId = 1 }
            });
            GivenMappedMovie(1);
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };

            Subject.TrackDownload(client, Item(client, "shared"));

            Subject.GetTrackedDownloads().Should().HaveCount(3);
            Subject.GetTrackedDownloads().Select(t => t.AcquisitionTarget).Should().BeEquivalentTo(new[] { main, slotA, slotB });
            Subject.GetTrackedDownloads().Should().OnlyContain(t => t.MovieId == 1);
        }

        [Test]
        public void reused_download_id_for_new_slot_should_not_mutate_terminal_old_slot()
        {
            var now = DateTime.UtcNow;
            var slotA = MovieAcquisitionTarget.ForEditionSlot(42);
            var slotB = MovieAcquisitionTarget.ForEditionSlot(43);
            var movieGrabs = new List<MovieHistory> { Grab("reused", 1, slotA, now.AddMinutes(-1)) };
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId("reused")).Returns(movieGrabs);
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetLatestDownloadHistoryItemForTarget("reused", 7, 1, It.Is<MovieAcquisitionTarget>(t => t.Equals(slotA))))
                .Returns(new DownloadHistory { EventType = DownloadHistoryEventType.DownloadFailed });
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetForMovie(1)).Returns(new List<MovieEditionSlot>
            {
                new MovieEditionSlot { Id = 42, MovieId = 1 },
                new MovieEditionSlot { Id = 43, MovieId = 1 }
            });
            GivenMappedMovie(1);
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };
            var oldSlot = Subject.TrackDownload(client, Item(client, "reused")).Single();
            oldSlot.State.Should().Be(TrackedDownloadState.Failed);

            movieGrabs.Insert(0, Grab("reused", 1, slotB, now));
            Subject.TrackDownload(client, Item(client, "reused", "Movie.2024.New.Release.1080p"));

            Subject.GetTrackedDownloads().Should().HaveCount(2);
            Subject.GetTrackedDownloads().Single(t => t.AcquisitionTarget.Equals(slotA)).Should().BeSameAs(oldSlot);
            oldSlot.State.Should().Be(TrackedDownloadState.Failed);
            Subject.GetTrackedDownloads().Single(t => t.AcquisitionTarget.Equals(slotB)).State.Should().Be(TrackedDownloadState.Downloading);
        }

        [Test]
        public void missing_target_grab_should_reconstruct_unknown_not_main()
        {
            var missingTargetGrab = new DownloadHistory
            {
                DownloadId = "missing-target",
                MovieId = 1,
                DownloadClientId = 7,
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                SourceTitle = "Movie.2024.1080p"
            };
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs("missing-target", 7))
                .Returns(new List<DownloadHistory> { missingTargetGrab });
            GivenMappedMovie(1);
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };

            var tracked = Subject.TrackDownload(client, Item(client, "missing-target")).Single();

            tracked.AcquisitionTarget.Should().BeSameAs(MovieAcquisitionTarget.Unknown);
            tracked.AcquisitionTarget.Should().NotBeSameAs(MovieAcquisitionTarget.Main);
        }

        [Test]
        public void malformed_target_should_create_only_an_unknown_fail_closed_envelope()
        {
            var malformed = new MovieHistory
            {
                DownloadId = "malformed",
                MovieId = 1,
                EventType = MovieHistoryEventType.Grabbed,
                Date = DateTime.UtcNow,
                SourceTitle = "Movie.2024.1080p"
            };
            malformed.Data[MovieHistory.ACQUISITION_TARGET] = "editionSlot";
            malformed.Data[MovieHistory.MOVIE_EDITION_SLOT_ID] = "broken";
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId("malformed")).Returns(new List<MovieHistory> { malformed });
            GivenMappedMovie(1);
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };

            var tracked = Subject.TrackDownload(client, Item(client, "malformed")).Single();

            tracked.AcquisitionTarget.Should().BeSameAs(MovieAcquisitionTarget.Unknown);
            tracked.MovieId.Should().Be(1);
            tracked.RemoteMovie.Should().BeNull();
        }

        [Test]
        public void rehydration_should_not_fall_back_to_unrelated_history()
        {
            var slotA = MovieAcquisitionTarget.ForEditionSlot(42);
            var exact = Grab("exact-only", 1, slotA, DateTime.UtcNow, "!!!");
            var unrelated = new MovieHistory
            {
                DownloadId = "exact-only",
                MovieId = 99,
                EventType = MovieHistoryEventType.MovieFileRenamed,
                Date = DateTime.UtcNow.AddMinutes(1),
                SourceTitle = "Unrelated.Movie.2024.1080p"
            };
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId("exact-only")).Returns(new List<MovieHistory> { unrelated, exact });
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null)).Returns((RemoteMovie)null);
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetForMovie(1)).Returns(new List<MovieEditionSlot> { new MovieEditionSlot { Id = 42, MovieId = 1 } });
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };

            Subject.TrackDownload(client, Item(client, "exact-only", "!!!"));

            Mocker.GetMock<IParsingService>().Verify(s => s.Map(It.IsAny<ParsedMovieInfo>(), 99), Times.Never());
        }

        [Test]
        public void reused_download_id_should_not_accept_global_mapping_for_another_movie_or_validate_its_slot()
        {
            var target = MovieAcquisitionTarget.ForEditionSlot(42);
            var grab = Grab("cross-movie", 1, target, DateTime.UtcNow);
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId("cross-movie")).Returns(new List<MovieHistory> { grab });
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null))
                .Returns(new RemoteMovie { Movie = new Movie { Id = 2 }, ParsedMovieInfo = new ParsedMovieInfo() });
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };

            var tracked = Subject.TrackDownload(client, Item(client, "cross-movie")).Single();

            tracked.MovieId.Should().Be(1);
            tracked.AcquisitionTarget.Should().Be(target);
            tracked.RemoteMovie.Should().BeNull();
            Mocker.GetMock<IMovieEditionSlotService>().Verify(s => s.GetForMovie(2), Times.Never());
        }

        [Test]
        public void should_reconstruct_union_of_current_client_download_grabs_including_download_history_only_target()
        {
            var main = MovieAcquisitionTarget.Main;
            var slot = MovieAcquisitionTarget.ForEditionSlot(42);
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId("union")).Returns(new List<MovieHistory> { Grab("union", 1, main, DateTime.UtcNow) });
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs("union", 7))
                .Returns(new List<DownloadHistory>
                {
                    DownloadGrab("union", 1, 7, slot),
                    DownloadGrab("union", 1, 7, main)
                });
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetForMovie(1)).Returns(new List<MovieEditionSlot> { new MovieEditionSlot { Id = 42, MovieId = 1 } });
            GivenMappedMovie(1);
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };

            var tracked = Subject.TrackDownload(client, Item(client, "union"));

            tracked.Should().HaveCount(2);
            tracked.Select(t => t.AcquisitionTarget).Should().BeEquivalentTo(new[] { main, slot });
        }

        [Test]
        public void should_keep_same_target_separate_for_two_movies_and_not_borrow_other_client()
        {
            var target = MovieAcquisitionTarget.Main;
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId("shared-id")).Returns(new List<MovieHistory>
            {
                Grab("shared-id", 1, target, DateTime.UtcNow),
                Grab("shared-id", 2, target, DateTime.UtcNow.AddMinutes(-1)),
                Grab("shared-id", 3, target, DateTime.UtcNow.AddMinutes(-2))
            });
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs("shared-id", 7))
                .Returns(new List<DownloadHistory>
                {
                    DownloadGrab("shared-id", 1, 7, target),
                    DownloadGrab("shared-id", 2, 7, target)
                });
            GivenMappedMovie(1);
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), 2)).Returns(new RemoteMovie { Movie = new Movie { Id = 2 }, ParsedMovieInfo = new ParsedMovieInfo() });
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };

            var tracked = Subject.TrackDownload(client, Item(client, "shared-id"));

            tracked.Select(t => t.MovieId).Should().BeEquivalentTo(new[] { 1, 2 });
            tracked.Should().NotContain(t => t.MovieId == 3);
        }

        [Test]
        public void ambiguous_mapping_should_preserve_and_warn_exact_logical_envelope()
        {
            var target = MovieAcquisitionTarget.Main;
            var grab = Grab("ambiguous", 1, target, DateTime.UtcNow);
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId("ambiguous")).Returns(new List<MovieHistory> { grab });
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), 1))
                .Throws(new MultipleMoviesFoundException(new List<Movie> { new Movie { Id = 1 }, new Movie { Id = 2 } }, "ambiguous"));
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };

            var tracked = Subject.TrackDownload(client, Item(client, "ambiguous")).Single();

            tracked.MovieId.Should().Be(1);
            tracked.AcquisitionTarget.Should().Be(target);
            tracked.RemoteMovie.Should().BeNull();
            tracked.Status.Should().Be(TrackedDownloadStatus.Warning);
            tracked.StatusMessages.Should().ContainSingle();
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

            var trackedDownload = Subject.TrackDownload(client, item).Single();

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

            var trackedDownload = Subject.TrackDownload(client, item).Single();

            trackedDownload.Should().NotBeNull();
            trackedDownload.RemoteMovie.Should().NotBeNull();
            trackedDownload.RemoteMovie.Release.Should().NotBeNull();
            trackedDownload.RemoteMovie.Release.Indexer.Should().Be("MyIndexer (Prowlarr)");
        }

        [Test]
        public void should_use_only_exact_download_history_context_when_clients_share_identity()
        {
            var target = MovieAcquisitionTarget.Main;
            var currentGrabDate = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            var otherClientGrabDate = currentGrabDate.AddHours(1);
            var currentGrab = DownloadGrab(
                "shared-context",
                1,
                7,
                target,
                "Current.Movie.2024.1080p",
                currentGrabDate,
                "CurrentIndexer",
                IndexerFlags.G_Freeleech,
                701);
            var otherClientMovieHistory = Grab(
                "shared-context",
                1,
                target,
                otherClientGrabDate,
                "Other.Client.Movie.2024.1080p");
            otherClientMovieHistory.Data[MovieHistory.INDEXER] = "OtherClientIndexer";
            otherClientMovieHistory.Data["indexerFlags"] = IndexerFlags.Nuked.ToString();

            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs("shared-context", 7))
                .Returns(new List<DownloadHistory> { currentGrab });
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId("shared-context"))
                .Returns(new List<MovieHistory> { otherClientMovieHistory });
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null))
                .Returns((RemoteMovie)null);
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), 1))
                .Returns(new RemoteMovie
                {
                    Movie = new Movie { Id = 1 },
                    ParsedMovieInfo = new ParsedMovieInfo()
                });
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };

            var tracked = Subject.TrackDownload(client, Item(client, "shared-context", "!!!")).Single();

            tracked.RemoteMovie.Should().NotBeNull();
            tracked.Added.Should().Be(currentGrabDate);
            tracked.Indexer.Should().Be("CurrentIndexer");
            tracked.RemoteMovie.Release.Should().BeSameAs(currentGrab.Release);
            tracked.RemoteMovie.Release.IndexerFlags.Should().Be(IndexerFlags.G_Freeleech);
            tracked.RemoteMovie.Release.IndexerId.Should().Be(701);
            Mocker.GetMock<IHistoryService>()
                .Verify(s => s.FindByDownloadId(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void bare_download_id_should_fail_closed_while_exact_key_resolves_selected_slot()
        {
            var slotA = MovieAcquisitionTarget.ForEditionSlot(42);
            var slotB = MovieAcquisitionTarget.ForEditionSlot(43);
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs("shared-identity", 7))
                .Returns(new List<DownloadHistory>
                {
                    DownloadGrab("shared-identity", 1, 7, slotA),
                    DownloadGrab("shared-identity", 1, 7, slotB)
                });
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetForMovie(1)).Returns(new List<MovieEditionSlot>
            {
                new MovieEditionSlot { Id = 42, MovieId = 1 },
                new MovieEditionSlot { Id = 43, MovieId = 1 }
            });
            GivenMappedMovie(1);
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };
            var tracked = Subject.TrackDownload(client, Item(client, "shared-identity"));

            Subject.Find("shared-identity").Should().BeNull();
            Subject.Find(new TrackedDownloadKey(7, "shared-identity", 1, slotA))
                .Should().BeSameAs(tracked.Single(t => t.AcquisitionTarget.Equals(slotA)));
            Subject.FindByDownloadClient(7, "shared-identity").Should().BeEquivalentTo(tracked);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void malformed_exact_download_id_should_fail_closed(string downloadId)
        {
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs("valid-key", 7))
                .Returns(new List<DownloadHistory> { DownloadGrab("valid-key", 1, 7, MovieAcquisitionTarget.Main) });
            GivenMappedMovie(1);
            var tracked = Subject.TrackDownload(client, Item(client, "valid-key")).Single();
            var malformedKey = new TrackedDownloadKey(7, downloadId, 1, MovieAcquisitionTarget.Main);

            Subject.Find(malformedKey).Should().BeNull();
            Subject.StopTracking(malformedKey);

            Subject.GetTrackedDownloads().Should().ContainSingle().Which.Should().BeSameAs(tracked);
            Mocker.GetMock<IEventAggregator>()
                .Verify(e => e.PublishEvent(It.IsAny<TrackedDownloadsRemovedEvent>()), Times.Never());
        }

        [Test]
        public void null_target_exact_key_should_fail_closed()
        {
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs("valid-key", 7))
                .Returns(new List<DownloadHistory> { DownloadGrab("valid-key", 1, 7, MovieAcquisitionTarget.Main) });
            GivenMappedMovie(1);
            var tracked = Subject.TrackDownload(client, Item(client, "valid-key")).Single();
            var malformedKey = new TrackedDownloadKey(7, "valid-key", 1, null);

            Subject.Find(malformedKey).Should().BeNull();
            Subject.StopTracking(malformedKey);

            Subject.GetTrackedDownloads().Should().ContainSingle().Which.Should().BeSameAs(tracked);
            Mocker.GetMock<IEventAggregator>()
                .Verify(e => e.PublishEvent(It.IsAny<TrackedDownloadsRemovedEvent>()), Times.Never());
        }

        [Test]
        public void exact_stop_should_remove_only_selected_slot_and_publish_only_that_envelope()
        {
            var slotA = MovieAcquisitionTarget.ForEditionSlot(42);
            var slotB = MovieAcquisitionTarget.ForEditionSlot(43);
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs("shared-stop", 7))
                .Returns(new List<DownloadHistory>
                {
                    DownloadGrab("shared-stop", 1, 7, slotA),
                    DownloadGrab("shared-stop", 1, 7, slotB)
                });
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetForMovie(1)).Returns(new List<MovieEditionSlot>
            {
                new MovieEditionSlot { Id = 42, MovieId = 1 },
                new MovieEditionSlot { Id = 43, MovieId = 1 }
            });
            GivenMappedMovie(1);
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };
            var tracked = Subject.TrackDownload(client, Item(client, "shared-stop"));
            var selected = tracked.Single(t => t.AcquisitionTarget.Equals(slotA));

            Subject.StopTracking(selected.Key);

            Subject.GetTrackedDownloads().Should().ContainSingle().Which.AcquisitionTarget.Should().Be(slotB);
            Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.Is<TrackedDownloadsRemovedEvent>(x =>
                x.TrackedDownloads.Count == 1 && x.TrackedDownloads.Single() == selected)), Times.Once());
        }

        [Test]
        public void bare_stop_should_preserve_single_target_behavior_but_fail_closed_for_siblings()
        {
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs("single-stop", 7))
                .Returns(new List<DownloadHistory> { DownloadGrab("single-stop", 1, 7, MovieAcquisitionTarget.Main) });
            GivenMappedMovie(1);
            Subject.TrackDownload(client, Item(client, "single-stop"));

            Subject.StopTracking("single-stop");

            Subject.GetTrackedDownloads().Should().BeEmpty();

            var slotA = MovieAcquisitionTarget.ForEditionSlot(42);
            var slotB = MovieAcquisitionTarget.ForEditionSlot(43);
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs("ambiguous-stop", 7))
                .Returns(new List<DownloadHistory>
                {
                    DownloadGrab("ambiguous-stop", 1, 7, slotA),
                    DownloadGrab("ambiguous-stop", 1, 7, slotB)
                });
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetForMovie(1)).Returns(new List<MovieEditionSlot>
            {
                new MovieEditionSlot { Id = 42, MovieId = 1 },
                new MovieEditionSlot { Id = 43, MovieId = 1 }
            });
            Subject.TrackDownload(client, Item(client, "ambiguous-stop"));

            Subject.StopTracking("ambiguous-stop");

            Subject.GetTrackedDownloads().Should().HaveCount(2);
        }

        [Test]
        public void explicitly_named_physical_group_stop_should_remove_all_siblings_for_that_client_only()
        {
            var slotA = MovieAcquisitionTarget.ForEditionSlot(42);
            var slotB = MovieAcquisitionTarget.ForEditionSlot(43);
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetGrabs("physical-stop", 7))
                .Returns(new List<DownloadHistory>
                {
                    DownloadGrab("physical-stop", 1, 7, slotA),
                    DownloadGrab("physical-stop", 1, 7, slotB)
                });
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetForMovie(1)).Returns(new List<MovieEditionSlot>
            {
                new MovieEditionSlot { Id = 42, MovieId = 1 },
                new MovieEditionSlot { Id = 43, MovieId = 1 }
            });
            GivenMappedMovie(1);
            var client = new DownloadClientDefinition { Id = 7, Protocol = DownloadProtocol.Torrent };
            Subject.TrackDownload(client, Item(client, "physical-stop"));

            Subject.StopTrackingPhysicalDownload(7, "physical-stop");

            Subject.GetTrackedDownloads().Should().BeEmpty();
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

            var trackedDownload = Subject.TrackDownload(client, item).Single();

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
            Subject.TrackDownload(client, item).Single().RemoteMovie.MovieEditionSlotId.Should().Be(42);

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

            var trackedDownload = Subject.TrackDownload(client, item).Single();

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
