using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.Search;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.DecisionEngine
{
    public interface IMakeDownloadDecision
    {
        List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false);
        List<DownloadDecision> GetPendingDecision(List<PendingReleaseInfo> pendingReleases);
        List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase);
    }

    public class DownloadDecisionMaker : IMakeDownloadDecision
    {
        private readonly IEnumerable<IDownloadDecisionEngineSpecification> _specifications;
        private readonly IParsingService _parsingService;
        private readonly IConfigService _configService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IRemoteMovieAggregationService _aggregationService;
        private readonly IMovieEditionSlotService _editionSlotService;
        private readonly IMovieEditionMatcher _editionMatcher;
        private readonly IMediaFileService _mediaFileService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly Logger _logger;

        public DownloadDecisionMaker(IEnumerable<IDownloadDecisionEngineSpecification> specifications,
                                     IParsingService parsingService,
                                     IConfigService configService,
                                     ICustomFormatCalculationService formatCalculator,
                                     IRemoteMovieAggregationService aggregationService,
                                     IMovieEditionSlotService editionSlotService,
                                     IMovieEditionMatcher editionMatcher,
                                     IMediaFileService mediaFileService,
                                     IQualityProfileService qualityProfileService,
                                     Logger logger)
        {
            _specifications = specifications;
            _parsingService = parsingService;
            _configService = configService;
            _formatCalculator = formatCalculator;
            _aggregationService = aggregationService;
            _editionSlotService = editionSlotService;
            _editionMatcher = editionMatcher;
            _mediaFileService = mediaFileService;
            _qualityProfileService = qualityProfileService;
            _logger = logger;
        }

        public List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false)
        {
            return GetDecisions(reports, pushedRelease).ToList();
        }

        public List<DownloadDecision> GetPendingDecision(List<PendingReleaseInfo> pendingReleases)
        {
            return GetDecisions(pendingReleases.Select(p => p.Release).ToList(), false, null, pendingReleases).ToList();
        }

        public List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase)
        {
            return GetDecisions(reports, false, searchCriteriaBase).ToList();
        }

        private IEnumerable<DownloadDecision> GetDecisions(List<ReleaseInfo> reports,
                                                            bool pushedRelease = false,
                                                            SearchCriteriaBase searchCriteria = null,
                                                            List<PendingReleaseInfo> pendingReleases = null)
        {
            if (reports.Any())
            {
                _logger.ProgressInfo("Processing {0} releases", reports.Count);
            }
            else
            {
                _logger.ProgressInfo("No results found");
            }

            var reportNumber = 1;

            foreach (var report in reports)
            {
                DownloadDecision decision = null;
                _logger.ProgressTrace("Processing release {0}/{1}", reportNumber, reports.Count);
                _logger.Debug("Processing release '{0}' from '{1}'", report.Title, report.Indexer);

                try
                {
                    var pendingRelease = pendingReleases?[reportNumber - 1];
                    var parsedMovieInfo = pendingRelease?.RemoteMovie.ParsedMovieInfo ?? Parser.Parser.ParseMovieTitle(report.Title);

                    if (parsedMovieInfo != null && !parsedMovieInfo.PrimaryMovieTitle.IsNullOrWhiteSpace())
                    {
                        var remoteMovie = pendingRelease?.RemoteMovie ??
                                          _parsingService.Map(parsedMovieInfo, report.ImdbId.ToString(), report.TmdbId, searchCriteria);
                        remoteMovie.Release = report;

                        if (remoteMovie.Movie == null)
                        {
                            decision = new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.UnknownMovie, pushedRelease ? "Unknown Movie. Unable to match to existing movie in Library using release title." : "Unknown Movie. Unable to match to correct movie using release title."));
                        }
                        else
                        {
                            _aggregationService.Augment(remoteMovie);
                            if (searchCriteria is MovieSearchCriteria movieSearchCriteria)
                            {
                                remoteMovie.AcquisitionTarget = movieSearchCriteria.AcquisitionTarget;
                            }

                            // Resolve edition identity and target isolation before custom-format
                            // scoring and every downstream decision specification.
                            var editionRejection = PrepareEditionContext(remoteMovie, searchCriteria, pendingRelease != null);

                            remoteMovie.CustomFormats = _formatCalculator.ParseCustomFormat(remoteMovie, remoteMovie.Release.Size);
                            var effectiveQualityProfile = remoteMovie.SlotQualityProfile ??
                                                          (searchCriteria as MovieSearchCriteria)?.OverrideQualityProfile ??
                                                          remoteMovie.Movie.QualityProfile;
                            remoteMovie.CustomFormatScore = effectiveQualityProfile?.CalculateCustomFormatScore(remoteMovie.CustomFormats) ?? 0;

                            _logger.Trace("Custom Format Score of '{0}' [{1}] calculated for '{2}'", remoteMovie.CustomFormatScore, remoteMovie.CustomFormats?.ConcatToString(), report.Title);

                            remoteMovie.DownloadAllowed = remoteMovie.Movie != null && editionRejection == null;
                            decision = editionRejection == null
                                ? GetDecisionForReport(remoteMovie, searchCriteria)
                                : new DownloadDecision(remoteMovie, editionRejection);
                        }
                    }

                    if (searchCriteria != null)
                    {
                        if (parsedMovieInfo == null)
                        {
                            parsedMovieInfo = new ParsedMovieInfo
                            {
                                Languages = LanguageParser.ParseLanguages(report.Title),
                                Quality = QualityParser.ParseQuality(report.Title)
                            };
                        }

                        if (parsedMovieInfo.PrimaryMovieTitle.IsNullOrWhiteSpace())
                        {
                            var remoteMovie = new RemoteMovie
                            {
                                Release = report,
                                ParsedMovieInfo = parsedMovieInfo,
                                Languages = parsedMovieInfo.Languages
                            };

                            decision = new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.UnableToParse, "Unable to parse release"));
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Couldn't process release.");

                    var remoteMovie = new RemoteMovie { Release = report };
                    decision = new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.Error, "Unexpected error processing release"));
                }

                reportNumber++;

                if (decision != null)
                {
                    var source = pushedRelease ? ReleaseSourceType.ReleasePush : ReleaseSourceType.Rss;

                    if (searchCriteria != null)
                    {
                        if (searchCriteria.InteractiveSearch)
                        {
                            source = ReleaseSourceType.InteractiveSearch;
                        }
                        else if (searchCriteria.UserInvokedSearch)
                        {
                            source = ReleaseSourceType.UserInvokedSearch;
                        }
                        else
                        {
                            source = ReleaseSourceType.Search;
                        }
                    }

                    decision.RemoteMovie.ReleaseSource = source;

                    if (searchCriteria is MovieSearchCriteria movieCriteria)
                    {
                        decision.RemoteMovie.AcquisitionTarget = movieCriteria.AcquisitionTarget;
                    }

                    if (decision.Rejections.Any())
                    {
                        _logger.Debug("Release '{0}' from '{1}' rejected for the following reasons: {2}", report.Title, report.Indexer, string.Join(", ", decision.Rejections));
                    }
                    else
                    {
                        _logger.Debug("Release '{0}' from '{1}' accepted", report.Title, report.Indexer);
                    }

                    yield return decision;
                }
            }
        }

        private DownloadRejection PrepareEditionContext(RemoteMovie remoteMovie, SearchCriteriaBase searchCriteria, bool persistedTarget = false)
        {
            var slots = _editionSlotService.GetForMovie(remoteMovie.Movie.Id) ?? new List<MovieEditionSlot>();
            var match = _editionMatcher.Match(remoteMovie, slots);
            remoteMovie.EditionMatchResult = match;

            var target = remoteMovie.AcquisitionTarget;
            _logger.Debug("Edition match movieId={0} targetKind={1} targetSlotId={2} candidateSlotIds=[{3}] source={4} status={5} reason={6}",
                remoteMovie.Movie.Id,
                target.Kind,
                target.EditionSlotId,
                string.Join(",", match.CandidateSlotIds),
                match.Source,
                match.Status,
                match.Reason);

            var selectedSlot = match.SelectedSlotId.HasValue
                ? slots.SingleOrDefault(slot => slot.Id == match.SelectedSlotId.Value)
                : null;
            if (match.Status == EditionMatchStatus.UniqueSlot && selectedSlot == null)
            {
                return new DownloadRejection(DownloadRejectionReason.WrongEdition,
                    $"Matched edition slot {match.SelectedSlotId} is no longer configured for this movie");
            }

            // RSS and release-push have no preselected slot target. A unique deterministic
            // match establishes the exact slot target; absent evidence remains ordinary Main.
            if (searchCriteria == null && !persistedTarget)
            {
                if (match.Status == EditionMatchStatus.NoEditionEvidence)
                {
                    return null;
                }

                if (match.Status != EditionMatchStatus.UniqueSlot)
                {
                    return RejectUnsafeEditionMatch(match);
                }

                if (!selectedSlot.Monitored)
                {
                    return new DownloadRejection(DownloadRejectionReason.WrongEdition,
                        $"Release matches unmonitored edition slot: {match.SelectedSlotEditionName}");
                }

                remoteMovie.AcquisitionTarget = MovieAcquisitionTarget.ForEditionSlot(match.SelectedSlotId.Value);
                StampEditionSlotContext(remoteMovie, selectedSlot);
                return null;
            }

            if (target.Kind == MovieAcquisitionTargetKind.Main)
            {
                if (match.Status == EditionMatchStatus.NoEditionEvidence)
                {
                    return null;
                }

                if (match.Status == EditionMatchStatus.UniqueSlot)
                {
                    return new DownloadRejection(DownloadRejectionReason.WrongEdition,
                        $"Release matches configured edition '{match.SelectedSlotEditionName}' (slot {match.SelectedSlotId.Value}), not Main");
                }

                return RejectUnsafeEditionMatch(match);
            }

            if (target.Kind != MovieAcquisitionTargetKind.EditionSlot || !target.EditionSlotId.HasValue)
            {
                return new DownloadRejection(DownloadRejectionReason.WrongEdition,
                    "Release acquisition target is unknown and cannot be matched safely");
            }

            var targetSlot = slots.SingleOrDefault(slot => slot.Id == target.EditionSlotId.Value);
            if (targetSlot == null)
            {
                return new DownloadRejection(DownloadRejectionReason.WrongEdition,
                    $"Requested edition slot {target.EditionSlotId.Value} is no longer configured for this movie");
            }

            if (persistedTarget && !targetSlot.Monitored)
            {
                return new DownloadRejection(DownloadRejectionReason.WrongEdition,
                    $"Requested edition slot is no longer monitored: {targetSlot.EditionName}");
            }

            if (match.Status != EditionMatchStatus.UniqueSlot)
            {
                return RejectUnsafeEditionMatch(match);
            }

            if (match.SelectedSlotId.Value != target.EditionSlotId.Value)
            {
                return new DownloadRejection(DownloadRejectionReason.WrongEdition,
                    $"Edition target mismatch: release matches slot {match.SelectedSlotId.Value}, but slot {target.EditionSlotId.Value} was requested");
            }

            StampEditionSlotContext(remoteMovie, selectedSlot,
                (searchCriteria as MovieSearchCriteria)?.OverrideQualityProfile);
            return null;
        }

        private static DownloadRejection RejectUnsafeEditionMatch(EditionMatchResult match)
        {
            var message = match.Status switch
            {
                EditionMatchStatus.Ambiguous => $"Release edition evidence matches multiple configured edition slots ({string.Join(", ", match.CandidateSlotIds)}): {match.Reason}",
                EditionMatchStatus.UnknownEdition => match.Reason,
                EditionMatchStatus.Invalid => $"Release edition could not be mapped safely: {match.Reason}",
                EditionMatchStatus.NoEditionEvidence => "Release edition could not be mapped safely to the requested edition slot",
                _ => match.Reason
            };

            return new DownloadRejection(DownloadRejectionReason.WrongEdition, message);
        }

        private void StampEditionSlotContext(RemoteMovie remoteMovie, MovieEditionSlot slot, QualityProfile overrideQualityProfile = null)
        {
            remoteMovie.MovieEditionSlotId = slot.Id;
            remoteMovie.SlotContextStamped = true;
            remoteMovie.SlotMovieFile = null;
            remoteMovie.SlotQualityProfile = overrideQualityProfile;
            remoteMovie.SlotMinimumCustomFormatScore = slot.MinimumCustomFormatScore;

            if (remoteMovie.SlotQualityProfile == null && slot.QualityProfileId.HasValue)
            {
                remoteMovie.SlotQualityProfile = _qualityProfileService.Get(slot.QualityProfileId.Value);
            }

            remoteMovie.SlotQualityProfile ??= remoteMovie.Movie.QualityProfile;
            remoteMovie.SlotMovieFile = _mediaFileService.GetFilesByMovie(remoteMovie.Movie.Id)
                .SingleOrDefault(f => f.MovieEditionSlotId == slot.Id);
        }

        private DownloadDecision GetDecisionForReport(RemoteMovie remoteMovie, SearchCriteriaBase searchCriteria = null)
        {
            var reasons = Array.Empty<DownloadRejection>();

            foreach (var specifications in _specifications.GroupBy(v => v.Priority).OrderBy(v => v.Key))
            {
                reasons = specifications.Select(c => EvaluateSpec(c, remoteMovie, searchCriteria))
                                        .Where(c => c != null)
                                        .ToArray();

                if (reasons.Any())
                {
                    break;
                }
            }

            return new DownloadDecision(remoteMovie, reasons.ToArray());
        }

        private DownloadRejection EvaluateSpec(IDownloadDecisionEngineSpecification spec, RemoteMovie remoteMovie, SearchCriteriaBase searchCriteriaBase = null)
        {
            try
            {
                var result = spec.IsSatisfiedBy(remoteMovie, searchCriteriaBase);

                if (!result.Accepted)
                {
                    return new DownloadRejection(result.Reason, result.Message, spec.Type);
                }
            }
            catch (NotImplementedException)
            {
                _logger.Trace("Spec " + spec.GetType().Name + " does not care about movies.");
            }
            catch (Exception e)
            {
                e.Data.Add("report", remoteMovie.Release.ToJson());
                e.Data.Add("parsed", remoteMovie.ParsedMovieInfo.ToJson());
                _logger.Error(e, "Couldn't evaluate decision on {0}, with spec: {1}", remoteMovie.Release.Title, spec.GetType().Name);
                return new DownloadRejection(DownloadRejectionReason.DecisionError, $"{spec.GetType().Name}: {e.Message}");
            }

            return null;
        }
    }
}
