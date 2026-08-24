using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.Pending.PendingReleaseServiceTests
{
    [TestFixture]
    public class AddFixture : CoreTest<PendingReleaseService>
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

            Mocker.GetMock<IPrioritizeDownloadDecision>()
                  .Setup(s => s.PrioritizeDecisionsForMovies(It.IsAny<List<DownloadDecision>>()))
                  .Returns((List<DownloadDecision> d) => d);
        }

        private void GivenHeldRelease(string title, string indexer, DateTime publishDate, PendingReleaseReason reason = PendingReleaseReason.Delay, MovieAcquisitionTarget target = null)
        {
            var release = _release.JsonClone();
            release.Indexer = indexer;
            release.PublishDate = publishDate;

            var heldReleases = Builder<PendingRelease>.CreateListOfSize(1)
                                                   .All()
                                                   .With(h => h.MovieId = _movie.Id)
                                                   .With(h => h.Title = title)
                                                   .With(h => h.Release = release)
                                                   .With(h => h.Reason = reason)
                                                   .With(h => h.ParsedMovieInfo = _parsedMovieInfo)
                                                   .With(h => h.AdditionalInfo = PendingInfoFor(target ?? MovieAcquisitionTarget.Main))
                                                   .Build();

            _heldReleases.AddRange(heldReleases);
        }

        private static PendingReleaseAdditionalInfo PendingInfoFor(MovieAcquisitionTarget target)
        {
            return new PendingReleaseAdditionalInfo
            {
                AcquisitionTargetKind = target.Kind,
                AcquisitionTargetEditionSlotId = target.EditionSlotId
            };
        }

        [Test]
        public void should_add()
        {
            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyInsert();
        }

        [TestCase(MovieAcquisitionTargetKind.Main, null)]
        [TestCase(MovieAcquisitionTargetKind.EditionSlot, 41)]
        public void should_persist_the_exact_acquisition_target(MovieAcquisitionTargetKind kind, int? slotId)
        {
            var target = kind == MovieAcquisitionTargetKind.EditionSlot
                ? MovieAcquisitionTarget.ForEditionSlot(slotId.Value)
                : MovieAcquisitionTarget.Main;
            _remoteMovie.AcquisitionTarget = target;

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            Mocker.GetMock<IPendingReleaseRepository>()
                  .Verify(v => v.Insert(It.Is<PendingRelease>(p =>
                      p.AdditionalInfo.AcquisitionTargetKind == kind &&
                      p.AdditionalInfo.AcquisitionTargetEditionSlotId == slotId)), Times.Once());
        }

        [Test]
        public void should_retain_the_same_release_for_main_and_each_edition_slot()
        {
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate, target: MovieAcquisitionTarget.Main);

            var decisions = new[]
            {
                MovieAcquisitionTarget.Main,
                MovieAcquisitionTarget.ForEditionSlot(41),
                MovieAcquisitionTarget.ForEditionSlot(42)
            }.Select(target =>
            {
                var remoteMovie = new RemoteMovie
                {
                    Movie = _movie,
                    ParsedMovieInfo = _parsedMovieInfo,
                    Release = _release,
                    AcquisitionTarget = target
                };

                return Tuple.Create(new DownloadDecision(remoteMovie), PendingReleaseReason.Delay);
            }).ToList();

            Subject.AddMany(decisions);

            Mocker.GetMock<IPendingReleaseRepository>()
                  .Verify(v => v.Insert(It.IsAny<PendingRelease>()), Times.Exactly(2));
        }

        [Test]
        public void should_not_add_if_it_is_the_same_release_from_the_same_indexer()
        {
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate);

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyNoInsert();
        }

        [Test]
        public void should_not_add_if_it_is_the_same_release_from_the_same_indexer_twice()
        {
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate, PendingReleaseReason.DownloadClientUnavailable);
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate, PendingReleaseReason.Fallback);

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyNoInsert();
        }

        [Test]
        public void should_remove_duplicate_if_it_is_the_same_release_from_the_same_indexer_twice()
        {
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate, PendingReleaseReason.DownloadClientUnavailable);
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate, PendingReleaseReason.Fallback);

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Fallback);

            Mocker.GetMock<IPendingReleaseRepository>()
                  .Verify(v => v.Delete(It.IsAny<int>()), Times.Once());
        }

        [Test]
        public void should_add_if_title_is_different()
        {
            GivenHeldRelease(_release.Title + "-RP", _release.Indexer, _release.PublishDate);

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyInsert();
        }

        [Test]
        public void should_add_if_indexer_is_different()
        {
            GivenHeldRelease(_release.Title, "AnotherIndexer", _release.PublishDate);

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyInsert();
        }

        [Test]
        public void should_add_if_publish_date_is_different()
        {
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate.AddHours(1));

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyInsert();
        }

        private void VerifyInsert()
        {
            Mocker.GetMock<IPendingReleaseRepository>()
                .Verify(v => v.Insert(It.IsAny<PendingRelease>()), Times.Once());
        }

        private void VerifyNoInsert()
        {
            Mocker.GetMock<IPendingReleaseRepository>()
                .Verify(v => v.Insert(It.IsAny<PendingRelease>()), Times.Never());
        }
    }
}
