using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.History;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.MovieTests
{
    [TestFixture]
    public class MovieAcquisitionTargetFixture
    {
        [Test]
        public void should_represent_main_slot_and_unknown_as_distinct_values()
        {
            MovieAcquisitionTarget.Main.Kind.Should().Be(MovieAcquisitionTargetKind.Main);
            MovieAcquisitionTarget.Main.EditionSlotId.Should().BeNull();
            MovieAcquisitionTarget.ForEditionSlot(42).Kind.Should().Be(MovieAcquisitionTargetKind.EditionSlot);
            MovieAcquisitionTarget.ForEditionSlot(42).EditionSlotId.Should().Be(42);
            MovieAcquisitionTarget.Unknown.Kind.Should().Be(MovieAcquisitionTargetKind.Unknown);
            MovieAcquisitionTarget.Unknown.EditionSlotId.Should().BeNull();
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void should_reject_non_positive_slot_ids(int slotId)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MovieAcquisitionTarget.ForEditionSlot(slotId));
        }

        [Test]
        public void should_have_deterministic_value_equality()
        {
            MovieAcquisitionTarget.ForEditionSlot(42).Should().Be(MovieAcquisitionTarget.ForEditionSlot(42));
            MovieAcquisitionTarget.ForEditionSlot(42).GetHashCode().Should().Be(MovieAcquisitionTarget.ForEditionSlot(42).GetHashCode());
            MovieAcquisitionTarget.ForEditionSlot(42).Should().NotBe(MovieAcquisitionTarget.ForEditionSlot(43));
            MovieAcquisitionTarget.Main.Should().NotBe(MovieAcquisitionTarget.Unknown);
        }

        [Test]
        public void should_round_trip_explicit_history_serialization()
        {
            var data = new Dictionary<string, string>();
            MovieAcquisitionTargetSerializer.Write(data, MovieAcquisitionTarget.ForEditionSlot(42));

            data[MovieHistory.ACQUISITION_TARGET].Should().Be("editionSlot");
            data[MovieHistory.MOVIE_EDITION_SLOT_ID].Should().Be("42");
            MovieAcquisitionTargetSerializer.Read(data).Should().Be(MovieAcquisitionTarget.ForEditionSlot(42));
        }

        [Test]
        public void transition_to_main_or_unknown_should_clear_stale_slot_identity()
        {
            var data = new Dictionary<string, string>();
            MovieAcquisitionTargetSerializer.Write(data, MovieAcquisitionTarget.ForEditionSlot(42));
            MovieAcquisitionTargetSerializer.Write(data, MovieAcquisitionTarget.Main);
            data.Should().NotContainKey(MovieHistory.MOVIE_EDITION_SLOT_ID);
            MovieAcquisitionTargetSerializer.Read(data).Should().Be(MovieAcquisitionTarget.Main);

            MovieAcquisitionTargetSerializer.Write(data, MovieAcquisitionTarget.ForEditionSlot(42));
            MovieAcquisitionTargetSerializer.Write(data, MovieAcquisitionTarget.Unknown);
            data.Should().NotContainKey(MovieHistory.MOVIE_EDITION_SLOT_ID);
            MovieAcquisitionTargetSerializer.Read(data).Should().Be(MovieAcquisitionTarget.Unknown);
        }

        [Test]
        public void malformed_or_absent_explicit_history_target_should_fail_closed()
        {
            MovieAcquisitionTargetSerializer.Read(new Dictionary<string, string>()).Should().Be(MovieAcquisitionTarget.Unknown);
            MovieAcquisitionTargetSerializer.Read(new Dictionary<string, string>
            {
                [MovieHistory.ACQUISITION_TARGET] = "editionSlot",
                [MovieHistory.MOVIE_EDITION_SLOT_ID] = "not-an-id"
            }).Should().Be(MovieAcquisitionTarget.Unknown);
        }

        [TestCase("main", "42")]
        [TestCase("unknown", "42")]
        [TestCase("unexpected", "42")]
        [TestCase("editionSlot", null)]
        [TestCase("editionSlot", "not-an-id")]
        public void contradictory_or_incomplete_explicit_history_target_should_fail_closed(string kind, string slotId)
        {
            var data = new Dictionary<string, string>
            {
                [MovieHistory.ACQUISITION_TARGET] = kind
            };

            if (slotId != null)
            {
                data[MovieHistory.MOVIE_EDITION_SLOT_ID] = slotId;
            }

            MovieAcquisitionTargetSerializer.Read(data).Should().Be(MovieAcquisitionTarget.Unknown);
            MovieAcquisitionTargetSerializer.ReadLegacyHistory(data).Should().Be(MovieAcquisitionTarget.Unknown);
        }

        [Test]
        public void isolated_legacy_history_parser_should_reconstruct_pre_feature_main_and_slots()
        {
            MovieAcquisitionTargetSerializer.ReadLegacyHistory(new Dictionary<string, string>()).Should().Be(MovieAcquisitionTarget.Main);
            MovieAcquisitionTargetSerializer.ReadLegacyHistory(new Dictionary<string, string>
            {
                [MovieHistory.MOVIE_EDITION_SLOT_ID] = "42"
            }).Should().Be(MovieAcquisitionTarget.ForEditionSlot(42));
        }

        [TestCase(MovieAcquisitionTargetKind.Main, null)]
        [TestCase(MovieAcquisitionTargetKind.EditionSlot, 42)]
        [TestCase(MovieAcquisitionTargetKind.Unknown, null)]
        public void movie_edition_search_command_should_round_trip_through_system_text_json(MovieAcquisitionTargetKind kind, int? slotId)
        {
            var target = kind == MovieAcquisitionTargetKind.Main
                ? MovieAcquisitionTarget.Main
                : kind == MovieAcquisitionTargetKind.EditionSlot
                    ? MovieAcquisitionTarget.ForEditionSlot(slotId.Value)
                    : MovieAcquisitionTarget.Unknown;
            var command = new MovieEditionSearchCommand { MovieId = 7, AcquisitionTarget = target };

            var roundTripped = JsonSerializer.Deserialize<MovieEditionSearchCommand>(JsonSerializer.Serialize(command));

            roundTripped.MovieId.Should().Be(7);
            roundTripped.AcquisitionTarget.Should().Be(target);
        }

        [Test]
        public void movie_edition_search_command_should_fail_closed_when_json_target_is_null()
        {
            var command = JsonSerializer.Deserialize<MovieEditionSearchCommand>("{\"movieId\":7,\"acquisitionTarget\":null}", new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            command.AcquisitionTarget.Should().Be(MovieAcquisitionTarget.Unknown);
            command.MovieEditionSlotId.Should().BeNull();
        }

        [Test]
        public void queue_compatibility_null_slot_should_fail_closed()
        {
            var queue = new NzbDrone.Core.Queue.Queue { AcquisitionTarget = MovieAcquisitionTarget.Main };

            queue.MovieEditionSlotId = null;

            queue.AcquisitionTarget.Should().Be(MovieAcquisitionTarget.Unknown);
        }

        [Test]
        public void local_movie_import_placement_should_keep_unassigned_and_unknown_distinct_and_clear_stale_slots()
        {
            var localMovie = new LocalMovie { MovieEditionSlotId = 42 };

            localMovie.ImportTarget = MovieFileImportTarget.Unassigned;
            localMovie.ImportTarget.Should().Be(MovieFileImportTarget.Unassigned);
            localMovie.AcquisitionTarget.Should().Be(MovieAcquisitionTarget.Unknown);
            localMovie.MovieEditionSlotId.Should().BeNull();

            localMovie.ImportTarget = MovieFileImportTarget.Unknown;
            localMovie.ImportTarget.Should().Be(MovieFileImportTarget.Unknown);
            localMovie.AcquisitionTarget.Should().Be(MovieAcquisitionTarget.Unknown);
        }

        [Test]
        public void local_movie_should_bridge_main_and_slot_acquisition_identity_to_import_placement()
        {
            var localMovie = new LocalMovie { AcquisitionTarget = MovieAcquisitionTarget.Main };
            localMovie.ImportTarget.Should().Be(MovieFileImportTarget.Main);

            localMovie.AcquisitionTarget = MovieAcquisitionTarget.ForEditionSlot(42);
            localMovie.ImportTarget.Should().Be(MovieFileImportTarget.EditionSlot);
            localMovie.MovieEditionSlotId.Should().Be(42);

            localMovie.AcquisitionTarget = MovieAcquisitionTarget.Unknown;
            localMovie.ImportTarget.Should().Be(MovieFileImportTarget.Unknown);
            localMovie.MovieEditionSlotId.Should().BeNull();
        }
    }
}
