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
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.DecisionEngine
{
    public interface IMakeDownloadDecision
    {
        List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false);
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
        private readonly IMediaFileService _mediaFileService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly Logger _logger;

        public DownloadDecisionMaker(IEnumerable<IDownloadDecisionEngineSpecification> specifications,
                                     IParsingService parsingService,
                                     IConfigService configService,
                                     ICustomFormatCalculationService formatCalculator,
                                     IRemoteMovieAggregationService aggregationService,
                                     IMovieEditionSlotService editionSlotService,
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
            _mediaFileService = mediaFileService;
            _qualityProfileService = qualityProfileService;
            _logger = logger;
        }

        public List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false)
        {
            return GetDecisions(reports, pushedRelease).ToList();
        }

        public List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase)
        {
            return GetDecisions(reports, false, searchCriteriaBase).ToList();
        }

        private IEnumerable<DownloadDecision> GetDecisions(List<ReleaseInfo> reports, bool pushedRelease = false, SearchCriteriaBase searchCriteria = null)
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
                    var parsedMovieInfo = Parser.Parser.ParseMovieTitle(report.Title);

                    if (parsedMovieInfo != null && !parsedMovieInfo.PrimaryMovieTitle.IsNullOrWhiteSpace())
                    {
                        var remoteMovie = _parsingService.Map(parsedMovieInfo, report.ImdbId.ToString(), report.TmdbId, searchCriteria);
                        remoteMovie.Release = report;

                        if (remoteMovie.Movie == null)
                        {
                            decision = new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.UnknownMovie, pushedRelease ? "Unknown Movie. Unable to match to existing movie in Library using release title." : "Unknown Movie. Unable to match to correct movie using release title."));
                        }
                        else
                        {
                            _aggregationService.Augment(remoteMovie);
                            DownloadRejection rssEditionRejection = null;

                            // Slot context must be available before custom-format scoring and
                            // decision specifications evaluate the release.
                            if (searchCriteria is MovieSearchCriteria movieSearchCriteria && movieSearchCriteria.MovieEditionSlotId.HasValue)
                            {
                                StampExplicitEditionSlotContext(remoteMovie, movieSearchCriteria);
                            }
                            else if (searchCriteria == null)
                            {
                                // For RSS, match a parsed edition to a monitored slot so
                                // scoring and disk-comparison specs use the slot context.
                                rssEditionRejection = StampRssEditionSlotContext(remoteMovie);
                            }

                            remoteMovie.CustomFormats = _formatCalculator.ParseCustomFormat(remoteMovie, remoteMovie.Release.Size);
                            var effectiveQualityProfile = remoteMovie.SlotQualityProfile ??
                                                          (searchCriteria as MovieSearchCriteria)?.OverrideQualityProfile ??
                                                          remoteMovie.Movie.QualityProfile;
                            remoteMovie.CustomFormatScore = effectiveQualityProfile?.CalculateCustomFormatScore(remoteMovie.CustomFormats) ?? 0;

                            _logger.Trace("Custom Format Score of '{0}' [{1}] calculated for '{2}'", remoteMovie.CustomFormatScore, remoteMovie.CustomFormats?.ConcatToString(), report.Title);

                            remoteMovie.DownloadAllowed = remoteMovie.Movie != null && rssEditionRejection == null;
                            decision = rssEditionRejection == null
                                ? GetDecisionForReport(remoteMovie, searchCriteria)
                                : new DownloadDecision(remoteMovie, rssEditionRejection);
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
                        decision.RemoteMovie.MovieEditionSlotId = movieCriteria.MovieEditionSlotId;
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

        private void StampExplicitEditionSlotContext(RemoteMovie remoteMovie, MovieSearchCriteria searchCriteria)
        {
            var requestedSlotId = searchCriteria.MovieEditionSlotId.Value;
            var slot = _editionSlotService.GetForMovie(remoteMovie.Movie.Id)
                ?.FirstOrDefault(s => s.Id == requestedSlotId && s.MovieId == remoteMovie.Movie.Id);

            if (slot == null)
            {
                return;
            }

            StampEditionSlotContext(remoteMovie, slot, searchCriteria.OverrideQualityProfile);
        }

        private DownloadRejection StampRssEditionSlotContext(RemoteMovie remoteMovie)
        {
            var slots = _editionSlotService.GetForMovie(remoteMovie.Movie.Id) ?? new List<MovieEditionSlot>();
            var normalizedParsedEdition = EditionNormalizer.Normalize(remoteMovie.ParsedMovieInfo?.Edition);
            MovieEditionSlot slot;

            if (!normalizedParsedEdition.IsNullOrWhiteSpace())
            {
                // Parsed edition identity is authoritative and must never fall through to main.
                slot = slots.FirstOrDefault(s => MatchesParsedEditionSlot(normalizedParsedEdition, s));
            }
            else
            {
                // A configured title term is also edition-targeted, including unmonitored slots.
                slot = slots.FirstOrDefault(s => MatchesRssEditionTitle(remoteMovie, s));
            }

            if (slot == null)
            {
                if (normalizedParsedEdition.IsNullOrWhiteSpace())
                {
                    return null;
                }

                return new DownloadRejection(DownloadRejectionReason.WrongEdition,
                    $"Parsed release edition is not configured for this movie: {remoteMovie.ParsedMovieInfo.Edition}");
            }

            if (!slot.Monitored)
            {
                return new DownloadRejection(DownloadRejectionReason.WrongEdition,
                    $"Release matches unmonitored edition slot: {slot.EditionName}");
            }

            _logger.Debug("RSS release '{0}' matched edition slot '{1}' (id={2})", remoteMovie.Release.Title, slot.EditionName, slot.Id);
            StampEditionSlotContext(remoteMovie, slot);
            return null;
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

        private static bool MatchesParsedEditionSlot(string normalizedParsedEdition, MovieEditionSlot slot)
        {
            var normalizedEditionName = EditionNormalizer.Normalize(slot.EditionName);
            var normalizedSearchTerm = EditionNormalizer.Normalize(slot.SearchTerm);

            return normalizedParsedEdition == normalizedEditionName ||
                   normalizedParsedEdition == normalizedSearchTerm ||
                   (slot.Aliases ?? new List<string>()).Any(alias => normalizedParsedEdition == EditionNormalizer.Normalize(alias));
        }

        private static bool MatchesRssEditionTitle(RemoteMovie remoteMovie, MovieEditionSlot slot)
        {
            return (!slot.EditionName.IsNullOrWhiteSpace() && TitleContainsExactEditionTerm(remoteMovie, slot.EditionName)) ||
                   (!slot.SearchTerm.IsNullOrWhiteSpace() && TitleContainsExactEditionTerm(remoteMovie, slot.SearchTerm)) ||
                   (slot.Aliases ?? new List<string>()).Any(alias => TitleContainsExactEditionTerm(remoteMovie, alias));
        }

        private static bool TitleContainsExactEditionTerm(RemoteMovie remoteMovie, string editionTerm)
        {
            return MovieEditionSpecification.TitleContainsEditionTerm(remoteMovie, editionTerm);
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
