using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieEditionSlots
{
    [TestFixture]
    public class MovieFileAssignmentServiceFixture : CoreTest<MovieFileAssignmentService>
    {
        private const int MovieId = 10;
        private const int SlotId = 20;
        private Movie _movie;
        private MovieEditionSlot _slot;
        private MovieFile _file;
        private List<MovieFile> _files;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = MovieId };
            _slot = new MovieEditionSlot { Id = SlotId, MovieId = MovieId, EditionName = "IMAX" };
            _file = new MovieFile { Id = 30, MovieId = MovieId };
            _files = new List<MovieFile> { _file };
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(MovieId)).Returns(_movie);
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(SlotId)).Returns(_slot);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetMovie(_file.Id)).Returns(_file);
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.FindByEditionSlotId(SlotId))
                .Returns(() => _files.SingleOrDefault(file => file.MovieEditionSlotId == SlotId));
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetUnassignedFiles(MovieId))
                .Returns(() => _files.Where(file => !file.MovieEditionSlotId.HasValue && file.Id != _movie.MovieFileId).ToList());
        }

        [Test]
        public void assign_should_write_the_durable_file_side_assignment()
        {
            Subject.AssignFileToEdition(MovieId, _file.Id, SlotId);
            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(s => s.AssignFile(MovieId, _file.Id, SlotId, null), Times.Once);
            Assert.That(_file.MovieEditionSlotId, Is.EqualTo(SlotId));
        }

        [Test]
        public void assign_should_be_idempotent_for_the_same_file_and_slot()
        {
            _file.MovieEditionSlotId = SlotId;
            Assert.DoesNotThrow(() => Subject.AssignFileToEdition(MovieId, _file.Id, SlotId));
            VerifyNoWrites();
        }

        [Test]
        public void assign_should_reject_the_current_main_file()
        {
            _movie.MovieFileId = _file.Id;
            Assert.Throws<InvalidOperationException>(() => Subject.AssignFileToEdition(MovieId, _file.Id, SlotId));
            VerifyNoWrites();
        }

        [Test]
        public void assign_should_reject_a_file_attached_to_another_slot()
        {
            _file.MovieEditionSlotId = SlotId + 1;
            Assert.Throws<InvalidOperationException>(() => Subject.AssignFileToEdition(MovieId, _file.Id, SlotId));
            VerifyNoWrites();
        }

        [Test]
        public void assign_should_reject_an_occupied_slot()
        {
            _files.Add(new MovieFile { Id = 31, MovieId = MovieId, MovieEditionSlotId = SlotId });
            Assert.Throws<InvalidOperationException>(() => Subject.AssignFileToEdition(MovieId, _file.Id, SlotId));
            VerifyNoWrites();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void assign_should_reject_cross_movie_ownership_before_writing(bool wrongFile)
        {
            if (wrongFile) _file.MovieId = MovieId + 1;
            else _slot.MovieId = MovieId + 1;
            Assert.Throws<InvalidOperationException>(() => Subject.AssignFileToEdition(MovieId, _file.Id, SlotId));
            VerifyNoWrites();
        }

        [Test]
        public void make_main_reject_should_not_partially_mutate_when_another_main_exists()
        {
            var previousMain = SetPreviousMain();
            _file.MovieEditionSlotId = SlotId;
            Assert.Throws<InvalidOperationException>(() => Subject.MakeFileMain(MovieId, _file.Id, ExistingMainFileAction.Reject));
            Assert.That(_file.MovieEditionSlotId, Is.EqualTo(SlotId));
            Assert.That(_movie.MovieFileId, Is.EqualTo(previousMain.Id));
            VerifyNoWrites();
        }

        [Test]
        public void make_main_keep_unassigned_should_preserve_old_main_and_promote_target()
        {
            var previousMain = SetPreviousMain();
            _file.MovieEditionSlotId = SlotId;
            Subject.MakeFileMain(MovieId, _file.Id, ExistingMainFileAction.KeepUnassigned);
            Assert.That(previousMain.MovieEditionSlotId, Is.Null);
            Assert.That(_file.MovieEditionSlotId, Is.Null);
            Assert.That(_movie.MovieFileId, Is.EqualTo(_file.Id));
            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(It.IsAny<Movie>(), It.IsAny<MovieFile>()), Times.Never);
            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(s => s.UnassignFile(_file.Id, SlotId), Times.Once);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(previousMain), Times.Never);
            Mocker.GetMock<IMovieService>().Verify(s => s.UpdateMovie(_movie), Times.Once);
        }

        [Test]
        public void make_main_delete_should_delete_exact_old_main_after_preflight()
        {
            var previousMain = SetPreviousMain();
            _file.MovieEditionSlotId = SlotId;
            Subject.MakeFileMain(MovieId, _file.Id, ExistingMainFileAction.DeleteRecycle);
            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(_movie, previousMain), Times.Once);
            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(_movie, _file), Times.Never);
            Assert.That(_file.MovieEditionSlotId, Is.Null);
            Assert.That(_movie.MovieFileId, Is.EqualTo(_file.Id));
        }

        [Test]
        public void make_main_should_reject_cross_movie_target_without_deleting_old_main()
        {
            SetPreviousMain();
            _file.MovieId = MovieId + 1;
            Assert.Throws<InvalidOperationException>(() => Subject.MakeFileMain(MovieId, _file.Id, ExistingMainFileAction.DeleteRecycle));
            VerifyNoWrites();
        }

        [Test]
        public void make_main_should_reject_target_attached_to_cross_movie_slot_without_mutation()
        {
            _file.MovieEditionSlotId = SlotId;
            _slot.MovieId = MovieId + 1;
            Assert.Throws<InvalidOperationException>(() => Subject.MakeFileMain(MovieId, _file.Id, ExistingMainFileAction.KeepUnassigned));
            Assert.That(_file.MovieEditionSlotId, Is.EqualTo(SlotId));
            VerifyNoWrites();
        }

        [Test]
        public void remove_cancel_should_not_even_load_or_mutate_lifecycle_state()
        {
            Subject.RemoveEdition(SlotId, AttachedEditionFileAction.Cancel);
            Mocker.GetMock<IMovieEditionSlotService>().Verify(s => s.GetById(It.IsAny<int>()), Times.Never);
            VerifyNoWrites();
        }

        [Test]
        public void remove_keep_unassigned_should_detach_before_deleting_slot()
        {
            _file.MovieEditionSlotId = SlotId;
            var detached = false;
            Mocker.GetMock<IMovieEditionSlotMutationStore>().Setup(s => s.UnassignFile(_file.Id, SlotId)).Callback(() => detached = true);
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.Delete(SlotId)).Callback(() => Assert.That(detached, Is.True));
            Subject.RemoveEdition(SlotId, AttachedEditionFileAction.KeepUnassigned);
            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(s => s.UnassignFile(_file.Id, SlotId), Times.Once);
            Mocker.GetMock<IMovieEditionSlotService>().Verify(s => s.Delete(SlotId), Times.Once);
            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(It.IsAny<Movie>(), It.IsAny<MovieFile>()), Times.Never);
        }

        [Test]
        public void remove_delete_should_delete_exact_edition_file_after_validation()
        {
            _file.MovieEditionSlotId = SlotId;
            Subject.RemoveEdition(SlotId, AttachedEditionFileAction.DeleteRecycle);
            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(_movie, _file), Times.Once);
            Mocker.GetMock<IMovieEditionSlotService>().Verify(s => s.Delete(SlotId), Times.Once);
        }

        [Test]
        public void remove_delete_should_reject_cross_movie_file_before_deleting_anything()
        {
            _file.MovieEditionSlotId = SlotId;
            _file.MovieId = MovieId + 1;
            Assert.Throws<InvalidOperationException>(() => Subject.RemoveEdition(SlotId, AttachedEditionFileAction.DeleteRecycle));
            VerifyNoWrites();
        }

        [Test]
        public void convert_to_main_should_require_explicit_previous_main_action()
        {
            _file.MovieEditionSlotId = SlotId;
            Assert.Throws<InvalidOperationException>(() => Subject.RemoveEdition(SlotId, AttachedEditionFileAction.ConvertToMain));
            Assert.That(_file.MovieEditionSlotId, Is.EqualTo(SlotId));
            VerifyNoWrites();
        }

        [Test]
        public void convert_to_main_reject_should_not_partially_mutate()
        {
            var previousMain = SetPreviousMain();
            _file.MovieEditionSlotId = SlotId;
            Assert.Throws<InvalidOperationException>(() => Subject.RemoveEdition(SlotId, AttachedEditionFileAction.ConvertToMain, ExistingMainFileAction.Reject));
            Assert.That(_file.MovieEditionSlotId, Is.EqualTo(SlotId));
            Assert.That(_movie.MovieFileId, Is.EqualTo(previousMain.Id));
            VerifyNoWrites();
        }

        [Test]
        public void convert_to_main_keep_unassigned_should_promote_and_then_delete_slot()
        {
            var previousMain = SetPreviousMain();
            _file.MovieEditionSlotId = SlotId;
            Subject.RemoveEdition(SlotId, AttachedEditionFileAction.ConvertToMain, ExistingMainFileAction.KeepUnassigned);
            Assert.That(previousMain.MovieEditionSlotId, Is.Null);
            Assert.That(_file.MovieEditionSlotId, Is.Null);
            Assert.That(_movie.MovieFileId, Is.EqualTo(_file.Id));
            Mocker.GetMock<IMovieEditionSlotService>().Verify(s => s.Delete(SlotId), Times.Once);
            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(It.IsAny<Movie>(), It.IsAny<MovieFile>()), Times.Never);
        }

        [Test]
        public void convert_to_main_should_reject_missing_attached_file_without_deleting_slot()
        {
            _files.Clear();
            Assert.Throws<InvalidOperationException>(() => Subject.RemoveEdition(SlotId, AttachedEditionFileAction.ConvertToMain, ExistingMainFileAction.KeepUnassigned));
            VerifyNoWrites();
        }

        [Test]
        public void remove_missing_attached_file_should_delete_only_the_slot()
        {
            _files.Clear();
            Subject.RemoveEdition(SlotId, AttachedEditionFileAction.KeepUnassigned);
            Mocker.GetMock<IMovieEditionSlotService>().Verify(s => s.Delete(SlotId), Times.Once);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(It.IsAny<MovieFile>()), Times.Never);
            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(s => s.AssignFile(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>()), Times.Never);
            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(s => s.UnassignFile(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(It.IsAny<Movie>(), It.IsAny<MovieFile>()), Times.Never);
        }

        [Test]
        public void getters_should_use_only_durable_slot_assignment_and_exclude_main_from_unassigned()
        {
            var parserOnly = new MovieFile { Id = 31, MovieId = MovieId, Edition = "IMAX" };
            var durable = new MovieFile { Id = 32, MovieId = MovieId, Edition = "Other", MovieEditionSlotId = SlotId };
            var main = new MovieFile { Id = 33, MovieId = MovieId };
            _movie.MovieFileId = main.Id;
            _files = new List<MovieFile> { parserOnly, durable, main };
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.FindByEditionSlotId(SlotId))
                .Returns(() => _files.SingleOrDefault(file => file.MovieEditionSlotId == SlotId));
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetUnassignedFiles(MovieId))
                .Returns(() => _files.Where(file => !file.MovieEditionSlotId.HasValue && file.Id != _movie.MovieFileId).ToList());
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetMovie(main.Id)).Returns(main);
            Assert.That(Subject.GetEditionFile(SlotId), Is.SameAs(durable));
            Assert.That(Subject.GetMainFile(MovieId), Is.SameAs(main));
            Assert.That(Subject.GetUnassignedFiles(MovieId), Is.EqualTo(new[] { parserOnly }));
        }

        private MovieFile SetPreviousMain()
        {
            var previousMain = new MovieFile { Id = 40, MovieId = MovieId };
            _movie.MovieFileId = previousMain.Id;
            _files.Add(previousMain);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetMovie(previousMain.Id)).Returns(previousMain);
            return previousMain;
        }

        private void VerifyNoWrites()
        {
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(It.IsAny<MovieFile>()), Times.Never);
            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(s => s.AssignFile(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>()), Times.Never);
            Mocker.GetMock<IMovieEditionSlotMutationStore>().Verify(s => s.UnassignFile(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
            Mocker.GetMock<IMovieService>().Verify(s => s.UpdateMovie(It.IsAny<Movie>()), Times.Never);
            Mocker.GetMock<IMovieEditionSlotService>().Verify(s => s.Delete(It.IsAny<int>()), Times.Never);
            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(It.IsAny<Movie>(), It.IsAny<MovieFile>()), Times.Never);
        }
    }
}
