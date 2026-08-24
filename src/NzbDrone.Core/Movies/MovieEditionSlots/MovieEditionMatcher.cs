using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public interface IMovieEditionMatcher
    {
        EditionMatchResult Match(RemoteMovie remoteMovie, IReadOnlyCollection<MovieEditionSlot> slots);
    }

    public class MovieEditionMatcher : IMovieEditionMatcher
    {
        private static readonly Regex ApostropheRegex = new Regex("['’‘]", RegexOptions.Compiled);
        private static readonly Regex TokenRegex = new Regex(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

        public EditionMatchResult Match(RemoteMovie remoteMovie, IReadOnlyCollection<MovieEditionSlot> slots)
        {
            if (remoteMovie?.ParsedMovieInfo == null)
            {
                return EditionMatchResult.Invalid("Parsed movie information is unavailable");
            }

            var configuredSlots = (slots ?? Array.Empty<MovieEditionSlot>()).ToList();
            var invalidReason = ValidateSlots(remoteMovie, configuredSlots);
            if (invalidReason != null)
            {
                return EditionMatchResult.Invalid(invalidReason);
            }

            var identities = configuredSlots
                .SelectMany(GetIdentities)
                .OrderBy(identity => identity.Slot.Id)
                .ThenBy(identity => identity.Type)
                .ThenBy(identity => identity.Normalized, StringComparer.Ordinal)
                .ThenBy(identity => identity.DisplayValue, StringComparer.Ordinal)
                .ToList();

            var normalizedParsedEdition = EditionNormalizer.Normalize(remoteMovie.ParsedMovieInfo.Edition);
            if (normalizedParsedEdition.IsNotNullOrWhiteSpace())
            {
                return MatchParsedEdition(remoteMovie.ParsedMovieInfo.Edition, normalizedParsedEdition, identities);
            }

            if (remoteMovie.Release?.Title.IsNullOrWhiteSpace() != false)
            {
                return EditionMatchResult.Invalid("Release title is unavailable for edition matching");
            }

            if (identities.Count == 0)
            {
                return EditionMatchResult.NoEvidence();
            }

            return MatchTitle(remoteMovie, identities);
        }

        private static string ValidateSlots(RemoteMovie remoteMovie, List<MovieEditionSlot> slots)
        {
            if (slots.Any(slot => slot == null || slot.Id <= 0))
            {
                return "Configured edition slots contain an invalid slot identity";
            }

            if (slots.GroupBy(slot => slot.Id).Any(group => group.Count() > 1))
            {
                return "Configured edition slots contain duplicate immutable slot IDs";
            }

            if (remoteMovie.Movie != null && slots.Any(slot => slot.MovieId != remoteMovie.Movie.Id))
            {
                return "Configured edition slots do not all belong to the matched movie";
            }

            if (slots.Any(slot => !GetIdentities(slot).Any()))
            {
                return "A configured edition slot has no valid canonical, search, or alias identity";
            }

            return null;
        }

        private static EditionMatchResult MatchParsedEdition(string parsedEdition,
                                                              string normalizedParsedEdition,
                                                              List<EditionIdentity> identities)
        {
            var matches = identities.Where(identity => identity.Normalized == normalizedParsedEdition).ToList();
            var candidateSlots = DistinctSlots(matches);
            if (candidateSlots.Count == 0)
            {
                return EditionMatchResult.Unknown(EditionMatchSource.ParsedMetadata,
                    $"Parsed edition '{parsedEdition}' could not be mapped safely to a configured edition slot");
            }

            if (candidateSlots.Count > 1)
            {
                return EditionMatchResult.Ambiguous(EditionMatchSource.ParsedMetadata, candidateSlots,
                    "Parsed edition identity maps to multiple configured edition slots");
            }

            var identity = matches
                .Where(match => match.Slot.Id == candidateSlots[0].Id)
                .OrderBy(match => match.Type)
                .ThenBy(match => match.DisplayValue, StringComparer.Ordinal)
                .First();
            return EditionMatchResult.Unique(EditionMatchSource.ParsedMetadata,
                candidateSlots,
                candidateSlots[0],
                identity.DisplayValue,
                identity.Type,
                $"Parsed edition uniquely maps to edition slot {candidateSlots[0].Id}");
        }

        private static EditionMatchResult MatchTitle(RemoteMovie remoteMovie, List<EditionIdentity> identities)
        {
            var titleTokens = Tokenize(remoteMovie.Release.Title);
            foreach (var movieTitle in remoteMovie.ParsedMovieInfo.MovieTitles ?? new List<string>())
            {
                RemoveTokenSequence(titleTokens, Tokenize(movieTitle));
            }

            RemoveTokenSequence(titleTokens, Tokenize(remoteMovie.ParsedMovieInfo.ReleaseGroup));

            var matches = identities
                .Select(identity => new TitleIdentityMatch(identity, Tokenize(identity.DisplayValue)))
                .Where(match => ContainsSequence(titleTokens, match.Tokens))
                .ToList();

            if (matches.Count == 0)
            {
                return EditionMatchResult.NoEvidence();
            }

            var candidateSlots = DistinctSlots(matches.Select(match => match.Identity));
            var maximumSpecificity = matches.Max(match => match.Tokens.Count);
            var winners = matches.Where(match => match.Tokens.Count == maximumSpecificity).ToList();
            var winningSlots = DistinctSlots(winners.Select(match => match.Identity));
            if (winningSlots.Count > 1)
            {
                return EditionMatchResult.Ambiguous(EditionMatchSource.NormalizedTitleFallback, candidateSlots,
                    "Equally specific title evidence maps to multiple configured edition slots");
            }

            var selectedSlot = winningSlots[0];
            var identity = winners
                .Where(match => match.Identity.Slot.Id == selectedSlot.Id)
                .OrderBy(match => match.Identity.Type)
                .ThenBy(match => match.Identity.Normalized, StringComparer.Ordinal)
                .ThenBy(match => match.Identity.DisplayValue, StringComparer.Ordinal)
                .First()
                .Identity;

            return EditionMatchResult.Unique(EditionMatchSource.NormalizedTitleFallback,
                candidateSlots,
                selectedSlot,
                identity.DisplayValue,
                identity.Type,
                $"Uniquely most-specific title phrase maps to edition slot {selectedSlot.Id}");
        }

        private static List<MovieEditionSlot> DistinctSlots(IEnumerable<EditionIdentity> matches)
        {
            return matches
                .Select(match => match.Slot)
                .GroupBy(slot => slot.Id)
                .Select(group => group.First())
                .OrderBy(slot => slot.Id)
                .ToList();
        }

        private static IEnumerable<EditionIdentity> GetIdentities(MovieEditionSlot slot)
        {
            if (slot == null)
            {
                yield break;
            }

            foreach (var identity in CreateIdentity(slot, slot.EditionName, EditionIdentityType.CanonicalName))
            {
                yield return identity;
            }

            foreach (var identity in CreateIdentity(slot, slot.SearchTerm, EditionIdentityType.SearchTerm))
            {
                yield return identity;
            }

            foreach (var alias in slot.Aliases ?? new List<string>())
            {
                foreach (var identity in CreateIdentity(slot, alias, EditionIdentityType.Alias))
                {
                    yield return identity;
                }
            }
        }

        private static IEnumerable<EditionIdentity> CreateIdentity(MovieEditionSlot slot, string displayValue, EditionIdentityType type)
        {
            var normalized = EditionNormalizer.Normalize(displayValue);
            if (normalized.IsNotNullOrWhiteSpace())
            {
                yield return new EditionIdentity(slot, displayValue, normalized, type);
            }
        }

        private static List<string> Tokenize(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return new List<string>();
            }

            var apostropheNormalized = ApostropheRegex.Replace(value, string.Empty);
            return TokenRegex.Matches(apostropheNormalized)
                .Select(match => EditionNormalizer.Normalize(match.Value))
                .Where(token => token.IsNotNullOrWhiteSpace())
                .ToList();
        }

        private static bool ContainsSequence(List<string> source, List<string> sequence)
        {
            if (sequence.Count == 0 || sequence.Count > source.Count)
            {
                return false;
            }

            for (var start = 0; start <= source.Count - sequence.Count; start++)
            {
                if (source.Skip(start).Take(sequence.Count).SequenceEqual(sequence))
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveTokenSequence(List<string> source, List<string> sequence)
        {
            if (sequence.Count == 0 || sequence.Count > source.Count)
            {
                return;
            }

            for (var start = source.Count - sequence.Count; start >= 0; start--)
            {
                if (source.Skip(start).Take(sequence.Count).SequenceEqual(sequence))
                {
                    source.RemoveRange(start, sequence.Count);
                }
            }
        }

        private sealed class EditionIdentity
        {
            public EditionIdentity(MovieEditionSlot slot, string displayValue, string normalized, EditionIdentityType type)
            {
                Slot = slot;
                DisplayValue = displayValue;
                Normalized = normalized;
                Type = type;
            }

            public MovieEditionSlot Slot { get; }
            public string DisplayValue { get; }
            public string Normalized { get; }
            public EditionIdentityType Type { get; }
        }

        private sealed class TitleIdentityMatch
        {
            public TitleIdentityMatch(EditionIdentity identity, List<string> tokens)
            {
                Identity = identity;
                Tokens = tokens;
            }

            public EditionIdentity Identity { get; }
            public List<string> Tokens { get; }
        }
    }
}
