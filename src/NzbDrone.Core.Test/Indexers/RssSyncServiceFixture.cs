using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Indexers
{
    [TestFixture]
    public class RssSyncServiceFixture : CoreTest<RssSyncService>
    {
        [Test]
        public void should_route_pending_retries_through_the_authoritative_target_decision_path()
        {
            var rssRelease = new ReleaseInfo { Title = "Fresh.Movie.2026" };
            var pendingRemoteMovie = new RemoteMovie
            {
                Movie = new Movie { Id = 7 },
                Release = new ReleaseInfo { Title = "Pending.Movie.2026" },
                AcquisitionTarget = MovieAcquisitionTarget.ForEditionSlot(41)
            };
            var pendingRelease = new PendingReleaseInfo(pendingRemoteMovie, PendingReleaseReason.Delay);
            var rssDecision = new DownloadDecision(new RemoteMovie { Release = rssRelease });
            var pendingDecision = new DownloadDecision(pendingRemoteMovie);

            Mocker.GetMock<IFetchAndParseRss>()
                  .Setup(s => s.Fetch())
                  .ReturnsAsync(new List<ReleaseInfo> { rssRelease });
            Mocker.GetMock<IPendingReleaseService>()
                  .Setup(s => s.GetPending())
                  .Returns(new List<PendingReleaseInfo> { pendingRelease });
            Mocker.GetMock<IMakeDownloadDecision>()
                  .Setup(s => s.GetRssDecision(It.IsAny<List<ReleaseInfo>>(), false))
                  .Returns(new List<DownloadDecision> { rssDecision });
            Mocker.GetMock<IMakeDownloadDecision>()
                  .Setup(s => s.GetPendingDecision(It.IsAny<List<PendingReleaseInfo>>()))
                  .Returns(new List<DownloadDecision> { pendingDecision });
            Mocker.GetMock<IProcessDownloadDecisions>()
                  .Setup(s => s.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                  .ReturnsAsync(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision>()));

            Subject.Execute(new RssSyncCommand());

            Mocker.GetMock<IMakeDownloadDecision>()
                  .Verify(s => s.GetRssDecision(It.Is<List<ReleaseInfo>>(r => r.Count == 1 && r[0] == rssRelease), false), Times.Once());
            Mocker.GetMock<IMakeDownloadDecision>()
                  .Verify(s => s.GetPendingDecision(It.Is<List<PendingReleaseInfo>>(p => p.Count == 1 && p[0].AcquisitionTarget.Equals(MovieAcquisitionTarget.ForEditionSlot(41)))), Times.Once());
            Mocker.GetMock<IProcessDownloadDecisions>()
                  .Verify(s => s.ProcessDecisions(It.Is<List<DownloadDecision>>(d => d.Count == 2 && d[0] == rssDecision && d[1] == pendingDecision)), Times.Once());
        }
    }
}
