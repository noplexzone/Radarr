using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.Pending.PendingReleaseServiceTests
{
    [TestFixture]
    public class RemoveGrabbedFixture : CoreTest<PendingReleaseService>
    {
        private DownloadDecision _temporarilyRejected;
        private Movie _movie;
        private QualityProfile _profile;
        private ReleaseInfo _release;
        private ParsedMovieInfo _parsedMovieInfo;
        private RemoteMovie _remoteMovie;
        private List<PendingRelease> _heldReleases;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                                     .Build();

            _profile = new QualityProfile
            {
                Name = "Test",
                Cutoff = Quality.HDTV720p.Id,
                Items = new List<QualityProfileQualityItem>
                                   {
                                       new QualityProfileQualityItem { Allowed = true, Quality = Quality.HDTV720p },
                                       new QualityProfileQualityItem { Allowed = true, Quality = Quality.WEBDL720p },
                                       new QualityProfileQualityItem { Allowed = true, Quality = Quality.Bluray720p }
                                   },
            };

            _movie.QualityProfile = _profile;

            _release = Builder<ReleaseInfo>.CreateNew().Build();

            _parsedMovieInfo = Builder<ParsedMovieInfo>.CreateNew().Build();
            _parsedMovieInfo.Quality = new QualityModel(Quality.HDTV720p);

            _remoteMovie = new RemoteMovie();
            _remoteMovie.Movie = _movie;
            _remoteMovie.ParsedMovieInfo = _parsedMovieInfo;
            _remoteMovie.Release = _release;

            _temporarilyRejected = new DownloadDecision(_remoteMovie, new DownloadRejection(DownloadRejectionReason.MinimumAgeDelay, "Temp Rejected", RejectionType.Temporary));

            _heldReleases = new List<PendingRelease>();

            Mocker.GetMock<IPendingReleaseRepository>()
                  .Setup(s => s.All())
                  .Returns(_heldReleases);

            Mocker.GetMock<IPendingReleaseRepository>()
                  .Setup(s => s.AllByMovieId(It.IsAny<int>()))
                  .Returns<int>(i => _heldReleases.Where(v => v.MovieId == i).ToList());

            Mocker.GetMock<IMovieService>()
                  .Setup(s => s.GetMovie(It.IsAny<int>()))
                  .Returns(_movie);

            Mocker.GetMock<IMovieService>()
                  .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<Movie> { _movie });

            Mocker.GetMock<IPrioritizeDownloadDecision>()
                  .Setup(s => s.PrioritizeDecisionsForMovies(It.IsAny<List<DownloadDecision>>()))
                  .Returns((List<DownloadDecision> d) => d);
        }

        private void GivenHeldRelease(QualityModel quality)
        {
            var parsedMovieInfo = _parsedMovieInfo.JsonClone();
            parsedMovieInfo.Quality = quality;

            var heldReleases = Builder<PendingRelease>.CreateListOfSize(1)
                                                   .All()
                                                   .With(h => h.MovieId = _movie.Id)
                                                   .With(h => h.Release = _release.JsonClone())
                                                   .With(h => h.ParsedMovieInfo = parsedMovieInfo)
                                                   .Build();

            _heldReleases.AddRange(heldReleases);
        }

        [Test]
        public void should_delete_if_the_grabbed_quality_is_the_same()
        {
            GivenHeldRelease(_parsedMovieInfo.Quality);

            Subject.Handle(new MovieGrabbedEvent(_remoteMovie));

            VerifyDelete();
        }

        [Test]
        public void should_delete_if_the_grabbed_quality_is_the_higher()
        {
            GivenHeldRelease(new QualityModel(Quality.SDTV));

            Subject.Handle(new MovieGrabbedEvent(_remoteMovie));

            VerifyDelete();
        }

        [Test]
        public void should_not_delete_if_the_grabbed_quality_is_the_lower()
        {
            GivenHeldRelease(new QualityModel(Quality.Bluray720p));

            Subject.Handle(new MovieGrabbedEvent(_remoteMovie));

            VerifyNoDelete();
        }

        [Test]
        public void should_isolate_targets_before_reconstructing_grabbed_cleanup_rows()
        {
            var slotA = MovieAcquisitionTarget.ForEditionSlot(41);
            var slotB = MovieAcquisitionTarget.ForEditionSlot(42);
            var malformedSlotA = BuildHeldRelease(1, slotA, _parsedMovieInfo.Quality);
            malformedSlotA.ParsedMovieInfo = null;
            var main = BuildHeldRelease(2, MovieAcquisitionTarget.Main, _parsedMovieInfo.Quality);
            var validSlotB = BuildHeldRelease(3, slotB, _parsedMovieInfo.Quality);
            _heldReleases.AddRange(new[] { malformedSlotA, main, validSlotB });
            _remoteMovie.AcquisitionTarget = slotB;
            Mocker.GetMock<IRemoteMovieAggregationService>()
                  .Setup(v => v.Augment(It.Is<RemoteMovie>(r => r.ParsedMovieInfo == null)))
                  .Throws(new System.InvalidOperationException("malformed slot A"));

            Subject.Handle(new MovieGrabbedEvent(_remoteMovie));

            Mocker.GetMock<IPendingReleaseRepository>()
                  .Verify(v => v.Delete(It.Is<PendingRelease>(p => p.Id == validSlotB.Id)), Times.Once());
            Mocker.GetMock<IPendingReleaseRepository>()
                  .Verify(v => v.Delete(It.Is<PendingRelease>(p => p.Id == malformedSlotA.Id || p.Id == main.Id)), Times.Never());
            Mocker.GetMock<IRemoteMovieAggregationService>()
                  .Verify(v => v.Augment(It.IsAny<RemoteMovie>()), Times.Never());
        }

        private PendingRelease BuildHeldRelease(int id, MovieAcquisitionTarget target, QualityModel quality)
        {
            var additionalInfo = new PendingReleaseAdditionalInfo();
            additionalInfo.SerializeAcquisitionTarget(target);
            var parsedMovieInfo = _parsedMovieInfo.JsonClone();
            parsedMovieInfo.Quality = quality;

            return new PendingRelease
            {
                Id = id,
                MovieId = _movie.Id,
                Release = _release.JsonClone(),
                ParsedMovieInfo = parsedMovieInfo,
                AdditionalInfo = additionalInfo
            };
        }

        private void VerifyDelete()
        {
            Mocker.GetMock<IPendingReleaseRepository>()
                .Verify(v => v.Delete(It.IsAny<PendingRelease>()), Times.Once());
        }

        private void VerifyNoDelete()
        {
            Mocker.GetMock<IPendingReleaseRepository>()
                .Verify(v => v.Delete(It.IsAny<PendingRelease>()), Times.Never());
        }
    }
}
