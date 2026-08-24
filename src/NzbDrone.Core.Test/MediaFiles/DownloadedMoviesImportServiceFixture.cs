using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class DownloadedMoviesImportServiceFixture : CoreTest<DownloadedMovieImportService>
    {
        private string _droneFactory = "c:\\drop\\".AsOsAgnostic();
        private string[] _subFolders = new[] { "c:\\root\\foldername".AsOsAgnostic() };
        private string[] _videoFiles = new[] { "c:\\root\\foldername\\47.ronin.2013.ext".AsOsAgnostic() };

        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IDiskScanService>().Setup(c => c.GetVideoFiles(It.IsAny<string>(), It.IsAny<bool>()))
                  .Returns(_videoFiles);

            Mocker.GetMock<IDiskScanService>().Setup(c => c.FilterPaths(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<bool>()))
                  .Returns<string, IEnumerable<string>, bool>((b, s, e) => s.ToList());

            Mocker.GetMock<IDiskProvider>().Setup(c => c.GetDirectories(It.IsAny<string>()))
                  .Returns(_subFolders);

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FolderExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IImportApprovedMovie>()
                  .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, null, ImportMode.Auto))
                  .Returns(new List<ImportResult>());

            var downloadItem = Builder<DownloadClientItem>.CreateNew()
                .With(v => v.DownloadId = "sab1")
                .With(v => v.Status = DownloadItemStatus.Downloading)
                .Build();

            var remoteMovie = Builder<RemoteMovie>.CreateNew()
                .With(v => v.Movie = new Movie())
                .Build();

            _trackedDownload = new TrackedDownload
            {
                DownloadItem = downloadItem,
                RemoteMovie = remoteMovie,
                State = TrackedDownloadState.Downloading
            };
        }

        private void GivenValidMovie()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie(It.IsAny<string>()))
                  .Returns(Builder<Movie>.CreateNew().Build());
        }

        private void GivenSuccessfulImport()
        {
            var localMovie = new LocalMovie();

            var imported = new List<ImportDecision>();
            imported.Add(new ImportDecision(localMovie));

            Mocker.GetMock<IMakeImportDecision>()
                  .Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), null, true, true))
                  .Returns(imported);

            Mocker.GetMock<IImportApprovedMovie>()
                  .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), It.IsAny<bool>(), It.IsAny<DownloadClientItem>(), It.IsAny<ImportMode>()))
                  .Returns(imported.Select(i => new ImportResult(i)).ToList())
                  .Callback(() => WasImportedResponse());
        }

        private void WasImportedResponse()
        {
            Mocker.GetMock<IDiskScanService>().Setup(c => c.GetVideoFiles(It.IsAny<string>(), It.IsAny<bool>()))
                  .Returns(System.Array.Empty<string>());
        }

        [Test]
        public void should_search_for_series_using_folder_name()
        {
            Subject.ProcessRootFolder(new DirectoryInfo(_droneFactory));

            Mocker.GetMock<IParsingService>().Verify(c => c.GetMovie("foldername"), Times.Once());
        }

        [Test]
        public void should_skip_if_file_is_in_use_by_another_process()
        {
            GivenValidMovie();

            Mocker.GetMock<IDiskProvider>().Setup(c => c.IsFileLocked(It.IsAny<string>()))
                  .Returns(true);

            Subject.ProcessRootFolder(new DirectoryInfo(_droneFactory));

            VerifyNoImport();
        }

        [Test]
        public void should_skip_if_no_series_found()
        {
            Mocker.GetMock<IParsingService>().Setup(c => c.GetMovie("foldername")).Returns((Movie)null);

            Subject.ProcessRootFolder(new DirectoryInfo(_droneFactory));

            Mocker.GetMock<IMakeImportDecision>()
                .Verify(c => c.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), It.IsAny<ParsedMovieInfo>(), It.IsAny<bool>()),
                    Times.Never());

            VerifyNoImport();
        }

        [Test]
        public void should_not_import_if_folder_is_a_series_path()
        {
            GivenValidMovie();

            Mocker.GetMock<IMovieService>()
                  .Setup(s => s.MoviePathExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskScanService>()
                  .Setup(c => c.GetVideoFiles(It.IsAny<string>(), It.IsAny<bool>()))
                  .Returns(System.Array.Empty<string>());

            Subject.ProcessRootFolder(new DirectoryInfo(_droneFactory));

            Mocker.GetMock<IDiskScanService>()
                  .Verify(v => v.GetVideoFiles(It.IsAny<string>(), true), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_delete_folder_if_no_files_were_imported()
        {
            Mocker.GetMock<IImportApprovedMovie>()
                  .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), false, null, ImportMode.Auto))
                  .Returns(new List<ImportResult>());

            Subject.ProcessRootFolder(new DirectoryInfo(_droneFactory));

            Mocker.GetMock<IDiskProvider>()
                  .Verify(v => v.GetFolderSize(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_not_delete_folder_after_import()
        {
            GivenValidMovie();

            GivenSuccessfulImport();

            _trackedDownload.DownloadItem.CanMoveFiles = false;

            Subject.ProcessPath(_droneFactory, ImportMode.Auto, _trackedDownload.RemoteMovie.Movie, _trackedDownload.DownloadItem);

            Mocker.GetMock<IDiskProvider>()
                  .Verify(v => v.DeleteFolder(It.IsAny<string>(), true), Times.Never());
        }

        [Test]
        public void should_delete_folder_if_importmode_move()
        {
            GivenValidMovie();

            GivenSuccessfulImport();

            _trackedDownload.DownloadItem.CanMoveFiles = false;

            Subject.ProcessPath(_droneFactory, ImportMode.Move, _trackedDownload.RemoteMovie.Movie, _trackedDownload.DownloadItem);

            Mocker.GetMock<IDiskProvider>()
                  .Verify(v => v.DeleteFolder(It.IsAny<string>(), true), Times.Once());
        }

        [Test]
        public void should_not_delete_folder_if_importmode_copy()
        {
            GivenValidMovie();

            GivenSuccessfulImport();

            _trackedDownload.DownloadItem.CanMoveFiles = true;

            Subject.ProcessPath(_droneFactory, ImportMode.Copy, _trackedDownload.RemoteMovie.Movie, _trackedDownload.DownloadItem);

            Mocker.GetMock<IDiskProvider>()
                  .Verify(v => v.DeleteFolder(It.IsAny<string>(), true), Times.Never());
        }

        [Test]
        public void should_not_delete_folder_if_files_were_imported_and_video_files_remain()
        {
            GivenValidMovie();

            var localMovie = new LocalMovie();

            var imported = new List<ImportDecision>();
            imported.Add(new ImportDecision(localMovie));

            Mocker.GetMock<IMakeImportDecision>()
                  .Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), null, true))
                  .Returns(imported);

            Mocker.GetMock<IImportApprovedMovie>()
                  .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, null, ImportMode.Auto))
                  .Returns(imported.Select(i => new ImportResult(i)).ToList());

            Subject.ProcessRootFolder(new DirectoryInfo(_droneFactory));

            Mocker.GetMock<IDiskProvider>()
                  .Verify(v => v.DeleteFolder(It.IsAny<string>(), true), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_delete_folder_if_files_were_imported_and_only_sample_files_remain()
        {
            GivenValidMovie();

            var localMovie = new LocalMovie();

            var imported = new List<ImportDecision>();
            imported.Add(new ImportDecision(localMovie));

            Mocker.GetMock<IMakeImportDecision>()
                  .Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), null, true))
                  .Returns(imported);

            Mocker.GetMock<IImportApprovedMovie>()
                  .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, null, ImportMode.Auto))
                  .Returns(imported.Select(i => new ImportResult(i)).ToList());

            Mocker.GetMock<IDetectSample>()
                  .Setup(s => s.IsSample(It.IsAny<MovieMetadata>(),
                      It.IsAny<string>()))
                  .Returns(DetectSampleResult.Sample);

            Subject.ProcessRootFolder(new DirectoryInfo(_droneFactory));

            Mocker.GetMock<IDiskProvider>()
                  .Verify(v => v.DeleteFolder(It.IsAny<string>(), true), Times.Once());
        }

        [TestCase("_UNPACK_")]
        [TestCase("_FAILED_")]
        public void should_remove_unpack_from_folder_name(string prefix)
        {
            var folderName = "47.ronin.2013.hdtv-lol";
            var folders = new[] { string.Format(@"C:\Test\Unsorted\{0}{1}", prefix, folderName).AsOsAgnostic() };

            Mocker.GetMock<IDiskProvider>()
                  .Setup(c => c.GetDirectories(It.IsAny<string>()))
                  .Returns(folders);

            Subject.ProcessRootFolder(new DirectoryInfo(_droneFactory));

            Mocker.GetMock<IParsingService>()
                .Verify(v => v.GetMovie(folderName), Times.Once());

            Mocker.GetMock<IParsingService>()
                .Verify(v => v.GetMovie(It.Is<string>(s => s.StartsWith(prefix))), Times.Never());
        }

        [Test]
        public void should_return_importresult_on_unknown_movie()
        {
            Mocker.GetMock<IDiskProvider>().Setup(c => c.FolderExists(It.IsAny<string>()))
                  .Returns(false);

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(It.IsAny<string>()))
                  .Returns(true);

            var fileName = @"C:\folder\file.mkv".AsOsAgnostic();

            var result = Subject.ProcessPath(fileName);

            result.Should().HaveCount(1);
            result.First().ImportDecision.Should().NotBeNull();
            result.First().ImportDecision.LocalMovie.Should().NotBeNull();
            result.First().ImportDecision.LocalMovie.Path.Should().Be(fileName);
            result.First().Result.Should().Be(ImportResultType.Rejected);
        }

        [Test]
        public void should_not_delete_if_there_is_large_rar_file()
        {
            GivenValidMovie();

            var localMovie = new LocalMovie();

            var imported = new List<ImportDecision>();
            imported.Add(new ImportDecision(localMovie));

            Mocker.GetMock<IMakeImportDecision>()
                  .Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), null, true))
                  .Returns(imported);

            Mocker.GetMock<IImportApprovedMovie>()
                  .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, null, ImportMode.Auto))
                  .Returns(imported.Select(i => new ImportResult(i)).ToList());

            Mocker.GetMock<IDetectSample>()
                  .Setup(s => s.IsSample(It.IsAny<MovieMetadata>(),
                      It.IsAny<string>()))
                  .Returns(DetectSampleResult.Sample);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFiles(It.IsAny<string>(), true))
                  .Returns(new[] { _videoFiles.First().Replace(".ext", ".rar") });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(It.IsAny<string>()))
                  .Returns(15.Megabytes());

            Subject.ProcessRootFolder(new DirectoryInfo(_droneFactory));

            Mocker.GetMock<IDiskProvider>()
                  .Verify(v => v.DeleteFolder(It.IsAny<string>(), true), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_use_folder_if_folder_import()
        {
            GivenValidMovie();

            var folderName = @"C:\media\ba09030e-1234-1234-1234-123456789abc\[HorribleSubs] American Psycho (2000) [720p]".AsOsAgnostic();
            var fileName = @"C:\media\ba09030e-1234-1234-1234-123456789abc\[HorribleSubs] American Psycho (2000) [720p]\[HorribleSubs] American Psycho (2000) [720p].mkv".AsOsAgnostic();

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FolderExists(folderName))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>().Setup(c => c.GetFiles(folderName, false))
                  .Returns(new[] { fileName });

            var localMovie = new LocalMovie();

            var imported = new List<ImportDecision>();
            imported.Add(new ImportDecision(localMovie));

            Subject.ProcessPath(fileName);

            Mocker.GetMock<IMakeImportDecision>()
                  .Verify(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), It.IsAny<ParsedMovieInfo>(), true), Times.Once());
        }

        [Test]
        public void should_not_use_folder_if_file_import()
        {
            GivenValidMovie();

            var fileName = @"C:\media\ba09030e-1234-1234-1234-123456789abc\Torrents\[HorribleSubs] 47 Ronin (2013) [720p].mkv".AsOsAgnostic();

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FolderExists(fileName))
                  .Returns(false);

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(fileName))
                  .Returns(true);

            var localMovie = new LocalMovie();

            var imported = new List<ImportDecision>();
            imported.Add(new ImportDecision(localMovie));

            var result = Subject.ProcessPath(fileName);

            Mocker.GetMock<IMakeImportDecision>()
                  .Verify(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), null, true), Times.Once());
        }

        [Test]
        public void should_not_process_if_file_and_folder_do_not_exist()
        {
            var folderName = @"C:\media\ba09030e-1234-1234-1234-123456789abc\[HorribleSubs] 47 Ronin (2013) [720p]".AsOsAgnostic();

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FolderExists(folderName))
                  .Returns(false);

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(folderName))
                  .Returns(false);

            Subject.ProcessPath(folderName).Should().BeEmpty();

            Mocker.GetMock<IParsingService>()
                .Verify(v => v.GetMovie(It.IsAny<string>()), Times.Never());

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_not_delete_if_no_files_were_imported()
        {
            GivenValidMovie();

            var localMovie = new LocalMovie();

            var imported = new List<ImportDecision>();
            imported.Add(new ImportDecision(localMovie));

            Mocker.GetMock<IMakeImportDecision>()
                  .Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), null, true))
                  .Returns(imported);

            Mocker.GetMock<IImportApprovedMovie>()
                  .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, null, ImportMode.Auto))
                  .Returns(new List<ImportResult>());

            Mocker.GetMock<IDetectSample>()
                  .Setup(s => s.IsSample(It.IsAny<MovieMetadata>(),
                      It.IsAny<string>()))
                  .Returns(DetectSampleResult.Sample);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(It.IsAny<string>()))
                  .Returns(15.Megabytes());

            Subject.ProcessRootFolder(new DirectoryInfo(_droneFactory));

            Mocker.GetMock<IDiskProvider>()
                  .Verify(v => v.DeleteFolder(It.IsAny<string>(), true), Times.Never());
        }

        [Test]
        public void should_return_rejection_if_nothing_imported_and_contains_rar_file()
        {
            GivenValidMovie();

            var path = @"C:\media\ba09030e-1234-1234-1234-123456789abc\[HorribleSubs] American Psycho (2000) [720p]\[HorribleSubs] American Psycho (2000) [720p].mkv".AsOsAgnostic();
            var imported = new List<ImportDecision>();

            Mocker.GetMock<IMakeImportDecision>()
                .Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), null, true, true))
                .Returns(imported);

            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, null, ImportMode.Auto))
                .Returns(imported.Select(i => new ImportResult(i)).ToList());

            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.GetFiles(It.IsAny<string>(), true))
                .Returns(new[] { _videoFiles.First().Replace(".ext", ".rar") });

            var result = Subject.ProcessPath(path);

            result.Count.Should().Be(1);
            result.First().Result.Should().Be(ImportResultType.Rejected);
        }

        [Test]
        public void should_return_rejection_if_nothing_imported_and_contains_executable_file()
        {
            GivenValidMovie();

            var path = @"C:\media\ba09030e-1234-1234-1234-123456789abc\[HorribleSubs] American Psycho (2000) [720p]\[HorribleSubs] American Psycho (2000) [720p].mkv".AsOsAgnostic();
            var imported = new List<ImportDecision>();

            Mocker.GetMock<IMakeImportDecision>()
                .Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), null, true, true))
                .Returns(imported);

            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, null, ImportMode.Auto))
                .Returns(imported.Select(i => new ImportResult(i)).ToList());

            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.GetFiles(It.IsAny<string>(), true))
                .Returns(new[] { _videoFiles.First().Replace(".ext", ".exe") });

            var result = Subject.ProcessPath(path);

            result.Count.Should().Be(1);
            result.First().Result.Should().Be(ImportResultType.Rejected);
        }

        private static PhysicalDownloadImportEnvelope Envelope(int clientId, string downloadId, int movieId, MovieAcquisitionTarget target)
        {
            var item = new DownloadClientItem { DownloadId = downloadId, OutputPath = new OsPath(@"C:\drop\shared".AsOsAgnostic()) };
            var remote = new RemoteMovie { Movie = new Movie { Id = movieId }, AcquisitionTarget = target };
            return new PhysicalDownloadImportEnvelope(new TrackedDownloadKey(clientId, downloadId, movieId, target), remote, item);
        }

        private void GivenGroupedDecisions()
        {
            Mocker.GetMock<IMakeImportDecision>()
                .Setup(service => service.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>(), It.IsAny<ParsedMovieInfo>(), true))
                .Returns<List<string>, Movie, DownloadClientItem, ParsedMovieInfo, bool>((files, movie, _, _, _) =>
                    files.Select(file => new ImportDecision(new LocalMovie { Path = file, Movie = movie })).ToList());
            Mocker.GetMock<IMakeImportDecision>()
                .Setup(service => service.GetDecision(It.IsAny<LocalMovie>(), It.IsAny<DownloadClientItem>()))
                .Returns<LocalMovie, DownloadClientItem>((movie, _) => new ImportDecision(movie));
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(service => service.GetForMovie(It.IsAny<int>()))
                .Returns(new List<MovieEditionSlot>());
        }

        [Test]
        public void physical_group_should_use_exact_download_history_grab_without_movie_history_fallback()
        {
            var path = @"C:\drop\shared\Movie.mkv".AsOsAgnostic();
            Mocker.GetMock<IDiskScanService>().Setup(service => service.GetVideoFiles(It.IsAny<string>(), It.IsAny<bool>())).Returns(new[] { path });
            var main = Envelope(7, "shared", 1, MovieAcquisitionTarget.Main);
            var slot = Envelope(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42));
            GivenGroupedDecisions();
            Mocker.GetMock<IMovieEditionMatcher>().Setup(service => service.Match(It.IsAny<RemoteMovie>(), It.IsAny<IReadOnlyCollection<MovieEditionSlot>>()))
                .Returns(EditionMatchResult.NoEvidence());
            var grab = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                DownloadId = "shared",
                DownloadClientId = 7,
                MovieId = 1,
                SourceTitle = "Exact.Source",
                Release = new ReleaseInfo { Title = "Exact.Release", Indexer = "Exact.Indexer", Size = 123 }
            };
            MovieAcquisitionTargetSerializer.Write(grab.Data, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(service => service.GetLatestGrabForTarget("shared", 7, 1, MovieAcquisitionTarget.Main))
                .Returns(grab);
            List<ImportDecision> captured = null;
            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(service => service.Import(It.IsAny<List<ImportDecision>>(), true, main.ImportItem, ImportMode.Copy))
                .Callback<List<ImportDecision>, bool, DownloadClientItem, ImportMode>((decisions, _, _, _) => captured = decisions)
                .Returns<List<ImportDecision>, bool, DownloadClientItem, ImportMode>((decisions, _, _, _) => decisions.Select(decision => new ImportResult(decision)).ToList());

            Subject.ProcessPhysicalGroup(@"C:\drop\shared".AsOsAgnostic(), new[] { main, slot });

            captured.Single().LocalMovie.Release.Title.Should().Be("Exact.Source");
            captured.Single().LocalMovie.Release.Indexer.Should().Be("Exact.Indexer");
            captured.Single().LocalMovie.Release.Size.Should().Be(123);
            captured.Single().LocalMovie.Release.MovieIds.Should().Equal(1);
            captured.Single().LocalMovie.Release.AcquisitionTarget.Should().Be(MovieAcquisitionTarget.Main);
            Mocker.GetMock<IHistoryService>().Verify(service => service.FindByDownloadId(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void physical_group_should_support_same_target_value_for_distinct_movies_and_import_once()
        {
            var firstPath = @"C:\drop\shared\Movie1.mkv".AsOsAgnostic();
            var secondPath = @"C:\drop\shared\Movie2.mkv".AsOsAgnostic();
            Mocker.GetMock<IDiskScanService>().Setup(service => service.GetVideoFiles(It.IsAny<string>(), It.IsAny<bool>())).Returns(new[] { firstPath, secondPath });
            var first = Envelope(7, "shared", 1, MovieAcquisitionTarget.Main);
            var second = Envelope(7, "shared", 2, MovieAcquisitionTarget.Main);
            GivenGroupedDecisions();
            Mocker.GetMock<IMovieEditionMatcher>()
                .Setup(service => service.Match(It.IsAny<RemoteMovie>(), It.IsAny<IReadOnlyCollection<MovieEditionSlot>>()))
                .Returns<RemoteMovie, IReadOnlyCollection<MovieEditionSlot>>((movie, _) =>
                    Path.GetFileNameWithoutExtension(movie.Release.Title).EndsWith(movie.Movie.Id.ToString())
                        ? EditionMatchResult.NoEvidence()
                        : EditionMatchResult.Unknown(EditionMatchSource.NormalizedTitleFallback, "different movie"));
            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(service => service.Import(It.IsAny<List<ImportDecision>>(), true, first.ImportItem, ImportMode.Copy))
                .Returns<List<ImportDecision>, bool, DownloadClientItem, ImportMode>((decisions, _, _, _) => decisions.Select(decision => new ImportResult(decision)).ToList());

            var results = Subject.ProcessPhysicalGroup(@"C:\drop\shared".AsOsAgnostic(), new[] { first, second });

            results.Single(result => result.Key == first.Key).ImportResults.Single().ImportDecision.LocalMovie.Path.Should().Be(firstPath);
            results.Single(result => result.Key == second.Key).ImportResults.Single().ImportDecision.LocalMovie.Path.Should().Be(secondPath);
            Mocker.GetMock<IImportApprovedMovie>().Verify(service => service.Import(It.Is<List<ImportDecision>>(decisions => decisions.Count == 2), true, first.ImportItem, ImportMode.Copy), Times.Once());
        }

        [Test]
        public void physical_group_should_reject_duplicate_normalized_source_for_all_targets_before_mutation()
        {
            var path = @"C:\drop\shared\Movie.mkv".AsOsAgnostic();
            Mocker.GetMock<IDiskScanService>().Setup(service => service.GetVideoFiles(It.IsAny<string>(), It.IsAny<bool>())).Returns(new[] { path });
            var first = Envelope(7, "shared", 1, MovieAcquisitionTarget.Main);
            var second = Envelope(7, "shared", 2, MovieAcquisitionTarget.Main);
            GivenGroupedDecisions();
            Mocker.GetMock<IMovieEditionMatcher>().Setup(service => service.Match(It.IsAny<RemoteMovie>(), It.IsAny<IReadOnlyCollection<MovieEditionSlot>>()))
                .Returns(EditionMatchResult.NoEvidence());
            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(service => service.Import(It.IsAny<List<ImportDecision>>(), true, first.ImportItem, ImportMode.Copy))
                .Returns<List<ImportDecision>, bool, DownloadClientItem, ImportMode>((decisions, _, _, _) => decisions.Select(decision => new ImportResult(decision, decision.Rejections.Select(rejection => rejection.Message).ToArray())).ToList());

            Subject.ProcessPhysicalGroup(@"C:\drop\shared".AsOsAgnostic(), new[] { first, second });

            Mocker.GetMock<IImportApprovedMovie>().Verify(service => service.Import(
                It.Is<List<ImportDecision>>(decisions => decisions.Count == 2 && decisions.All(decision => !decision.Approved)),
                true, first.ImportItem, ImportMode.Copy), Times.Once());
            Mocker.GetMock<IMakeImportDecision>().Verify(service => service.GetDecision(It.IsAny<LocalMovie>(), It.IsAny<DownloadClientItem>()), Times.Never());
        }

        [Test]
        public void physical_group_retry_exclusion_should_be_client_scoped()
        {
            var path = @"C:\drop\shared\Movie.mkv".AsOsAgnostic();
            Mocker.GetMock<IDiskScanService>().Setup(service => service.GetVideoFiles(It.IsAny<string>(), It.IsAny<bool>())).Returns(new[] { path });
            var main = Envelope(7, "shared", 1, MovieAcquisitionTarget.Main);
            var slot = Envelope(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42));
            GivenGroupedDecisions();
            Mocker.GetMock<IMovieEditionMatcher>().Setup(service => service.Match(It.IsAny<RemoteMovie>(), It.IsAny<IReadOnlyCollection<MovieEditionSlot>>()))
                .Returns(EditionMatchResult.NoEvidence());
            var otherClient = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.FileImported,
                DownloadId = "shared",
                DownloadClientId = 8,
                MovieId = 1,
                SourceTitle = path
            };
            MovieAcquisitionTargetSerializer.Write(otherClient.Data, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IDownloadHistoryService>().Setup(service => service.GetHistory("shared", 7))
                .Returns(new List<DownloadHistory> { otherClient });
            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(service => service.Import(It.IsAny<List<ImportDecision>>(), true, main.ImportItem, ImportMode.Copy))
                .Returns<List<ImportDecision>, bool, DownloadClientItem, ImportMode>((decisions, _, _, _) => decisions.Select(decision => new ImportResult(decision)).ToList());

            Subject.ProcessPhysicalGroup(@"C:\drop\shared".AsOsAgnostic(), new[] { main, slot });

            Mocker.GetMock<IMakeImportDecision>().Verify(service => service.GetImportDecisions(
                It.Is<List<string>>(files => files.Contains(path)), main.RemoteMovie.Movie, main.ImportItem, It.IsAny<ParsedMovieInfo>(), true), Times.Once());
        }

        [Test]
        public void physical_group_should_use_all_exact_keys_but_make_decisions_only_for_pending_envelopes()
        {
            var path = @"C:\drop\shared\Movie.IMAX.mkv".AsOsAgnostic();
            Mocker.GetMock<IDiskScanService>().Setup(service => service.GetVideoFiles(It.IsAny<string>(), It.IsAny<bool>())).Returns(new[] { path });
            var imported = Envelope(7, "shared", 1, MovieAcquisitionTarget.Main) with { ShouldImport = false };
            var pending = Envelope(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42));
            GivenGroupedDecisions();
            Mocker.GetMock<IMovieEditionMatcher>().Setup(service => service.Match(It.IsAny<RemoteMovie>(), It.IsAny<IReadOnlyCollection<MovieEditionSlot>>()))
                .Returns(EditionMatchResult.Unique(EditionMatchSource.ParsedMetadata, new[] { new MovieEditionSlot { Id = 42 } }, new MovieEditionSlot { Id = 42 }, "IMAX", EditionIdentityType.CanonicalName, "exact"));
            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(service => service.Import(It.IsAny<List<ImportDecision>>(), true, pending.ImportItem, ImportMode.Copy))
                .Returns<List<ImportDecision>, bool, DownloadClientItem, ImportMode>((decisions, _, _, _) => decisions.Select(decision => new ImportResult(decision)).ToList());

            Subject.ProcessPhysicalGroup(@"C:\drop\shared".AsOsAgnostic(), new[] { imported, pending });

            Mocker.GetMock<IMakeImportDecision>().Verify(service => service.GetImportDecisions(
                It.IsAny<List<string>>(), imported.RemoteMovie.Movie, imported.ImportItem, It.IsAny<ParsedMovieInfo>(), true), Times.Never());
            Mocker.GetMock<IMakeImportDecision>().Verify(service => service.GetImportDecisions(
                It.Is<List<string>>(files => files.SequenceEqual(new[] { path })), pending.RemoteMovie.Movie, pending.ImportItem, It.IsAny<ParsedMovieInfo>(), true), Times.Once());
            Mocker.GetMock<IDownloadHistoryService>().Verify(service => service.GetLatestDownloadHistoryItemForTarget(
                "shared", 7, 1, MovieAcquisitionTarget.Main), Times.Once());
            Mocker.GetMock<IDownloadHistoryService>().Verify(service => service.GetLatestDownloadHistoryItemForTarget(
                "shared", 7, 1, pending.Key.AcquisitionTarget), Times.Once());
        }

        [Test]
        public void physical_group_retry_should_exclude_current_attempt_source_owned_by_imported_sibling()
        {
            var now = System.DateTime.UtcNow;
            var path = @"C:\drop\shared\Movie.mkv".AsOsAgnostic();
            Mocker.GetMock<IDiskScanService>().Setup(service => service.GetVideoFiles(It.IsAny<string>(), It.IsAny<bool>())).Returns(new[] { path });
            var imported = Envelope(7, "shared", 1, MovieAcquisitionTarget.Main) with { ShouldImport = false };
            var pending = Envelope(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42));
            GivenGroupedDecisions();
            var grab = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadGrabbed, DownloadId = "shared", DownloadClientId = 7, MovieId = 1, Date = now.AddMinutes(-2)
            };
            MovieAcquisitionTargetSerializer.Write(grab.Data, MovieAcquisitionTarget.Main);
            var fileImported = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.FileImported, DownloadId = "shared", DownloadClientId = 7, MovieId = 1, SourceTitle = path, Date = now.AddMinutes(-1)
            };
            MovieAcquisitionTargetSerializer.Write(fileImported.Data, MovieAcquisitionTarget.Main);
            var completed = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadImported, DownloadId = "shared", DownloadClientId = 7, MovieId = 1, Date = now
            };
            MovieAcquisitionTargetSerializer.Write(completed.Data, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IDownloadHistoryService>().Setup(service => service.GetHistory("shared", 7))
                .Returns(new List<DownloadHistory> { completed, fileImported, grab });
            Mocker.GetMock<IDownloadHistoryService>().Setup(service => service.GetLatestDownloadHistoryItemForTarget(
                    "shared", 7, 1, MovieAcquisitionTarget.Main))
                .Returns(completed);
            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(service => service.Import(It.IsAny<List<ImportDecision>>(), true, pending.ImportItem, ImportMode.Copy))
                .Returns(new List<ImportResult>());

            Subject.ProcessPhysicalGroup(@"C:\drop\shared".AsOsAgnostic(), new[] { imported, pending });

            Mocker.GetMock<IMovieEditionMatcher>().Verify(service => service.Match(
                It.IsAny<RemoteMovie>(), It.IsAny<IReadOnlyCollection<MovieEditionSlot>>()), Times.Never());
            Mocker.GetMock<IImportApprovedMovie>().Verify(service => service.Import(
                It.Is<List<ImportDecision>>(decisions => decisions.Count == 0), true, pending.ImportItem, ImportMode.Copy), Times.Once());
        }

        [TestCase(DownloadHistoryEventType.DownloadGrabbed)]
        [TestCase(DownloadHistoryEventType.DownloadFailed)]
        [TestCase(DownloadHistoryEventType.DownloadIgnored)]
        public void physical_group_should_not_exclude_old_import_source_after_newer_exact_lifecycle(DownloadHistoryEventType newestEvent)
        {
            var now = System.DateTime.UtcNow;
            var path = @"C:\drop\shared\Movie.mkv".AsOsAgnostic();
            Mocker.GetMock<IDiskScanService>().Setup(service => service.GetVideoFiles(It.IsAny<string>(), It.IsAny<bool>())).Returns(new[] { path });
            var main = Envelope(7, "shared", 1, MovieAcquisitionTarget.Main);
            var slot = Envelope(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42));
            GivenGroupedDecisions();
            Mocker.GetMock<IMovieEditionMatcher>().Setup(service => service.Match(It.IsAny<RemoteMovie>(), It.IsAny<IReadOnlyCollection<MovieEditionSlot>>()))
                .Returns(EditionMatchResult.NoEvidence());
            var oldImport = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.FileImported,
                DownloadId = "shared",
                DownloadClientId = 7,
                MovieId = 1,
                SourceTitle = path,
                Date = now.AddMinutes(-2)
            };
            MovieAcquisitionTargetSerializer.Write(oldImport.Data, MovieAcquisitionTarget.Main);
            var newest = new DownloadHistory
            {
                EventType = newestEvent,
                DownloadId = "shared",
                DownloadClientId = 7,
                MovieId = 1,
                Date = now.AddMinutes(-1)
            };
            MovieAcquisitionTargetSerializer.Write(newest.Data, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IDownloadHistoryService>().Setup(service => service.GetHistory("shared", 7))
                .Returns(new List<DownloadHistory> { newest, oldImport });
            Mocker.GetMock<IDownloadHistoryService>().Setup(service => service.GetLatestDownloadHistoryItemForTarget(
                    "shared", 7, 1, MovieAcquisitionTarget.Main))
                .Returns(newest);
            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(service => service.Import(It.IsAny<List<ImportDecision>>(), true, main.ImportItem, ImportMode.Copy))
                .Returns<List<ImportDecision>, bool, DownloadClientItem, ImportMode>((decisions, _, _, _) => decisions.Select(decision => new ImportResult(decision)).ToList());

            Subject.ProcessPhysicalGroup(@"C:\drop\shared".AsOsAgnostic(), new[] { main, slot });

            Mocker.GetMock<IMakeImportDecision>().Verify(service => service.GetImportDecisions(
                It.Is<List<string>>(files => files.Contains(path)), main.RemoteMovie.Movie, main.ImportItem, It.IsAny<ParsedMovieInfo>(), true), Times.Once());
        }

        private void VerifyNoImport()
        {
            Mocker.GetMock<IImportApprovedMovie>().Verify(c => c.Import(It.IsAny<List<ImportDecision>>(), true, null, ImportMode.Auto),
                Times.Never());
        }

        private void VerifyImport()
        {
            Mocker.GetMock<IImportApprovedMovie>().Verify(c => c.Import(It.IsAny<List<ImportDecision>>(), true, null, ImportMode.Auto),
                Times.Once());
        }
    }
}
