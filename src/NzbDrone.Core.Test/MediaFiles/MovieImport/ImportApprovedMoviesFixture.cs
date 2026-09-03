using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.RecoverableOperations;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport
{
    [TestFixture]

    // TODO: Update all of this for movies.
    public class ImportApprovedMoviesFixture : CoreTest<ImportApprovedMovie>
    {
        private List<ImportDecision> _rejectedDecisions;
        private List<ImportDecision> _approvedDecisions;

        private DownloadClientItem _downloadClientItem;

        [SetUp]
        public void Setup()
        {
            _rejectedDecisions = new List<ImportDecision>();
            _approvedDecisions = new List<ImportDecision>();

            var outputPath = @"C:\Test\Unsorted\TV\30.Rock.S01E01".AsOsAgnostic();

            var movie = Builder<Movie>.CreateNew()
                .With(e => e.Id = 1)
                .With(e => e.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                .With(s => s.Path = @"C:\Test\TV\30 Rock".AsOsAgnostic())
                .Build();

            _rejectedDecisions.Add(new ImportDecision(new LocalMovie(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));
            _rejectedDecisions.Add(new ImportDecision(new LocalMovie(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));
            _rejectedDecisions.Add(new ImportDecision(new LocalMovie(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));

            _approvedDecisions.Add(new ImportDecision(
                                       new LocalMovie
                                       {
                                           Movie = movie,
                                           Path = Path.Combine(movie.Path, "30 Rock - S01E01 - Pilot.avi"),
                                           Quality = new QualityModel(),
                                           ReleaseGroup = "DRONE"
                                       }));

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeMovieFile(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<bool>()))
                  .Returns(new MovieFileMoveResult());

            Mocker.GetMock<IHistoryService>()
                .Setup(x => x.FindByDownloadId(It.IsAny<string>()))
                .Returns(new List<MovieHistory>());

            _downloadClientItem = Builder<DownloadClientItem>.CreateNew()
                .With(d => d.OutputPath = new OsPath(outputPath))
                .Build();

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.Add(It.IsAny<MovieFile>()))
                .Returns<MovieFile>(file => file);

            Mocker.GetMock<IConfigService>().SetupGet(s => s.UseScriptImport).Returns(true);
        }

        private void GivenNewDownload()
        {
            _approvedDecisions.ForEach(a => a.LocalMovie.Path = Path.Combine(_downloadClientItem.OutputPath.ToString(), Path.GetFileName(a.LocalMovie.Path)));
        }

        private void GivenExistingFileOnDisk()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesWithRelativePath(It.IsAny<int>(), It.IsAny<string>()))
                  .Returns(new List<MovieFile>());
        }

        private void GivenDurableDownload(string destination = null, int persistedId = 900, bool noOutgoingMain = true)
        {
            var localMovie = _approvedDecisions.First().LocalMovie;
            if (noOutgoingMain)
            {
                localMovie.Movie.MovieFileId = 0;
            }

            destination ??= Path.Combine(localMovie.Movie.Path, "renamed.mkv");
            Mocker.GetMock<IConfigService>().SetupGet(s => s.UseScriptImport).Returns(false);
            Mocker.GetMock<IMoveMovieFiles>()
                .Setup(s => s.PreflightMovieFile(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<string>()))
                .Returns(destination);
            Mocker.GetMock<IRecoverableMovieFileImportCoordinator>()
                .Setup(s => s.Import(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<TransferMode>(), It.IsAny<MovieFile>(), It.IsAny<int>(), It.IsAny<MovieFileImportTarget>(), It.IsAny<DownloadClientItem>()))
                .Returns((MovieFile desired, LocalMovie _, TransferMode __, MovieFile ___, int ____, MovieFileImportTarget target, DownloadClientItem _____) =>
                {
                    desired.Id = persistedId;
                    desired.ImportTarget = target;
                    return new RecoverableMovieFileImportResult
                    {
                        ImportedMovieFile = desired,
                        IsImported = true,
                        Operation = new RecoverableOperation { State = RecoverableOperationState.Completed }
                    };
                });
        }

        [Test]
        public void should_not_import_any_if_there_are_no_approved_decisions()
        {
            Subject.Import(_rejectedDecisions, false).Where(i => i.Result == ImportResultType.Imported).Should().BeEmpty();

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.IsAny<MovieFile>()), Times.Never());
        }

        [Test]
        public void should_import_each_approved()
        {
            GivenExistingFileOnDisk();

            Subject.Import(_approvedDecisions, false).Should().HaveCount(1);
        }

        [Test]
        public void should_only_import_approved()
        {
            GivenExistingFileOnDisk();

            var all = new List<ImportDecision>();
            all.AddRange(_rejectedDecisions);
            all.AddRange(_approvedDecisions);

            var result = Subject.Import(all, false);

            result.Should().HaveCount(all.Count);
            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(_approvedDecisions.Count);
        }

        [Test]
        public void should_only_import_each_movie_once()
        {
            GivenExistingFileOnDisk();

            var all = new List<ImportDecision>();
            all.AddRange(_approvedDecisions);
            all.Add(new ImportDecision(_approvedDecisions.First().LocalMovie));

            var result = Subject.Import(all, false);

            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(_approvedDecisions.Count);
        }

        [Test]
        public void should_move_new_downloads()
        {
            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeMovieFile(It.IsAny<MovieFile>(), _approvedDecisions.First().LocalMovie, false),
                          Times.Once());
        }

        [Test]
        public void should_publish_MovieImportedEvent_for_new_downloads()
        {
            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent(It.IsAny<MovieFileImportedEvent>()), Times.Once());
        }

        [Test]
        public void should_not_move_existing_files()
        {
            GivenExistingFileOnDisk();

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, false);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeMovieFile(It.IsAny<MovieFile>(), _approvedDecisions.First().LocalMovie, false),
                          Times.Never());
        }

        [Test]
        public void should_import_larger_files_first()
        {
            GivenExistingFileOnDisk();

            var fileDecision = _approvedDecisions.First();
            fileDecision.LocalMovie.Size = 1.Gigabytes();

            var sampleDecision = new ImportDecision(
                new LocalMovie
                {
                    Movie = fileDecision.LocalMovie.Movie,
                    Path = @"C:\Test\TV\30 Rock\30 Rock - 2017 - Pilot.avi".AsOsAgnostic(),
                    Quality = new QualityModel(),
                    Size = 80.Megabytes()
                });

            var all = new List<ImportDecision>();
            all.Add(fileDecision);
            all.Add(sampleDecision);

            var results = Subject.Import(all, false);

            results.Should().HaveCount(all.Count);
            results.Should().ContainSingle(d => d.Result == ImportResultType.Imported);
            results.Should().ContainSingle(d => d.Result == ImportResultType.Imported && d.ImportDecision.LocalMovie.Size == fileDecision.LocalMovie.Size);
        }

        [Test]
        public void should_copy_when_cannot_move_files_downloads()
        {
            GivenNewDownload();
            _downloadClientItem.Title = "30.Rock.S01E01";
            _downloadClientItem.CanMoveFiles = false;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeMovieFile(It.IsAny<MovieFile>(), _approvedDecisions.First().LocalMovie, true), Times.Once());
        }

        [Test]
        public void should_use_override_importmode()
        {
            GivenNewDownload();
            _downloadClientItem.Title = "30.Rock.S01E01";
            _downloadClientItem.CanMoveFiles = false;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem, ImportMode.Move);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeMovieFile(It.IsAny<MovieFile>(), _approvedDecisions.First().LocalMovie, false), Times.Once());
        }

        [Test]
        public void should_use_file_name_only_for_download_client_item_without_a_job_folder()
        {
            var fileName = "Series.Title.S01E01.720p.HDTV.x264-Sonarr.mkv";
            var path = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), fileName);

            _downloadClientItem.OutputPath = new OsPath(path);
            _approvedDecisions.First().LocalMovie.Path = path;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<MovieFile>(c => c.OriginalFilePath == fileName)));
        }

        [Test]
        public void should_use_folder_and_file_name_only_for_download_client_item_with_a_job_folder()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);

            _downloadClientItem.OutputPath = new OsPath(outputPath);
            _approvedDecisions.First().LocalMovie.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<MovieFile>(c => c.OriginalFilePath == $"{name}\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_include_intermediate_folders_for_download_client_item_with_a_job_folder()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);

            _downloadClientItem.OutputPath = new OsPath(outputPath);
            _approvedDecisions.First().LocalMovie.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<MovieFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_use_folder_info_original_title_to_find_relative_path()
        {
            var name = "Transformers.2007.720p.BluRay.x264-Radarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\movies\".AsOsAgnostic(), name);
            var localMovie = _approvedDecisions.First().LocalMovie;

            localMovie.FolderMovieInfo = new ParsedMovieInfo { OriginalTitle = name };
            localMovie.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<MovieFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_get_relative_path_when_there_is_no_grandparent_windows()
        {
            WindowsOnly();

            var name = "Transformers.2007.720p.BluRay.x264-Radarr";
            var outputPath = @"C:\".AsOsAgnostic();
            var localMovie = _approvedDecisions.First().LocalMovie;
            localMovie.FolderMovieInfo = new ParsedMovieInfo { ReleaseTitle = name };
            localMovie.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<MovieFile>(c => c.OriginalFilePath == $"{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_get_relative_path_when_there_is_no_grandparent_for_UNC_path()
        {
            WindowsOnly();

            var name = "Transformers.2007.720p.BluRay.x264-Radarr";
            var outputPath = @"\\server\share";
            var localMovie = _approvedDecisions.First().LocalMovie;

            localMovie.FolderMovieInfo = new ParsedMovieInfo { ReleaseTitle = name };
            localMovie.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<MovieFile>(c => c.OriginalFilePath == $"{name}.mkv")));
        }

        [Test]
        public void should_use_folder_info_original_title_to_find_relative_path_when_file_is_not_in_download_client_item_output_directory()
        {
            var name = "Transformers.2007.720p.BluRay.x264-Radarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\movie\".AsOsAgnostic(), name);
            var localMovie = _approvedDecisions.First().LocalMovie;

            _downloadClientItem.OutputPath = new OsPath(Path.Combine(@"C:\Test\Unsorted\movie-Other\".AsOsAgnostic(), name));
            localMovie.FolderMovieInfo = new ParsedMovieInfo { ReleaseTitle = name };
            localMovie.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<MovieFile>(c => c.OriginalFilePath == $"subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_use_folder_info_original_title_to_find_relative_path_when_download_client_item_has_an_empty_output_path()
        {
            var name = "Transformers.2007.720p.BluRay.x264-Radarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\movies\".AsOsAgnostic(), name);
            var localMovie = _approvedDecisions.First().LocalMovie;

            localMovie.FolderMovieInfo = new ParsedMovieInfo { ReleaseTitle = name };
            localMovie.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<MovieFile>(c => c.OriginalFilePath == $"subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_include_scene_name_with_new_downloads()
        {
            var firstDecision = _approvedDecisions.First();
            firstDecision.LocalMovie.SceneName = "Movie.Title.2022.dvdrip-DRONE";

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeMovieFile(It.Is<MovieFile>(e => e.SceneName == firstDecision.LocalMovie.SceneName), _approvedDecisions.First().LocalMovie, false),
                      Times.Once());
        }

        [Test]
        public void should_import_both_when_same_movie_has_different_explicit_slots_even_with_identical_parsed_editions()
        {
            var movie = _approvedDecisions.First().LocalMovie.Movie;

            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(10)).Returns(new MovieEditionSlot { Id = 10, MovieId = movie.Id });
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(11)).Returns(new MovieEditionSlot { Id = 11, MovieId = movie.Id });

            var directorsDecision = new ImportDecision(
                new LocalMovie
                {
                    Movie = movie,
                    Path = Path.Combine(movie.Path, "30 Rock - Directors Cut.mkv"),
                    Quality = new QualityModel(),
                    Edition = "Director's Cut",
                    MovieEditionSlotId = 10,
                    ReleaseGroup = "DRONE"
                });

            var imaxDecision = new ImportDecision(
                new LocalMovie
                {
                    Movie = movie,
                    Path = Path.Combine(movie.Path, "30 Rock - IMAX.mkv"),
                    Quality = new QualityModel(),
                    Edition = "Director's Cut",
                    MovieEditionSlotId = 11,
                    ReleaseGroup = "DRONE"
                });

            var result = Subject.Import(new List<ImportDecision> { directorsDecision, imaxDecision }, true);

            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(2);
        }

        [Test]
        public void should_only_import_once_when_same_movie_and_same_explicit_slot_appears_twice()
        {
            var movie = _approvedDecisions.First().LocalMovie.Movie;

            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(10)).Returns(new MovieEditionSlot { Id = 10, MovieId = movie.Id });

            var first = new ImportDecision(
                new LocalMovie
                {
                    Movie = movie,
                    Path = Path.Combine(movie.Path, "30 Rock - Directors Cut 1080p.mkv"),
                    Quality = new QualityModel(Quality.Bluray1080p),
                    Edition = "Director's Cut",
                    MovieEditionSlotId = 10,
                    ReleaseGroup = "DRONE",
                    Size = 8.Gigabytes()
                });

            var second = new ImportDecision(
                new LocalMovie
                {
                    Movie = movie,
                    Path = Path.Combine(movie.Path, "30 Rock - Directors Cut 720p.mkv"),
                    Quality = new QualityModel(Quality.Bluray720p),
                    Edition = "Director's Cut",
                    MovieEditionSlotId = 10,
                    ReleaseGroup = "DRONE",
                    Size = 4.Gigabytes()
                });

            var result = Subject.Import(new List<ImportDecision> { first, second }, true);

            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(1);
        }

        [Test]
        public void should_reject_missing_or_cross_movie_explicit_slot_before_media_mutation()
        {
            var movie = _approvedDecisions.First().LocalMovie.Movie;
            var missing = _approvedDecisions.First();
            missing.LocalMovie.MovieEditionSlotId = 10;
            var crossMovie = new ImportDecision(new LocalMovie
            {
                Movie = movie,
                Path = Path.Combine(movie.Path, "cross.mkv"),
                Quality = new QualityModel(),
                MovieEditionSlotId = 11
            });

            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(10)).Throws(new KeyNotFoundException());
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(11)).Returns(new MovieEditionSlot { Id = 11, MovieId = movie.Id + 1 });

            var result = Subject.Import(new List<ImportDecision> { missing, crossMovie }, true);

            result.Should().OnlyContain(r => r.Result == ImportResultType.Skipped);
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.UpgradeMovieFile(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<bool>()), Times.Never);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Add(It.IsAny<MovieFile>()), Times.Never);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never);
        }

        [Test]
        public void should_use_slot_quality_profile_override_when_selecting_duplicate_slot_candidate()
        {
            var movie = _approvedDecisions.First().LocalMovie.Movie;
            var slotProfile = new QualityProfile
            {
                Id = 20,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Allowed = true, Quality = Quality.Bluray1080p },
                    new QualityProfileQualityItem { Allowed = true, Quality = Quality.Bluray720p }
                }
            };
            var slot = new MovieEditionSlot { Id = 10, MovieId = movie.Id, QualityProfileId = slotProfile.Id };
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(slot.Id)).Returns(slot);
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.Get(slotProfile.Id)).Returns(slotProfile);

            var lowerInSlotProfile = new ImportDecision(new LocalMovie
            {
                Movie = movie,
                Path = Path.Combine(movie.Path, "1080p.mkv"),
                Quality = new QualityModel(Quality.Bluray1080p),
                MovieEditionSlotId = slot.Id,
                Size = 9.Gigabytes()
            });
            var higherInSlotProfile = new ImportDecision(new LocalMovie
            {
                Movie = movie,
                Path = Path.Combine(movie.Path, "720p.mkv"),
                Quality = new QualityModel(Quality.Bluray720p),
                MovieEditionSlotId = slot.Id,
                Size = 1.Gigabytes()
            });

            var result = Subject.Import(new List<ImportDecision> { lowerInSlotProfile, higherInSlotProfile }, true);

            result.Should().ContainSingle(r => r.Result == ImportResultType.Imported && r.ImportDecision == higherInSlotProfile);
        }

        [Test]
        public void should_use_slot_profile_and_minimum_score_when_selecting_duplicate_slot_candidate()
        {
            var movie = _approvedDecisions.First().LocalMovie.Movie;
            var slotProfile = new QualityProfile
            {
                Id = 20,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Allowed = true, Quality = Quality.Bluray1080p },
                    new QualityProfileQualityItem { Allowed = true, Quality = Quality.Bluray720p }
                }
            };
            var preferredFormat = new CustomFormat { Id = 100, Name = "Preferred" };
            slotProfile.FormatItems = new List<ProfileFormatItem>
            {
                new ProfileFormatItem { Format = preferredFormat, Score = 100 }
            };
            var slot = new MovieEditionSlot { Id = 10, MovieId = movie.Id, QualityProfileId = slotProfile.Id, MinimumCustomFormatScore = 100 };
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(slot.Id)).Returns(slot);
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.Get(slotProfile.Id)).Returns(slotProfile);

            var belowMinimum = new ImportDecision(new LocalMovie
            {
                Movie = movie,
                Path = Path.Combine(movie.Path, "below.mkv"),
                Quality = new QualityModel(Quality.Bluray720p),
                CustomFormats = new List<CustomFormat>(),
                CustomFormatScore = 100,
                MovieEditionSlotId = slot.Id,
                Size = 9.Gigabytes()
            });
            var eligible = new ImportDecision(new LocalMovie
            {
                Movie = movie,
                Path = Path.Combine(movie.Path, "eligible.mkv"),
                Quality = new QualityModel(Quality.Bluray1080p),
                CustomFormats = new List<CustomFormat> { preferredFormat },
                CustomFormatScore = 0,
                MovieEditionSlotId = slot.Id,
                Size = 1.Gigabytes()
            });

            var result = Subject.Import(new List<ImportDecision> { belowMinimum, eligible }, true);

            result.Should().ContainSingle(r => r.Result == ImportResultType.Imported && r.ImportDecision == eligible);
        }

        [Test]
        public void exact_grouped_import_should_use_release_indexer_flags_without_movie_history()
        {
            var localMovie = _approvedDecisions.First().LocalMovie;
            localMovie.HasExactTargetContext = true;
            localMovie.Release = new GrabbedReleaseInfo(new NzbDrone.Core.Download.History.DownloadHistory
            {
                MovieId = localMovie.Movie.Id,
                Release = new ReleaseInfo { IndexerFlags = IndexerFlags.G_Freeleech }
            });
            _downloadClientItem.DownloadId = "shared";
            MovieFile captured = null;
            Mocker.GetMock<IMediaFileService>()
                .Setup(service => service.Add(It.IsAny<MovieFile>()))
                .Callback<MovieFile>(file => captured = file)
                .Returns<MovieFile>(file => file);

            Subject.Import(_approvedDecisions, true, _downloadClientItem, ImportMode.Copy);

            captured.IndexerFlags.Should().Be(IndexerFlags.G_Freeleech);
            Mocker.GetMock<IHistoryService>().Verify(service => service.FindByDownloadId(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void exact_grouped_null_minimum_should_not_fall_back_to_later_slot_minimum()
        {
            var movie = _approvedDecisions.First().LocalMovie.Movie;
            var slotProfile = new QualityProfile
            {
                Id = 20,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Allowed = true, Quality = Quality.Bluray1080p },
                    new QualityProfileQualityItem { Allowed = true, Quality = Quality.Bluray720p }
                }
            };
            var preferredFormat = new CustomFormat { Id = 100, Name = "Preferred" };
            slotProfile.FormatItems = new List<ProfileFormatItem>
            {
                new ProfileFormatItem { Format = preferredFormat, Score = 100 }
            };
            var slot = new MovieEditionSlot { Id = 10, MovieId = movie.Id, QualityProfileId = slotProfile.Id, MinimumCustomFormatScore = 100 };
            Mocker.GetMock<IMovieEditionSlotService>().Setup(service => service.GetById(slot.Id)).Returns(slot);

            var higherQualityWithoutMinimum = new ImportDecision(new LocalMovie
            {
                Movie = movie,
                Path = Path.Combine(movie.Path, "higher.mkv"),
                Quality = new QualityModel(Quality.Bluray720p),
                CustomFormats = new List<CustomFormat>(),
                MovieEditionSlotId = slot.Id,
                TargetQualityProfile = slotProfile,
                TargetMinimumCustomFormatScore = null,
                HasExactTargetContext = true,
                Size = 9.Gigabytes()
            });
            var lowerQualityMeetingMutableMinimum = new ImportDecision(new LocalMovie
            {
                Movie = movie,
                Path = Path.Combine(movie.Path, "lower.mkv"),
                Quality = new QualityModel(Quality.Bluray1080p),
                CustomFormats = new List<CustomFormat> { preferredFormat },
                MovieEditionSlotId = slot.Id,
                TargetQualityProfile = slotProfile,
                TargetMinimumCustomFormatScore = null,
                HasExactTargetContext = true,
                Size = 1.Gigabytes()
            });

            var result = Subject.Import(new List<ImportDecision> { higherQualityWithoutMinimum, lowerQualityMeetingMutableMinimum }, true);

            result.Should().ContainSingle(item => item.Result == ImportResultType.Imported && item.ImportDecision == higherQualityWithoutMinimum);
        }

        [Test]
        public void changing_a_slot_target_to_unassigned_should_clear_stale_slot_identity_before_upgrade()
        {
            var localMovie = _approvedDecisions.First().LocalMovie;
            localMovie.MovieEditionSlotId = 10;
            localMovie.ImportTarget = MovieFileImportTarget.Unassigned;

            Subject.Import(_approvedDecisions, true);

            localMovie.MovieEditionSlotId.Should().BeNull();
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.UpgradeMovieFile(
                It.Is<MovieFile>(file => file.MovieEditionSlotId == null && file.ImportTarget == MovieFileImportTarget.Unassigned),
                localMovie,
                false), Times.Once);
        }

        [TestCase(MovieFileImportTarget.Main)]
        [TestCase(MovieFileImportTarget.EditionSlot)]
        [TestCase(MovieFileImportTarget.Unassigned)]
        public void durable_new_download_should_coordinate_exact_target_without_legacy_mutations(MovieFileImportTarget target)
        {
            var localMovie = _approvedDecisions.First().LocalMovie;
            localMovie.ImportTarget = target;
            localMovie.Movie.MovieFileId = 101;
            var main = new MovieFile { Id = 101, MovieId = localMovie.Movie.Id, RelativePath = "main-old.mkv" };
            var slot = new MovieFile { Id = 202, MovieId = localMovie.Movie.Id, MovieEditionSlotId = 10, RelativePath = "slot-old.mkv" };
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetMovie(101)).Returns(main);
            if (target == MovieFileImportTarget.EditionSlot)
            {
                localMovie.MovieEditionSlotId = 10;
                Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(10)).Returns(new MovieEditionSlot { Id = 10, MovieId = localMovie.Movie.Id });
                Mocker.GetMock<IMediaFileService>().Setup(s => s.FindByEditionSlotId(10)).Returns(slot);
            }

            GivenDurableDownload(noOutgoingMain: false);
            var result = Subject.Import(_approvedDecisions, true, _downloadClientItem, ImportMode.Move);

            result.Should().ContainSingle(r => r.Result == ImportResultType.Imported);
            var expectedOutgoing = target == MovieFileImportTarget.Main ? main : target == MovieFileImportTarget.EditionSlot ? slot : null;
            Mocker.GetMock<IRecoverableMovieFileImportCoordinator>().Verify(s => s.Import(
                It.Is<MovieFile>(f => f.Path.EndsWith("renamed.mkv") && f.RelativePath == "renamed.mkv" && f.MovieEditionSlotId == (target == MovieFileImportTarget.EditionSlot ? 10 : null)),
                localMovie,
                TransferMode.Move,
                expectedOutgoing,
                101,
                target,
                _downloadClientItem), Times.Once);
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.UpgradeMovieFile(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<bool>()), Times.Never);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Add(It.IsAny<MovieFile>()), Times.Never);
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<MovieFileImportedEvent>()), Times.Never);
            if (target == MovieFileImportTarget.Main)
            {
                localMovie.Movie.MovieFile.Id.Should().Be(900);
            }
        }

        [TestCase(ImportMode.Move, true, TransferMode.Move)]
        [TestCase(ImportMode.Copy, false, TransferMode.Copy)]
        [TestCase(ImportMode.Copy, true, TransferMode.HardLinkOrCopy)]
        [TestCase(ImportMode.Auto, true, TransferMode.HardLinkOrCopy)]
        public void durable_new_download_should_map_transfer_mode(ImportMode importMode, bool hardlinks, TransferMode expected)
        {
            GivenDurableDownload();
            Mocker.GetMock<IConfigService>().SetupGet(s => s.CopyUsingHardlinks).Returns(hardlinks);
            _downloadClientItem.CanMoveFiles = importMode != ImportMode.Auto;

            Subject.Import(_approvedDecisions, true, _downloadClientItem, importMode);

            Mocker.GetMock<IRecoverableMovieFileImportCoordinator>().Verify(s => s.Import(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), expected, It.IsAny<MovieFile>(), It.IsAny<int>(), It.IsAny<MovieFileImportTarget>(), It.IsAny<DownloadClientItem>()), Times.Once);
        }

        [Test]
        public void durable_failure_should_not_add_publish_or_run_extras()
        {
            GivenDurableDownload();
            Mocker.GetMock<IRecoverableMovieFileImportCoordinator>()
                .Setup(s => s.Import(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<TransferMode>(), It.IsAny<MovieFile>(), It.IsAny<int>(), It.IsAny<MovieFileImportTarget>(), It.IsAny<DownloadClientItem>()))
                .Throws(new IOException("precommit failure"));

            Subject.Import(_approvedDecisions, true, _downloadClientItem).Should().ContainSingle(r => r.Result == ImportResultType.Skipped);

            Mocker.GetMock<IMediaFileService>().Verify(s => s.Add(It.IsAny<MovieFile>()), Times.Never);
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<MovieFileImportedEvent>()), Times.Never);
            Mocker.GetMock<IExtraService>().Verify(s => s.ImportMovie(It.IsAny<LocalMovie>(), It.IsAny<MovieFile>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public void durable_committed_pending_result_should_be_reported_imported_without_duplicate_completion()
        {
            GivenDurableDownload();
            Mocker.GetMock<IRecoverableMovieFileImportCoordinator>()
                .Setup(s => s.Import(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<TransferMode>(), It.IsAny<MovieFile>(), It.IsAny<int>(), It.IsAny<MovieFileImportTarget>(), It.IsAny<DownloadClientItem>()))
                .Returns(new RecoverableMovieFileImportResult
                {
                    ImportedMovieFile = new MovieFile { Id = 901, MovieId = 1, ImportTarget = MovieFileImportTarget.Main, RelativePath = "renamed.mkv" },
                    IsImported = true,
                    FinalizationPending = true,
                    Operation = new RecoverableOperation { State = RecoverableOperationState.Finalizing }
                });

            Subject.Import(_approvedDecisions, true, _downloadClientItem).Should().ContainSingle(r => r.Result == ImportResultType.Imported && r.FinalizationPending);
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<MovieFileImportedEvent>()), Times.Never);
            Mocker.GetMock<IExtraService>().Verify(s => s.ImportMovie(It.IsAny<LocalMovie>(), It.IsAny<MovieFile>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public void script_import_configuration_should_retain_complete_legacy_path()
        {
            Subject.Import(_approvedDecisions, true, _downloadClientItem);

            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.UpgradeMovieFile(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<bool>()), Times.Once);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Add(It.IsAny<MovieFile>()), Times.Once);
            Mocker.GetMock<IRecoverableMovieFileImportCoordinator>().Verify(s => s.Import(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<TransferMode>(), It.IsAny<MovieFile>(), It.IsAny<int>(), It.IsAny<MovieFileImportTarget>(), It.IsAny<DownloadClientItem>()), Times.Never);
        }

        [TestCase(MovieFileImportTarget.EditionSlot)]
        [TestCase(MovieFileImportTarget.Unassigned)]
        public void should_not_assign_movie_file_pointer_for_non_main_targets(MovieFileImportTarget target)
        {
            var localMovie = _approvedDecisions.First().LocalMovie;
            localMovie.ImportTarget = target;
            if (target == MovieFileImportTarget.EditionSlot)
            {
                localMovie.MovieEditionSlotId = 10;
                Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(10)).Returns(new MovieEditionSlot { Id = 10, MovieId = localMovie.Movie.Id });
            }

            Subject.Import(_approvedDecisions, true);

            localMovie.Movie.MovieFile.Should().BeNull();
        }

    }
}
