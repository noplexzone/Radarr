using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.MovieEditionSlots
{
    [TestFixture]
    public class MovieEditionMatcherFixture
    {
        private readonly MovieEditionMatcher _subject = new MovieEditionMatcher();

        private static MovieEditionSlot Slot(int id, string name, string searchTerm = null, params string[] aliases)
        {
            return new MovieEditionSlot
            {
                Id = id,
                MovieId = 7,
                EditionName = name,
                SearchTerm = searchTerm,
                Aliases = aliases?.ToList() ?? new List<string>(),
                Monitored = true
            };
        }

        private static RemoteMovie Remote(string parsedEdition = null, string title = "Movie.2024.1080p.BluRay", string movieTitle = "Movie", string releaseGroup = null)
        {
            return new RemoteMovie
            {
                Movie = new NzbDrone.Core.Movies.Movie { Id = 7 },
                ParsedMovieInfo = new ParsedMovieInfo
                {
                    Edition = parsedEdition,
                    MovieTitles = movieTitle == null ? new List<string>() : new List<string> { movieTitle },
                    ReleaseGroup = releaseGroup
                },
                Release = new ReleaseInfo { Title = title }
            };
        }

        [TestCase("Extended")]
        [TestCase("Extended Edition")]
        [TestCase("Director's Cut")]
        [TestCase("Extended Director's Cut")]
        [TestCase("Unrated")]
        [TestCase("Unrated Extended Edition")]
        [TestCase("Theatrical")]
        [TestCase("Ultimate Cut")]
        [TestCase("Final Cut")]
        public void parsed_metadata_should_uniquely_match_common_edition_identities(string edition)
        {
            var result = _subject.Match(Remote(parsedEdition: edition), new[] { Slot(42, edition) });

            result.Status.Should().Be(EditionMatchStatus.UniqueSlot);
            result.SelectedSlot.Id.Should().Be(42);
            result.Source.Should().Be(EditionMatchSource.ParsedMetadata);
            result.MatchedIdentity.Should().Be(edition);
        }

        [TestCase("Director’s Cut", "Director's Cut")]
        [TestCase("Directors.Cut", "Director's Cut")]
        [TestCase("EXTENDED-EDITION", "Extended Edition")]
        public void parsed_metadata_should_normalize_punctuation_apostrophes_and_case(string parsedEdition, string canonical)
        {
            _subject.Match(Remote(parsedEdition: parsedEdition), new[] { Slot(42, canonical) })
                .SelectedSlot.Id.Should().Be(42);
        }

        [Test]
        public void parsed_metadata_should_match_configured_abbreviation_alias()
        {
            var result = _subject.Match(Remote(parsedEdition: "DC"), new[] { Slot(42, "Director's Cut", null, "DC") });

            result.Status.Should().Be(EditionMatchStatus.UniqueSlot);
            result.MatchedIdentity.Should().Be("DC");
            result.MatchedIdentityType.Should().Be(EditionIdentityType.Alias);
        }

        [Test]
        public void aliases_that_normalize_identically_within_one_slot_should_remain_unique()
        {
            var result = _subject.Match(Remote(parsedEdition: "Director’s.Cut"),
                new[] { Slot(42, "Director's Cut", "Directors Cut", "DIRECTORS-CUT") });

            result.Status.Should().Be(EditionMatchStatus.UniqueSlot);
            result.CandidateSlotIds.Should().Equal(42);
        }

        [Test]
        public void same_normalized_alias_on_different_slots_should_be_ambiguous_independent_of_order()
        {
            var first = Slot(41, "Ultimate Cut", null, "UC");
            var second = Slot(42, "Unrated Cut", null, "U.C.");

            foreach (var slots in new[] { new[] { first, second }, new[] { second, first } })
            {
                var result = _subject.Match(Remote(parsedEdition: "UC"), slots);
                result.Status.Should().Be(EditionMatchStatus.Ambiguous);
                result.SelectedSlot.Should().BeNull();
                result.CandidateSlotIds.Should().Equal(41, 42);
                result.Reason.ToLowerInvariant().Should().Contain("multiple");
            }
        }

        [Test]
        public void parsed_metadata_mismatch_should_not_fall_back_to_matching_title()
        {
            var result = _subject.Match(
                Remote(parsedEdition: "Unknown Producer Cut", title: "Movie.2024.Directors.Cut.1080p"),
                new[] { Slot(42, "Director's Cut") });

            result.Status.Should().Be(EditionMatchStatus.UnknownEdition);
            result.SelectedSlot.Should().BeNull();
            result.Source.Should().Be(EditionMatchSource.ParsedMetadata);
        }

        [Test]
        public void no_parsed_metadata_should_use_unique_most_specific_title_phrase_in_both_slot_orderings()
        {
            var shorter = Slot(41, "Director's Cut");
            var longer = Slot(42, "Extended Director's Cut");

            foreach (var slots in new[] { new[] { shorter, longer }, new[] { longer, shorter } })
            {
                var result = _subject.Match(Remote(title: "Movie.2024.Extended.Directors.Cut.1080p"), slots);
                result.Status.Should().Be(EditionMatchStatus.UniqueSlot);
                result.SelectedSlot.Id.Should().Be(42);
                result.CandidateSlotIds.Should().Equal(41, 42);
                result.Source.Should().Be(EditionMatchSource.NormalizedTitleFallback);
            }
        }

        [Test]
        public void equal_specific_title_alias_winners_for_different_slots_should_remain_ambiguous()
        {
            var result = _subject.Match(Remote(title: "Movie.2024.DC.1080p"), new[]
            {
                Slot(41, "Director's Cut", null, "DC"),
                Slot(42, "Definitive Cut", null, "DC")
            });

            result.Status.Should().Be(EditionMatchStatus.Ambiguous);
            result.CandidateSlotIds.Should().Equal(41, 42);
        }

        [Test]
        public void title_fallback_should_not_match_terms_only_in_work_title_or_release_group()
        {
            _subject.Match(Remote(title: "Final.Cut.2024.1080p", movieTitle: "Final Cut"), new[] { Slot(41, "Final Cut") })
                .Status.Should().Be(EditionMatchStatus.NoEditionEvidence);

            _subject.Match(Remote(title: "[DC].Movie.2024.1080p-DC", releaseGroup: "DC"), new[] { Slot(42, "Director's Cut", null, "DC") })
                .Status.Should().Be(EditionMatchStatus.NoEditionEvidence);
        }

        [Test]
        public void title_fallback_should_match_phrase_boundaries_not_substrings()
        {
            _subject.Match(Remote(title: "Movie.2024.CLIMAX.1080p"), new[] { Slot(42, "IMAX") })
                .Status.Should().Be(EditionMatchStatus.NoEditionEvidence);
        }

        [Test]
        public void no_slots_and_no_evidence_should_preserve_legacy_no_edition_behavior()
        {
            var result = _subject.Match(Remote(), new List<MovieEditionSlot>());

            result.Status.Should().Be(EditionMatchStatus.NoEditionEvidence);
            result.CandidateSlotIds.Should().BeEmpty();
        }

        [Test]
        public void parsed_unknown_edition_should_fail_closed_even_when_no_slots_exist()
        {
            _subject.Match(Remote(parsedEdition: "Producer's Cut"), new List<MovieEditionSlot>())
                .Status.Should().Be(EditionMatchStatus.UnknownEdition);
        }
    }
}
