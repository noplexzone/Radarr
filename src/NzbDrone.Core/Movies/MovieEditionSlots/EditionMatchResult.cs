using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public enum EditionMatchStatus
    {
        NoEditionEvidence = 0,
        UniqueSlot = 1,
        UnknownEdition = 2,
        Ambiguous = 3,
        Invalid = 4
    }

    public enum EditionIdentityType
    {
        CanonicalName = 0,
        SearchTerm = 1,
        Alias = 2
    }

    public sealed class EditionMatchResult
    {
        private EditionMatchResult(EditionMatchStatus status,
                                   EditionMatchSource source,
                                   IEnumerable<MovieEditionSlot> candidates,
                                   MovieEditionSlot selectedSlot,
                                   string matchedIdentity,
                                   EditionIdentityType? matchedIdentityType,
                                   string reason)
        {
            Status = status;
            Source = source;
            CandidateSlotIds = (candidates ?? Array.Empty<MovieEditionSlot>())
                .Select(slot => slot.Id)
                .Distinct()
                .OrderBy(id => id)
                .ToList()
                .AsReadOnly();
            SelectedSlotId = status == EditionMatchStatus.UniqueSlot ? selectedSlot?.Id : null;
            SelectedSlotEditionName = status == EditionMatchStatus.UniqueSlot ? selectedSlot?.EditionName : null;
            MatchedIdentity = status == EditionMatchStatus.UniqueSlot ? matchedIdentity : null;
            MatchedIdentityType = status == EditionMatchStatus.UniqueSlot ? matchedIdentityType : null;
            Reason = reason;
        }

        public EditionMatchStatus Status { get; }
        public EditionMatchSource Source { get; }
        public IReadOnlyList<int> CandidateSlotIds { get; }
        public int? SelectedSlotId { get; }
        public string SelectedSlotEditionName { get; }
        public string MatchedIdentity { get; }
        public EditionIdentityType? MatchedIdentityType { get; }
        public string Reason { get; }

        public static EditionMatchResult NoEvidence(string reason = "No edition evidence was found")
        {
            return new EditionMatchResult(EditionMatchStatus.NoEditionEvidence, EditionMatchSource.None, null, null, null, null, reason);
        }

        public static EditionMatchResult Unique(EditionMatchSource source,
                                                IEnumerable<MovieEditionSlot> candidates,
                                                MovieEditionSlot selectedSlot,
                                                string matchedIdentity,
                                                EditionIdentityType matchedIdentityType,
                                                string reason)
        {
            if (selectedSlot == null)
            {
                throw new ArgumentNullException(nameof(selectedSlot));
            }

            var candidateList = (candidates ?? Array.Empty<MovieEditionSlot>()).ToList();
            if (!candidateList.Any(candidate => candidate.Id == selectedSlot.Id))
            {
                throw new ArgumentException("The selected edition slot must be present in the candidate slots", nameof(candidates));
            }

            return new EditionMatchResult(EditionMatchStatus.UniqueSlot, source, candidateList, selectedSlot, matchedIdentity, matchedIdentityType, reason);
        }

        public static EditionMatchResult Unknown(EditionMatchSource source, string reason)
        {
            return new EditionMatchResult(EditionMatchStatus.UnknownEdition, source, null, null, null, null, reason);
        }

        public static EditionMatchResult Ambiguous(EditionMatchSource source, IEnumerable<MovieEditionSlot> candidates, string reason)
        {
            return new EditionMatchResult(EditionMatchStatus.Ambiguous, source, candidates, null, null, null, reason);
        }

        public static EditionMatchResult Invalid(string reason)
        {
            return new EditionMatchResult(EditionMatchStatus.Invalid, EditionMatchSource.None, null, null, null, null, reason);
        }
    }
}
