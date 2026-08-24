using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieEditionSlots
{
    [TestFixture]
    public class MovieEditionSlotMutationStoreFixture : DbTest<MovieEditionSlotMutationStore, MovieEditionSlot>
    {
        private const int MovieId = 42;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IMovieEditionSlotMutationFaultInjector>()
                .Setup(x => x.Check(It.IsAny<MovieEditionSlotMutationStep>()));
        }

        [Test]
        public void add_should_atomically_persist_slot_aliases_and_precedence_collapsed_identities()
        {
            var slot = Subject.Add(NewSlot("Director's Cut", "Directors.Cut", " directors cut ", "DC"));

            slot.Id.Should().BePositive();
            Db.All<MovieEditionSlotAlias>().Select(x => (x.Alias, x.NormalizedAlias)).Should().Equal(
                ("directors cut", "directorscut"),
                ("DC", "dc"));
            Db.All<MovieEditionIdentity>().OrderBy(x => x.NormalizedTerm).Select(x => (x.NormalizedTerm, x.IdentityType, x.DisplayValue)).Should().Equal(
                ("dc", MovieEditionIdentityType.Alias, "DC"),
                ("directorscut", MovieEditionIdentityType.CanonicalName, "Director's Cut"));
        }

        [Test]
        public void update_should_atomically_replace_aliases_and_identities_without_changing_persisted_lifecycle_fields()
        {
            var added = Subject.Add(NewSlot("IMAX", "Directors.Cut", "IMAX Enhanced"));
            var dateAdded = added.DateAdded;
            var lastSearch = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            added.LastSearchTime = lastSearch;
            Db.Update(added);

            var update = NewSlot("Extended", "EXT", "Long Cut");
            update.Id = added.Id;
            update.DateAdded = dateAdded.AddDays(5);
            update.LastSearchTime = null;
            update.Monitored = false;
            var result = Subject.Update(update);

            result.MovieId.Should().Be(MovieId);
            result.DateAdded.Should().Be(dateAdded);
            result.LastSearchTime.Should().Be(lastSearch);
            result.Monitored.Should().BeFalse();
            Db.All<MovieEditionSlotAlias>().Should().ContainSingle(x => x.Alias == "Long Cut");
            Db.All<MovieEditionIdentity>().Select(x => x.NormalizedTerm).Should().BeEquivalentTo("extended", "ext", "longcut");
        }

        [Test]
        public void duplicate_identity_should_be_validation_failure_without_partial_rows()
        {
            var existing = Subject.Add(NewSlot("IMAX"));

            var error = Assert.Throws<ValidationException>(() => Subject.Add(NewSlot("Extended", null, "I-MAX")));

            error.Errors.Should().ContainSingle(x => x.PropertyName == "EditionIdentity");
            Db.All<MovieEditionSlot>().Should().ContainSingle(x => x.Id == existing.Id);
            Db.All<MovieEditionSlotAlias>().Should().BeEmpty();
            Db.All<MovieEditionIdentity>().Should().ContainSingle(x => x.MovieEditionSlotId == existing.Id);
        }

        [Test]
        public void update_identity_collision_should_roll_back_and_preserve_old_complete_state()
        {
            var first = Subject.Add(NewSlot("IMAX", "IMAX Search", "IMAX Alias"));
            Subject.Add(NewSlot("Extended"));
            var update = NewSlot("Extended Alternate", null, "IMAX");
            update.Id = Db.All<MovieEditionSlot>().Single(x => x.EditionName == "Extended").Id;

            var error = Assert.Throws<ValidationException>(() => Subject.Update(update));

            error.Errors.Should().ContainSingle(x => x.PropertyName == "EditionIdentity");
            Db.All<MovieEditionSlot>().Single(x => x.Id == update.Id).EditionName.Should().Be("Extended");
            Db.All<MovieEditionIdentity>().Where(x => x.MovieEditionSlotId == update.Id).Should().ContainSingle(x => x.NormalizedTerm == "extended");
            Db.All<MovieEditionIdentity>().Where(x => x.MovieEditionSlotId == first.Id).Should().HaveCount(3);
        }

        [TestCase(MovieEditionSlotMutationStep.AfterSlotWrite)]
        [TestCase(MovieEditionSlotMutationStep.DuringAliases)]
        [TestCase(MovieEditionSlotMutationStep.DuringIdentities)]
        public void add_failure_at_any_mutation_step_should_roll_back_everything(MovieEditionSlotMutationStep step)
        {
            Mocker.GetMock<IMovieEditionSlotMutationFaultInjector>()
                .Setup(x => x.Check(step))
                .Throws(new InjectedMutationException());

            Assert.Throws<InjectedMutationException>(() => Subject.Add(NewSlot("IMAX", "Search", "Alias")));

            Db.All<MovieEditionSlot>().Should().BeEmpty();
            Db.All<MovieEditionSlotAlias>().Should().BeEmpty();
            Db.All<MovieEditionIdentity>().Should().BeEmpty();
        }

        [Test]
        public void failed_update_should_preserve_old_complete_state()
        {
            var existing = Subject.Add(NewSlot("IMAX", "Search", "Alias"));
            Mocker.GetMock<IMovieEditionSlotMutationFaultInjector>()
                .Setup(x => x.Check(MovieEditionSlotMutationStep.DuringIdentities))
                .Throws(new InjectedMutationException());

            var update = NewSlot("Extended", "New Search", "New Alias");
            update.Id = existing.Id;
            Assert.Throws<InjectedMutationException>(() => Subject.Update(update));

            Db.Single<MovieEditionSlot>().EditionName.Should().Be("IMAX");
            Db.Single<MovieEditionSlotAlias>().Alias.Should().Be("Alias");
            Db.All<MovieEditionIdentity>().Select(x => x.NormalizedTerm).Should().BeEquivalentTo("imax", "search", "alias");
        }

        [Test]
        public void assignment_after_slot_deletion_should_fail_without_leaving_a_dangling_reference()
        {
            var slot = Subject.Add(NewSlot("IMAX"));
            var file = Db.Insert(new MovieFile
            {
                MovieId = MovieId,
                Quality = new QualityModel(),
                Languages = new List<Language> { Language.English }
            });

            Subject.Delete(slot.Id);
            Assert.Throws<InvalidOperationException>(() => Subject.AssignFile(MovieId, file.Id, slot.Id, null));

            Db.All<MovieFile>().Single(x => x.Id == file.Id).MovieEditionSlotId.Should().BeNull();
        }

        [Test]
        public void deletion_after_transactional_assignment_should_fail_and_preserve_both_sides()
        {
            var slot = Subject.Add(NewSlot("IMAX"));
            var file = Db.Insert(new MovieFile
            {
                MovieId = MovieId,
                Quality = new QualityModel(),
                Languages = new List<Language> { Language.English }
            });

            Subject.AssignFile(MovieId, file.Id, slot.Id, null);
            Assert.Throws<InvalidOperationException>(() => Subject.Delete(slot.Id));

            Db.All<MovieEditionSlot>().Should().ContainSingle(x => x.Id == slot.Id);
            Db.All<MovieFile>().Single(x => x.Id == file.Id).MovieEditionSlotId.Should().Be(slot.Id);
        }

        [Test]
        public void equivalent_alias_sets_should_choose_the_same_identity_display_regardless_of_request_order()
        {
            var first = Subject.Add(NewSlot("First", null, "I-MAX", "IMAX"));
            var secondSlot = NewSlot("Second", null, "IMAX", "I-MAX");
            secondSlot.MovieId = MovieId + 1;
            var second = Subject.Add(secondSlot);

            Db.All<MovieEditionIdentity>().Single(x => x.MovieEditionSlotId == first.Id && x.IdentityType == MovieEditionIdentityType.Alias).DisplayValue.Should().Be("I-MAX");
            Db.All<MovieEditionIdentity>().Single(x => x.MovieEditionSlotId == second.Id && x.IdentityType == MovieEditionIdentityType.Alias).DisplayValue.Should().Be("I-MAX");
        }

        [Test]
        public void delete_should_authoritatively_reject_an_attached_file_and_roll_back_related_rows()
        {
            var existing = Subject.Add(NewSlot("IMAX", "Search", "Alias"));
            Db.Insert(new MovieFile
            {
                MovieId = MovieId,
                MovieEditionSlotId = existing.Id,
                Quality = new QualityModel(),
                Languages = new List<Language> { Language.English }
            });

            Assert.Throws<InvalidOperationException>(() => Subject.Delete(existing.Id));

            Db.All<MovieEditionSlot>().Should().ContainSingle(x => x.Id == existing.Id);
            Db.All<MovieEditionSlotAlias>().Should().ContainSingle(x => x.MovieEditionSlotId == existing.Id);
            Db.All<MovieEditionIdentity>().Should().HaveCount(3);
            Db.All<MovieFile>().Should().ContainSingle(x => x.MovieEditionSlotId == existing.Id);
        }

        [Test]
        public void delete_should_remove_slot_aliases_and_identities_and_failure_should_roll_back()
        {
            var existing = Subject.Add(NewSlot("IMAX", "Search", "Alias"));
            Mocker.GetMock<IMovieEditionSlotMutationFaultInjector>()
                .Setup(x => x.Check(MovieEditionSlotMutationStep.AfterDelete))
                .Throws(new InjectedMutationException());

            Assert.Throws<InjectedMutationException>(() => Subject.Delete(existing.Id));
            Db.All<MovieEditionSlot>().Should().ContainSingle();
            Db.All<MovieEditionSlotAlias>().Should().ContainSingle();
            Db.All<MovieEditionIdentity>().Should().HaveCount(3);

            Mocker.GetMock<IMovieEditionSlotMutationFaultInjector>().Reset();
            Subject.Delete(existing.Id);
            Db.All<MovieEditionSlot>().Should().BeEmpty();
            Db.All<MovieEditionSlotAlias>().Should().BeEmpty();
            Db.All<MovieEditionIdentity>().Should().BeEmpty();
        }

        private static MovieEditionSlot NewSlot(string editionName, string searchTerm = null, params string[] aliases)
        {
            return new MovieEditionSlot
            {
                MovieId = MovieId,
                EditionName = editionName,
                SearchTerm = searchTerm,
                Aliases = aliases?.ToList() ?? new List<string>(),
                Monitored = true,
                DateAdded = DateTime.UtcNow
            };
        }

        private sealed class InjectedMutationException : Exception
        {
        }
    }
}
