import React, { useCallback, useState } from 'react';
import { useSelector } from 'react-redux';
import AppState from 'App/State/AppState';
import CheckInput from 'Components/Form/CheckInput';
import Label from 'Components/Label';
import IconButton from 'Components/Link/IconButton';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import MonitorToggleButton from 'Components/MonitorToggleButton';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds } from 'Helpers/Props';
import MovieEdition from 'Movie/Editions/MovieEdition';
import { useMovieEditionActions } from 'Movie/Editions/useMovieEditions';
import MovieQuality from 'Movie/MovieQuality';
import MovieInteractiveSearchModal from 'Movie/Search/MovieInteractiveSearchModal';
import { InputChanged } from 'typings/inputs';
import translate from 'Utilities/String/translate';
import EditMovieEditionModal from './EditMovieEditionModal';
import styles from './MovieEditionsTable.css';

interface MovieEditionRowProps {
  edition: MovieEdition;
  movieQualityProfileId: number;
  isSearching: boolean;
  onMonitorPress(edition: MovieEdition, monitored: boolean): void;
  onSearchPress(edition: MovieEdition): void;
  onDeletePress(edition: MovieEdition, deleteFiles: boolean): void;
}

function MovieEditionRow({
  edition,
  movieQualityProfileId,
  isSearching,
  onMonitorPress,
  onSearchPress,
  onDeletePress,
}: MovieEditionRowProps) {
  const { saveEdition } = useMovieEditionActions();
  const qualityProfiles = useSelector(
    (state: AppState) => state.settings.qualityProfiles.items
  );

  const [isEditModalOpen, setIsEditModalOpen] = useState(false);
  const [isSearchModalOpen, setIsSearchModalOpen] = useState(false);
  const [isDeleteModalOpen, setIsDeleteModalOpen] = useState(false);
  const [deleteFiles, setDeleteFiles] = useState(false);

  const profileId = edition.qualityProfileId || movieQualityProfileId;
  const profileName =
    qualityProfiles.find((p) => p.id === profileId)?.name ?? '';

  const handleMonitorPress = useCallback(
    (monitored: boolean) => {
      onMonitorPress(edition, monitored);
    },
    [edition, onMonitorPress]
  );

  const handleSearchPress = useCallback(() => {
    onSearchPress(edition);
  }, [edition, onSearchPress]);

  const handleDeleteConfirm = useCallback(() => {
    setIsDeleteModalOpen(false);
    onDeletePress(edition, deleteFiles);
  }, [edition, deleteFiles, onDeletePress]);

  const handleInteractiveSearchPress = useCallback(() => {
    setIsSearchModalOpen(true);
  }, []);

  const handleEditPress = useCallback(() => {
    setIsEditModalOpen(true);
  }, []);

  const handleDeletePress = useCallback(() => {
    setIsDeleteModalOpen(true);
  }, []);

  const handleEditModalClose = useCallback(() => {
    setIsEditModalOpen(false);
  }, []);

  const handleSearchModalClose = useCallback(() => {
    setIsSearchModalOpen(false);
  }, []);

  const handleDeleteModalClose = useCallback(() => {
    setIsDeleteModalOpen(false);
  }, []);

  const handleDeleteFilesChange = useCallback(({ value }: InputChanged) => {
    setDeleteFiles(value as boolean);
  }, []);

  return (
    <TableRow>
      <TableRowCell className={styles.monitored}>
        <MonitorToggleButton
          monitored={edition.monitored}
          onPress={handleMonitorPress}
        />
      </TableRowCell>

      <TableRowCell>
        {edition.title}

        {edition.isDefault ? (
          <span className={styles.defaultLabel}>
            <Label kind={kinds.INFO}>{translate('Default')}</Label>
          </span>
        ) : null}
      </TableRowCell>

      <TableRowCell>
        {profileName}
        {edition.qualityProfileId ? null : ` (${translate('Movie')})`}
      </TableRowCell>

      <TableRowCell>
        {edition.movieFile ? (
          <MovieQuality quality={edition.movieFile.quality} />
        ) : (
          <Label kind={edition.monitored ? kinds.DANGER : kinds.DEFAULT}>
            {translate('Missing')}
          </Label>
        )}
      </TableRowCell>

      <TableRowCell>
        {edition.fileCount}

        {edition.isDuplicate ? (
          <Label
            kind={kinds.WARNING}
            title={translate('EditionDuplicateHelpText')}
          >
            {translate('Duplicate')}
          </Label>
        ) : null}
      </TableRowCell>

      <TableRowCell className={styles.actions}>
        <SpinnerIconButton
          name={icons.SEARCH}
          title={translate('AutomaticSearch')}
          isSpinning={isSearching}
          onPress={handleSearchPress}
        />

        <IconButton
          name={icons.INTERACTIVE}
          title={translate('InteractiveSearch')}
          onPress={handleInteractiveSearchPress}
        />

        <IconButton
          name={icons.EDIT}
          title={translate('EditEdition')}
          onPress={handleEditPress}
        />

        {edition.isDefault ? null : (
          <IconButton
            name={icons.REMOVE}
            kind={kinds.DANGER}
            title={translate('DeleteEdition')}
            onPress={handleDeletePress}
          />
        )}
      </TableRowCell>

      <EditMovieEditionModal
        isOpen={isEditModalOpen}
        movieId={edition.movieId}
        edition={edition}
        onSave={saveEdition}
        onModalClose={handleEditModalClose}
      />

      <MovieInteractiveSearchModal
        isOpen={isSearchModalOpen}
        movieId={edition.movieId}
        movieEditionId={edition.id}
        onModalClose={handleSearchModalClose}
      />

      <ConfirmModal
        isOpen={isDeleteModalOpen}
        kind={kinds.DANGER}
        title={translate('DeleteEdition')}
        message={
          <div>
            <div>
              {translate('DeleteEditionMessageText', {
                title: edition.title,
              })}
            </div>

            {edition.fileCount ? (
              <CheckInput
                name="deleteFiles"
                value={deleteFiles}
                helpText={translate('DeleteEditionFilesHelpText', {
                  count: edition.fileCount,
                })}
                kind={kinds.DANGER}
                onChange={handleDeleteFilesChange}
              />
            ) : null}
          </div>
        }
        confirmLabel={translate('Delete')}
        onConfirm={handleDeleteConfirm}
        onCancel={handleDeleteModalClose}
      />
    </TableRow>
  );
}

export default MovieEditionRow;
