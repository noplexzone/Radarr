using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]
    public class DownloadDecisionMakerFixture : CoreTest<DownloadDecisionMaker>
    {
        private List<ReleaseInfo> _reports;
        private RemoteMovie _remoteEpisode;

        private Mock<IDownloadDecisionEngineSpecification> _pass1;
        private Mock<IDownloadDecisionEngineSpecification> _pass2;
        private Mock<IDownloadDecisionEngineSpecification> _pass3;

        private Mock<IDownloadDecisionEngineSpecification> _fail1;
        private Mock<IDownloadDecisionEngineSpecification> _fail2;
        private Mock<IDownloadDecisionEngineSpecification> _fail3;

        [SetUp]
        public void Setup()
        {
            _pass1 = new Mock<IDownloadDecisionEngineSpecification>();
            _pass2 = new Mock<IDownloadDecisionEngineSpecification>();
            _pass3 = new Mock<IDownloadDecisionEngineSpecification>();

            _fail1 = new Mock<IDownloadDecisionEngineSpecification>();
            _fail2 = new Mock<IDownloadDecisionEngineSpecification>();
            _fail3 = new Mock<IDownloadDecisionEngineSpecification>();

            _pass1.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<SearchCriteriaBase>())).Returns(DownloadSpecDecision.Accept);
            _pass2.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<SearchCriteriaBase>())).Returns(DownloadSpecDecision.Accept);
            _pass3.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<SearchCriteriaBase>())).Returns(DownloadSpecDecision.Accept);

            _fail1.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<SearchCriteriaBase>())).Returns(DownloadSpecDecision.Reject(DownloadRejectionReason.Unknown, "fail1"));
            _fail2.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<SearchCriteriaBase>())).Returns(DownloadSpecDecision.Reject(DownloadRejectionReason.Unknown, "fail2"));
            _fail3.Setup(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), It.IsAny<SearchCriteriaBase>())).Returns(DownloadSpecDecision.Reject(DownloadRejectionReason.Unknown, "fail3"));

            _reports = new List<ReleaseInfo> { new ReleaseInfo { Title = "Movie.2018.1080p.AMZN.WEB-DL.DD5.1.H.264-NTG" } };
            _remoteEpisode = new RemoteMovie
            {
                Movie = new Movie { Id = 7 },
                ParsedMovieInfo = new ParsedMovieInfo()
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>())).Returns(_remoteEpisode);

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovie(It.IsAny<int>()))
                .Returns(new List<MovieFile>());

            Mocker.SetConstant<IMovieEditionMatcher>(new MovieEditionMatcher());
        }

        private void GivenSpecifications(params Mock<IDownloadDecisionEngineSpecification>[] mocks)
        {
            Mocker.SetConstant<IEnumerable<IDownloadDecisionEngineSpecification>>(mocks.Select(c => c.Object));
        }

        [Test]
        public void should_call_all_specifications()
        {
            GivenSpecifications(_pass1, _pass2, _pass3, _fail1, _fail2, _fail3);

            Subject.GetRssDecision(_reports).ToList();

            _fail1.Verify(c => c.IsSatisfiedBy(_remoteEpisode, null), Times.Once());
            _fail2.Verify(c => c.IsSatisfiedBy(_remoteEpisode, null), Times.Once());
            _fail3.Verify(c => c.IsSatisfiedBy(_remoteEpisode, null), Times.Once());
            _pass1.Verify(c => c.IsSatisfiedBy(_remoteEpisode, null), Times.Once());
            _pass2.Verify(c => c.IsSatisfiedBy(_remoteEpisode, null), Times.Once());
            _pass3.Verify(c => c.IsSatisfiedBy(_remoteEpisode, null), Times.Once());
        }

        [Test]
        public void should_return_rejected_if_single_specs_fail()
        {
            GivenSpecifications(_fail1);

            var result = Subject.GetRssDecision(_reports);

            result.Single().Approved.Should().BeFalse();
        }

        [Test]
        public void should_return_rejected_if_one_of_specs_fail()
        {
            GivenSpecifications(_pass1, _fail1, _pass2, _pass3);

            var result = Subject.GetRssDecision(_reports);

            result.Single().Approved.Should().BeFalse();
        }

        [Test]
        public void should_return_pass_if_all_specs_pass()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);

            var result = Subject.GetRssDecision(_reports);

            result.Single().Approved.Should().BeTrue();
        }

        [Test]
        public void should_have_same_number_of_rejections_as_specs_that_failed()
        {
            GivenSpecifications(_pass1, _pass2, _pass3, _fail1, _fail2, _fail3);

            var result = Subject.GetRssDecision(_reports);
            result.Single().Rejections.Should().HaveCount(3);
        }

        [Test]
        public void should_not_attempt_to_map_episode_if_not_parsable()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);
            _reports[0].Title = "Not parsable";

            Subject.GetRssDecision(_reports).ToList();

            Mocker.GetMock<IParsingService>().Verify(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()), Times.Never());

            _pass1.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), null), Times.Never());
            _pass2.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), null), Times.Never());
            _pass3.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), null), Times.Never());
        }

        [Test]
        public void should_return_rejected_result_for_unparsable_search()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);
            _reports[0].Title = "1937 - Snow White and the Seven Dwarves";

            Subject.GetSearchDecision(_reports, new MovieSearchCriteria()).ToList();

            Mocker.GetMock<IParsingService>().Verify(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()), Times.Never());

            _pass1.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), null), Times.Never());
            _pass2.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), null), Times.Never());
            _pass3.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), null), Times.Never());
        }

        [Test]
        public void should_copy_movie_edition_slot_id_from_search_criteria_to_remote_movie()
        {
            GivenSpecifications();

            var result = Subject.GetSearchDecision(_reports, new MovieSearchCriteria { MovieEditionSlotId = 42 });

            result.Single().RemoteMovie.MovieEditionSlotId.Should().Be(42);
        }

        [Test]
        public void should_stamp_explicit_edition_slot_context_before_specifications_run()
        {
            var slotFile = new MovieFile { Id = 99, MovieId = _remoteEpisode.Movie.Id, MovieEditionSlotId = 42 };
            var slotProfile = new QualityProfile { Id = 12, Name = "Slot Profile" };
            var criteria = new MovieSearchCriteria { MovieEditionSlotId = 42 };
            var specification = new Mock<IDownloadDecisionEngineSpecification>();
            _remoteEpisode.ParsedMovieInfo.Edition = "IMAX";

            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieEditionSlot>
                {
                    new MovieEditionSlot
                    {
                        Id = 42,
                        MovieId = _remoteEpisode.Movie.Id,
                        EditionName = "IMAX",
                        QualityProfileId = slotProfile.Id,
                        MinimumCustomFormatScore = 25
                    }
                });

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieFile> { slotFile });

            Mocker.GetMock<IQualityProfileService>()
                .Setup(s => s.Get(slotProfile.Id))
                .Returns(slotProfile);

            specification.SetupGet(s => s.Priority).Returns(0);
            specification
                .Setup(s => s.IsSatisfiedBy(It.IsAny<RemoteMovie>(), criteria))
                .Callback<RemoteMovie, SearchCriteriaBase>((remoteMovie, _) =>
                {
                    remoteMovie.MovieEditionSlotId.Should().Be(42);
                    remoteMovie.SlotContextStamped.Should().BeTrue();
                    remoteMovie.SlotMovieFile.Should().BeSameAs(slotFile);
                    remoteMovie.SlotQualityProfile.Should().BeSameAs(slotProfile);
                    remoteMovie.SlotMinimumCustomFormatScore.Should().Be(25);
                })
                .Returns(DownloadSpecDecision.Accept);

            GivenSpecifications(specification);

            Subject.GetSearchDecision(_reports, criteria).Single().Approved.Should().BeTrue();

            specification.Verify(s => s.IsSatisfiedBy(_remoteEpisode, criteria), Times.Once);
        }

        [Test]
        public void should_calculate_search_custom_format_score_using_criteria_override_profile()
        {
            GivenSpecifications(_pass1);

            var customFormat = new CustomFormat { Id = 7, Name = "Override Format" };
            var overrideProfile = new QualityProfile
            {
                FormatItems = new List<ProfileFormatItem>
                {
                    new ProfileFormatItem { Format = customFormat, Score = 75 }
                }
            };
            var criteria = new MovieSearchCriteria { OverrideQualityProfile = overrideProfile };

            Mocker.GetMock<ICustomFormatCalculationService>()
                .Setup(s => s.ParseCustomFormat(_remoteEpisode, It.IsAny<long>()))
                .Returns(new List<CustomFormat> { customFormat });

            Subject.GetSearchDecision(_reports, criteria).Single().RemoteMovie.CustomFormatScore.Should().Be(75);
        }

        [Test]
        public void should_stamp_matching_monitored_edition_slot_on_rss_decision()
        {
            GivenSpecifications(_pass1);

            var slotFile = new MovieFile { Id = 99, MovieId = _remoteEpisode.Movie.Id, MovieEditionSlotId = 42 };

            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieEditionSlot>
                {
                    new MovieEditionSlot
                    {
                        Id = 42,
                        MovieId = _remoteEpisode.Movie.Id,
                        EditionName = "IMAX",
                        SearchTerm = "IMAX",
                        Monitored = true,
                    }
                });

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieFile> { slotFile });

            _reports[0].Title = "Movie.Title.Imax.2018.1080p.AMZN.WEB-DL.DD5.1.H.264-NTG";

            var result = Subject.GetRssDecision(_reports);

            result.Single().RemoteMovie.MovieEditionSlotId.Should().Be(42);
            result.Single().RemoteMovie.SlotContextStamped.Should().BeTrue();
            result.Single().RemoteMovie.SlotMovieFile.Should().Be(slotFile);
        }

        [Test]
        public void should_prefer_exact_parsed_edition_slot_when_rss_slot_names_overlap()
        {
            GivenSpecifications(_pass1);
            _remoteEpisode.ParsedMovieInfo.Edition = "Extended Director's Cut";

            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieEditionSlot>
                {
                    new MovieEditionSlot
                    {
                        Id = 41,
                        MovieId = _remoteEpisode.Movie.Id,
                        EditionName = "Director's Cut",
                        Monitored = true
                    },
                    new MovieEditionSlot
                    {
                        Id = 42,
                        MovieId = _remoteEpisode.Movie.Id,
                        EditionName = "Extended Director's Cut",
                        Monitored = true
                    }
                });

            _reports[0].Title = "Movie.Title.Extended.Directors.Cut.2018.1080p.BluRay";

            var result = Subject.GetRssDecision(_reports).Single();

            result.RemoteMovie.MovieEditionSlotId.Should().Be(42);
            result.RemoteMovie.SlotContextStamped.Should().BeTrue();
        }

        [Test]
        public void should_preserve_rss_slot_context_when_slot_movie_file_is_stale()
        {
            GivenSpecifications(_pass1);

            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieEditionSlot>
                {
                    new MovieEditionSlot
                    {
                        Id = 42,
                        MovieId = _remoteEpisode.Movie.Id,
                        EditionName = "IMAX",
                        Monitored = true,
                    }
                });

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieFile>());

            _reports[0].Title = "Movie.Title.Imax.2018.1080p.AMZN.WEB-DL.DD5.1.H.264-NTG";

            var result = Subject.GetRssDecision(_reports).Single();

            result.Rejections.Should().NotContain(r => r.Reason == DownloadRejectionReason.Error);
            result.RemoteMovie.MovieEditionSlotId.Should().Be(42);
            result.RemoteMovie.SlotContextStamped.Should().BeTrue();
            result.RemoteMovie.SlotMovieFile.Should().BeNull();
        }

        [Test]
        public void should_calculate_rss_custom_format_score_using_slot_quality_profile()
        {
            GivenSpecifications(_pass1);

            var customFormat = new CustomFormat { Id = 7, Name = "Slot Format" };
            var movieProfile = new QualityProfile
            {
                FormatItems = new List<ProfileFormatItem>
                {
                    new ProfileFormatItem { Format = customFormat, Score = 5 }
                }
            };
            var slotProfile = new QualityProfile
            {
                Id = 12,
                FormatItems = new List<ProfileFormatItem>
                {
                    new ProfileFormatItem { Format = customFormat, Score = 50 }
                }
            };
            _remoteEpisode.Movie.QualityProfile = movieProfile;

            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieEditionSlot>
                {
                    new MovieEditionSlot
                    {
                        Id = 42,
                        MovieId = _remoteEpisode.Movie.Id,
                        EditionName = "IMAX",
                        Monitored = true,
                        QualityProfileId = slotProfile.Id
                    }
                });

            Mocker.GetMock<IQualityProfileService>()
                .Setup(s => s.Get(slotProfile.Id))
                .Returns(slotProfile);

            Mocker.GetMock<ICustomFormatCalculationService>()
                .Setup(s => s.ParseCustomFormat(_remoteEpisode, It.IsAny<long>()))
                .Returns(new List<CustomFormat> { customFormat });

            _reports[0].Title = "Movie.Title.Imax.2018.1080p.AMZN.WEB-DL.DD5.1.H.264-NTG";

            Subject.GetRssDecision(_reports).Single().RemoteMovie.CustomFormatScore.Should().Be(50);
        }

        [Test]
        public void should_reject_unconfigured_parsed_edition_from_rss_main_target()
        {
            GivenSpecifications(_pass1);
            _remoteEpisode.ParsedMovieInfo.Edition = "IMAX Enhanced";
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieEditionSlot>());

            var result = Subject.GetRssDecision(_reports).Single();

            result.Approved.Should().BeFalse();
            result.Rejections.Should().Contain(r => r.Reason == DownloadRejectionReason.WrongEdition);
            result.RemoteMovie.MovieEditionSlotId.Should().BeNull();
        }

        [Test]
        public void should_reject_configured_unmonitored_edition_from_rss_main_target()
        {
            GivenSpecifications(_pass1);
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieEditionSlot>
                {
                    new MovieEditionSlot { Id = 42, MovieId = _remoteEpisode.Movie.Id, EditionName = "IMAX", Monitored = false }
                });
            _reports[0].Title = "Movie.2018.IMAX.1080p.BluRay";

            var result = Subject.GetRssDecision(_reports).Single();

            result.Approved.Should().BeFalse();
            result.Rejections.Should().Contain(r => r.Reason == DownloadRejectionReason.WrongEdition);
        }

        [TestCase("Movie.2018.CLIMAX.1080p.BluRay")]
        [TestCase("Movie.2018.ABCD.1080p.BluRay")]
        public void should_not_match_rss_slot_terms_inside_larger_tokens(string title)
        {
            GivenSpecifications(_pass1);
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieEditionSlot>
                {
                    new MovieEditionSlot
                    {
                        Id = 42,
                        MovieId = _remoteEpisode.Movie.Id,
                        EditionName = title.Contains("CLIMAX") ? "IMAX" : "DC",
                        Monitored = true,
                        Aliases = null
                    }
                });
            _reports[0].Title = title;

            var result = Subject.GetRssDecision(_reports).Single();

            result.Approved.Should().BeTrue();
            result.RemoteMovie.MovieEditionSlotId.Should().BeNull();
        }

        [Test]
        public void should_match_rss_slot_alias_with_exact_parsed_identity()
        {
            GivenSpecifications(_pass1);
            _remoteEpisode.ParsedMovieInfo.Edition = "DC";
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieEditionSlot>
                {
                    new MovieEditionSlot
                    {
                        Id = 42,
                        MovieId = _remoteEpisode.Movie.Id,
                        EditionName = "Director's Cut",
                        Aliases = new List<string> { "DC" },
                        Monitored = true
                    }
                });

            Subject.GetRssDecision(_reports).Single().RemoteMovie.MovieEditionSlotId.Should().Be(42);
        }

        [Test]
        public void should_not_treat_movie_title_as_configured_rss_edition()
        {
            GivenSpecifications(_pass1);
            _remoteEpisode.ParsedMovieInfo.MovieTitles = new List<string> { "IMAX" };
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieEditionSlot>
                {
                    new MovieEditionSlot { Id = 42, MovieId = _remoteEpisode.Movie.Id, EditionName = "IMAX", Monitored = true }
                });
            _reports[0].Title = "IMAX.2018.1080p.BluRay";

            var result = Subject.GetRssDecision(_reports).Single();

            result.Approved.Should().BeTrue();
            result.RemoteMovie.MovieEditionSlotId.Should().BeNull();
        }

        [Test]
        public void should_not_match_a_leading_release_group_as_an_rss_edition()
        {
            GivenSpecifications(_pass1);
            _remoteEpisode.ParsedMovieInfo.MovieTitles = new List<string> { "Movie" };
            _remoteEpisode.ParsedMovieInfo.ReleaseGroup = "IMAX";
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(new List<MovieEditionSlot>
                {
                    new MovieEditionSlot { Id = 42, MovieId = _remoteEpisode.Movie.Id, EditionName = "IMAX", Monitored = true }
                });
            _reports[0].Title = "[IMAX].Movie.2024.1080p.BluRay";

            var result = Subject.GetRssDecision(_reports).Single();

            result.Approved.Should().BeTrue();
            result.RemoteMovie.MovieEditionSlotId.Should().BeNull();
        }

        [Test]
        public void should_not_attempt_to_make_decision_if_series_is_unknown()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);

            _remoteEpisode.Movie = null;

            Subject.GetRssDecision(_reports);

            _pass1.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), null), Times.Never());
            _pass2.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), null), Times.Never());
            _pass3.Verify(c => c.IsSatisfiedBy(It.IsAny<RemoteMovie>(), null), Times.Never());
        }

        [Test]
        public void broken_report_shouldnt_blowup_the_process()
        {
            GivenSpecifications(_pass1);

            Mocker.GetMock<IParsingService>().Setup(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()))
                     .Throws<TestException>();

            _reports = new List<ReleaseInfo>
                {
                    new ReleaseInfo { Title = "Movie.2018.1080p.AMZN.WEB-DL.DD5.1.H.264-NTG" },
                    new ReleaseInfo { Title = "Movie.2018.1080p.AMZN.WEB-DL.DD5.1.H.264-NTG" },
                    new ReleaseInfo { Title = "Movie.2018.1080p.AMZN.WEB-DL.DD5.1.H.264-NTG" }
                };

            Subject.GetRssDecision(_reports);

            Mocker.GetMock<IParsingService>().Verify(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()), Times.Exactly(_reports.Count));
        }

        [Test]
        public void should_return_unknown_series_rejection_if_series_is_unknown()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);

            _remoteEpisode.Movie = null;

            var result = Subject.GetRssDecision(_reports);

            result.Should().HaveCount(1);
        }

        [Test]
        public void should_not_allow_download_if_series_is_unknown()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);

            _remoteEpisode.Movie = null;

            var result = Subject.GetRssDecision(_reports);

            result.Should().HaveCount(1);

            // result.First().RemoteMovie.DownloadAllowed.Should().BeFalse();
        }

        [Test]
        [Ignore("Series")]
        public void should_not_allow_download_if_no_episodes_found()
        {
            GivenSpecifications(_pass1, _pass2, _pass3);

            _remoteEpisode.Movie = null;

            var result = Subject.GetRssDecision(_reports);

            result.Should().HaveCount(1);

            // result.First().RemoteMovie.DownloadAllowed.Should().BeFalse();
        }

        [Test]
        public void should_return_a_decision_when_exception_is_caught()
        {
            GivenSpecifications(_pass1);

            Mocker.GetMock<IParsingService>().Setup(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()))
                     .Throws<TestException>();

            _reports = new List<ReleaseInfo>
                {
                    new ReleaseInfo { Title = "Movie.2018.1080p.AMZN.WEB-DL.DD5.1.H.264-NTG" },
                };

            Subject.GetRssDecision(_reports).Should().HaveCount(1);
        }

        [Test]
        public void automatic_main_search_should_reject_release_uniquely_matching_configured_edition()
        {
            GivenSpecifications(_pass1);
            _remoteEpisode.ParsedMovieInfo.Edition = "Director's Cut";
            GivenEditionSlots(new MovieEditionSlot { Id = 42, MovieId = 7, EditionName = "Director's Cut", Monitored = true });

            var result = Subject.GetSearchDecision(_reports, new MovieSearchCriteria { AcquisitionTarget = MovieAcquisitionTarget.Main }).Single();

            result.Approved.Should().BeFalse();
            result.Rejections.Single().Message.Should().Contain("not Main");
            result.RemoteMovie.EditionMatchResult.Status.Should().Be(EditionMatchStatus.UniqueSlot);
            result.RemoteMovie.ReleaseSource.Should().Be(ReleaseSourceType.Search);
        }

        [Test]
        public void interactive_main_search_should_reject_ambiguous_edition_evidence()
        {
            GivenSpecifications(_pass1);
            _remoteEpisode.ParsedMovieInfo.Edition = "DC";
            GivenEditionSlots(
                new MovieEditionSlot { Id = 41, MovieId = 7, EditionName = "Director's Cut", Aliases = new List<string> { "DC" }, Monitored = true },
                new MovieEditionSlot { Id = 42, MovieId = 7, EditionName = "Definitive Cut", Aliases = new List<string> { "DC" }, Monitored = true });

            var criteria = new MovieSearchCriteria { AcquisitionTarget = MovieAcquisitionTarget.Main, InteractiveSearch = true };
            var result = Subject.GetSearchDecision(_reports, criteria).Single();

            result.Approved.Should().BeFalse();
            result.Rejections.Single().Message.ToLowerInvariant().Should().Contain("multiple");
            result.RemoteMovie.EditionMatchResult.CandidateSlotIds.Should().Equal(41, 42);
            result.RemoteMovie.ReleaseSource.Should().Be(ReleaseSourceType.InteractiveSearch);
        }

        [Test]
        public void exact_slot_search_should_accept_only_unique_match_for_same_immutable_slot_id()
        {
            GivenSpecifications(_pass1);
            _remoteEpisode.ParsedMovieInfo.Edition = "Director's Cut";
            GivenEditionSlots(
                new MovieEditionSlot { Id = 41, MovieId = 7, EditionName = "Theatrical", Monitored = true },
                new MovieEditionSlot { Id = 42, MovieId = 7, EditionName = "Director's Cut", Monitored = true });

            var criteria = new MovieSearchCriteria { AcquisitionTarget = MovieAcquisitionTarget.ForEditionSlot(42) };
            var result = Subject.GetSearchDecision(_reports, criteria).Single();

            result.Approved.Should().BeTrue();
            result.RemoteMovie.AcquisitionTarget.Should().Be(MovieAcquisitionTarget.ForEditionSlot(42));
            result.RemoteMovie.SlotContextStamped.Should().BeTrue();
        }

        [Test]
        public void explicit_slot_search_should_reject_unique_match_for_different_slot_with_target_mismatch_reason()
        {
            GivenSpecifications(_pass1);
            _remoteEpisode.ParsedMovieInfo.Edition = "Theatrical";
            GivenEditionSlots(
                new MovieEditionSlot { Id = 41, MovieId = 7, EditionName = "Theatrical", Monitored = true },
                new MovieEditionSlot { Id = 42, MovieId = 7, EditionName = "Director's Cut", Monitored = true });

            var result = Subject.GetSearchDecision(_reports,
                new MovieSearchCriteria { AcquisitionTarget = MovieAcquisitionTarget.ForEditionSlot(42) }).Single();

            result.Approved.Should().BeFalse();
            result.Rejections.Single().Message.ToLowerInvariant().Should().Contain("target mismatch");
        }

        [Test]
        public void rss_should_reject_ambiguous_title_evidence_without_order_winner()
        {
            GivenSpecifications(_pass1);
            _reports[0].Title = "Movie.2018.DC.1080p.BluRay";
            GivenEditionSlots(
                new MovieEditionSlot { Id = 42, MovieId = 7, EditionName = "Definitive Cut", Aliases = new List<string> { "DC" }, Monitored = true },
                new MovieEditionSlot { Id = 41, MovieId = 7, EditionName = "Director's Cut", Aliases = new List<string> { "DC" }, Monitored = true });

            var result = Subject.GetRssDecision(_reports).Single();

            result.Approved.Should().BeFalse();
            result.RemoteMovie.EditionMatchResult.Status.Should().Be(EditionMatchStatus.Ambiguous);
            result.Rejections.Single().Message.ToLowerInvariant().Should().Contain("multiple");
            result.RemoteMovie.ReleaseSource.Should().Be(ReleaseSourceType.Rss);
        }

        [Test]
        public void release_push_should_fail_closed_for_unknown_parsed_edition()
        {
            GivenSpecifications(_pass1);
            _remoteEpisode.ParsedMovieInfo.Edition = "Producer's Cut";
            GivenEditionSlots(new MovieEditionSlot { Id = 42, MovieId = 7, EditionName = "Director's Cut", Monitored = true });

            var result = Subject.GetRssDecision(_reports, true).Single();

            result.Approved.Should().BeFalse();
            result.Rejections.Single().Message.Should().Contain("could not be mapped safely");
            result.RemoteMovie.ReleaseSource.Should().Be(ReleaseSourceType.ReleasePush);
        }

        [Test]
        public void main_search_with_no_slots_and_no_edition_evidence_should_preserve_legacy_approval()
        {
            GivenSpecifications(_pass1);
            GivenEditionSlots();

            var result = Subject.GetSearchDecision(_reports,
                new MovieSearchCriteria { AcquisitionTarget = MovieAcquisitionTarget.Main }).Single();

            result.Approved.Should().BeTrue();
            result.RemoteMovie.EditionMatchResult.Status.Should().Be(EditionMatchStatus.NoEditionEvidence);
        }

        private void GivenEditionSlots(params MovieEditionSlot[] slots)
        {
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(service => service.GetForMovie(_remoteEpisode.Movie.Id))
                .Returns(slots.ToList());
        }
    }
}
