import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import { createSelector } from 'reselect';
import AppState from 'App/State/AppState';
import { QualityProfilesAppState } from 'App/State/SettingsAppState';
import * as commandNames from 'Commands/commandNames';
import FieldSet from 'Components/FieldSet';
import EnhancedSelectInput, {
  EnhancedSelectInputValue,
} from 'Components/Form/Select/EnhancedSelectInput';
import Icon from 'Components/Icon';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import MonitorToggleButton from 'Components/MonitorToggleButton';
import RelativeDateCell from 'Components/Table/Cells/RelativeDateCell';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds } from 'Helpers/Props';
import MovieFormats from 'Movie/MovieFormats';
import MovieQuality from 'Movie/MovieQuality';
import MovieEditionSlotInteractiveSearchModal from 'Movie/Search/MovieEditionSlotInteractiveSearchModal';
import { MovieFile } from 'MovieFile/MovieFile';
import { QualityModel } from 'Quality/Quality';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchQualityProfiles } from 'Store/Actions/settingsActions';
import createCommandsSelector from 'Store/Selectors/createCommandsSelector';
import createSortedSectionSelector from 'Store/Selectors/createSortedSectionSelector';
import CustomFormat from 'typings/CustomFormat';
import { EnhancedSelectInputChanged } from 'typings/inputs';
import QualityProfile from 'typings/QualityProfile';
import sortByProp from 'Utilities/Array/sortByProp';
import { findCommand, isCommandExecuting } from 'Utilities/Command';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import formatCustomFormatScore from 'Utilities/Number/formatCustomFormatScore';
import translate from 'Utilities/String/translate';
import styles from './MovieEditionSlotsTable.css';

const SLOT_DEFAULT_PROFILE_KEY = 'slot-default' as const;

type SlotQualityProfileValue = number | typeof SLOT_DEFAULT_PROFILE_KEY;

const selectQualityProfileItems = createSelector(
  createSortedSectionSelector<QualityProfile, QualityProfilesAppState>(
    'settings.qualityProfiles',
    sortByProp<QualityProfile, 'name'>('name')
  ),
  (section: QualityProfilesAppState) => section.items
);

interface SlotQualityProfileSelectProps {
  value: number | null;
  onChange: (value: number | null) => void;
}

function SlotQualityProfileSelect({
  value,
  onChange,
}: SlotQualityProfileSelectProps) {
  const profiles = useSelector(selectQualityProfileItems);

  const options = useMemo<EnhancedSelectInputValue<SlotQualityProfileValue>[]>(
    () => [
      {
        key: SLOT_DEFAULT_PROFILE_KEY,
        get value() {
          return translate('Default');
        },
      },
      ...profiles.map((p) => ({ key: p.id, value: p.name })),
    ],
    [profiles]
  );

  const handleChange = useCallback(
    ({
      value: newValue,
    }: EnhancedSelectInputChanged<SlotQualityProfileValue>) => {
      onChange(
        newValue === SLOT_DEFAULT_PROFILE_KEY ? null : (newValue as number)
      );
    },
    [onChange]
  );

  return (
    <EnhancedSelectInput
      name="qualityProfileId"
      value={value ?? SLOT_DEFAULT_PROFILE_KEY}
      values={options}
      onChange={handleChange}
    />
  );
}

interface MovieEditionSlot {
  id: number;
  movieId: number;
  editionName: string;
  searchTerm: string | null;
  aliases: string[];
  monitored: boolean;
  movieFileId: number | null;
  qualityProfileId: number | null;
  minimumCustomFormatScore: number | null;
  effectiveQualityProfileId: number;
  effectiveQualityProfileName: string;
  qualityProfileInherited: boolean;
  effectiveMinimumCustomFormatScore: number;
  minimumCustomFormatScoreInherited: boolean;
  status: 'assigned' | 'missing' | 'unmonitored';
  movieFile: MovieEditionSlotFile | null;
  movieFileQuality: QualityModel | null;
  movieFileCustomFormatScore: number | null;
  movieFileCustomFormats: CustomFormat[] | null;
  lastSearchTime: string | null;
  dateAdded: string;
  isSaving?: boolean;
  isDeleting?: boolean;
  isAssigning?: boolean;
  isConverting?: boolean;
}

interface MovieEditionSlotFile {
  id: number;
  relativePath: string;
  size: number;
  quality: QualityModel;
  customFormats: CustomFormat[];
  customFormatScore: number;
  dateAdded: string;
}

type EditionFileAction = 'keepUnassigned' | 'deleteRecycle';
type ExistingMainFileAction = 'keepUnassigned' | 'deleteRecycle' | 'reject';

interface PendingDeleteSlot {
  slot: MovieEditionSlot;
  attachedFileAction: EditionFileAction;
}

interface PendingConvertSlot {
  slot: MovieEditionSlot;
  existingMainFileAction: ExistingMainFileAction;
}

interface PendingAssignSlot {
  slot: MovieEditionSlot;
  movieFileId: number | null;
}

interface MovieEditionSlotRowProps {
  slot: MovieEditionSlot;
  isSearching: boolean;
  onMonitorToggle: (slot: MovieEditionSlot, monitored: boolean) => void;
  onSearchPress: (slotId: number) => void;
  onInteractiveSearchPress: (slot: MovieEditionSlot) => void;
  onAssignPress: (slot: MovieEditionSlot) => void;
  onUnassignPress: (slot: MovieEditionSlot) => void;
  onConvertToMainPress: (slot: MovieEditionSlot) => void;
  onSave: (
    slot: MovieEditionSlot,
    editionName: string,
    searchTerm: string,
    qualityProfileId: number | null,
    minimumCustomFormatScore: number | null
  ) => void;
  onDeletePress: (slot: MovieEditionSlot) => void;
}

function MovieEditionSlotRow(props: MovieEditionSlotRowProps) {
  const {
    slot,
    isSearching,
    onMonitorToggle,
    onSearchPress,
    onInteractiveSearchPress,
    onAssignPress,
    onUnassignPress,
    onConvertToMainPress,
    onSave,
    onDeletePress,
  } = props;

  const [editionName, setEditionName] = useState(slot.editionName);
  const [searchTerm, setSearchTerm] = useState(slot.searchTerm ?? '');
  const [qualityProfileId, setQualityProfileId] = useState<number | null>(
    slot.qualityProfileId
  );
  const [minimumCustomFormatScore, setMinimumCustomFormatScore] = useState(
    slot.minimumCustomFormatScore?.toString() ?? ''
  );

  useEffect(() => {
    setEditionName(slot.editionName);
    setSearchTerm(slot.searchTerm ?? '');
    setQualityProfileId(slot.qualityProfileId);
    setMinimumCustomFormatScore(
      slot.minimumCustomFormatScore?.toString() ?? ''
    );
  }, [
    slot.editionName,
    slot.searchTerm,
    slot.qualityProfileId,
    slot.minimumCustomFormatScore,
  ]);

  const handleMonitorTogglePress = useCallback(
    (monitored: boolean) => {
      onMonitorToggle(slot, monitored);
    },
    [slot, onMonitorToggle]
  );

  const handleSearchPress = useCallback(() => {
    onSearchPress(slot.id);
  }, [slot.id, onSearchPress]);

  const handleInteractiveSearchPress = useCallback(() => {
    onInteractiveSearchPress(slot);
  }, [slot, onInteractiveSearchPress]);

  const handleSavePress = useCallback(() => {
    const rawScore = minimumCustomFormatScore.trim();
    const parsedScore = rawScore ? parseInt(rawScore) : null;

    onSave(
      slot,
      editionName,
      searchTerm,
      qualityProfileId,
      Number.isNaN(parsedScore) ? null : parsedScore
    );
  }, [
    slot,
    editionName,
    searchTerm,
    qualityProfileId,
    minimumCustomFormatScore,
    onSave,
  ]);

  const handleEditionNameChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      setEditionName(event.target.value);
    },
    []
  );

  const handleSearchTermChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      setSearchTerm(event.target.value);
    },
    []
  );

  const handleQualityProfileIdChange = useCallback((value: number | null) => {
    setQualityProfileId(value);
  }, []);

  const handleMinimumCustomFormatScoreChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      setMinimumCustomFormatScore(event.target.value);
    },
    []
  );

  const handleAssignPress = useCallback(() => {
    onAssignPress(slot);
  }, [slot, onAssignPress]);

  const handleUnassignPress = useCallback(() => {
    onUnassignPress(slot);
  }, [slot, onUnassignPress]);

  const handleConvertToMainPress = useCallback(() => {
    onConvertToMainPress(slot);
  }, [slot, onConvertToMainPress]);

  const handleDeletePress = useCallback(() => {
    onDeletePress(slot);
  }, [slot, onDeletePress]);

  const movieFile = slot.movieFile;
  const movieFileQuality = movieFile?.quality ?? slot.movieFileQuality;
  const movieFileCustomFormats =
    movieFile?.customFormats ?? slot.movieFileCustomFormats;
  const movieFileCustomFormatScore =
    movieFile?.customFormatScore ?? slot.movieFileCustomFormatScore;

  let status = (
    <span className={styles.statusUnmonitored}>
      <Icon name={icons.UNMONITORED} title={translate('Unmonitored')} />{' '}
      {translate('Unmonitored')}
    </span>
  );

  if (slot.status === 'assigned' || movieFile) {
    status = (
      <span className={styles.statusHasFile}>
        <Icon
          name={icons.CHECK}
          kind={kinds.SUCCESS}
          title={translate('Downloaded')}
        />{' '}
        {translate('Downloaded')}
      </span>
    );
  } else if (slot.status === 'missing' || slot.monitored) {
    status = (
      <span className={styles.statusMissing}>
        <Icon
          name={icons.MISSING}
          kind={kinds.DANGER}
          title={translate('Missing')}
        />{' '}
        {translate('Missing')}
      </span>
    );
  }

  return (
    <TableRow>
      <TableRowCell className={styles.monitorCell}>
        <MonitorToggleButton
          monitored={slot.monitored}
          isSaving={slot.isSaving}
          onPress={handleMonitorTogglePress}
        />
      </TableRowCell>

      <TableRowCell>
        <input
          className={styles.editInput}
          type="text"
          value={editionName}
          onChange={handleEditionNameChange}
        />
      </TableRowCell>

      <TableRowCell>
        <input
          className={styles.editInput}
          type="text"
          value={searchTerm}
          placeholder="-"
          onChange={handleSearchTermChange}
        />
      </TableRowCell>

      <TableRowCell>{status}</TableRowCell>

      <TableRowCell className={styles.fileCell}>
        {movieFile ? (
          <span title={movieFile.relativePath}>{movieFile.relativePath}</span>
        ) : (
          '-'
        )}
      </TableRowCell>

      <TableRowCell>
        {movieFileQuality ? (
          <MovieQuality quality={movieFileQuality} isCutoffNotMet={false} />
        ) : (
          '-'
        )}
      </TableRowCell>

      <TableRowCell>
        {movieFileCustomFormats?.length ? (
          <MovieFormats formats={movieFileCustomFormats} />
        ) : (
          '-'
        )}
      </TableRowCell>

      <TableRowCell className={styles.customFormatScoreCell}>
        {movieFileCustomFormatScore == null
          ? '-'
          : formatCustomFormatScore(
              movieFileCustomFormatScore,
              movieFileCustomFormats?.length ?? 0
            )}
      </TableRowCell>

      <TableRowCell>
        <div className={styles.profileCell}>
          <SlotQualityProfileSelect
            value={qualityProfileId}
            onChange={handleQualityProfileIdChange}
          />
          {slot.qualityProfileInherited ? (
            <div className={styles.inheritedValue}>
              {slot.effectiveQualityProfileName}
            </div>
          ) : null}
        </div>
      </TableRowCell>

      <TableRowCell>
        <div className={styles.scoreCell}>
          <input
            className={styles.smallEditInput}
            type="number"
            value={minimumCustomFormatScore}
            placeholder={translate('Default')}
            onChange={handleMinimumCustomFormatScoreChange}
          />
          {slot.minimumCustomFormatScoreInherited ? (
            <div className={styles.inheritedValue}>
              {slot.effectiveMinimumCustomFormatScore}
            </div>
          ) : null}
        </div>
      </TableRowCell>

      <RelativeDateCell
        date={slot.lastSearchTime ?? undefined}
        includeTime={true}
      />

      <TableRowCell className={styles.actionsCell}>
        <SpinnerIconButton
          className={styles.actionButton}
          name={icons.SAVE}
          title={translate('Save')}
          isSpinning={!!slot.isSaving}
          onPress={handleSavePress}
        />

        <SpinnerIconButton
          className={styles.actionButton}
          name={icons.SEARCH}
          title={translate('SearchEdition')}
          isSpinning={isSearching}
          onPress={handleSearchPress}
        />

        <SpinnerIconButton
          className={styles.actionButton}
          name={icons.INTERACTIVE}
          title={translate('InteractiveSearch')}
          isSpinning={false}
          onPress={handleInteractiveSearchPress}
        />

        {movieFile ? (
          <>
            <SpinnerIconButton
              className={styles.actionButton}
              name={icons.UNMONITORED}
              title={translate('UnassignEditionFile')}
              isSpinning={!!slot.isAssigning}
              onPress={handleUnassignPress}
            />

            <SpinnerIconButton
              className={styles.actionButton}
              name={icons.MOVIE_FILE}
              title={translate('ConvertEditionFileToMain')}
              isSpinning={!!slot.isConverting}
              onPress={handleConvertToMainPress}
            />
          </>
        ) : (
          <SpinnerIconButton
            className={styles.actionButton}
            name={icons.MOVIE_FILE}
            title={translate('AssignEditionFile')}
            isSpinning={!!slot.isAssigning}
            onPress={handleAssignPress}
          />
        )}

        <SpinnerIconButton
          className={styles.actionButton}
          name={icons.DELETE}
          title={translate('Delete')}
          isSpinning={!!slot.isDeleting}
          onPress={handleDeletePress}
        />
      </TableRowCell>
    </TableRow>
  );
}

interface MovieEditionSlotsTableProps {
  movieId: number;
}

function MovieEditionSlotsTable({ movieId }: MovieEditionSlotsTableProps) {
  const dispatch = useDispatch();
  const commands = useSelector(createCommandsSelector());

  // Ensure quality profile options are loaded even when navigating directly to movie details.
  useEffect(() => {
    dispatch(fetchQualityProfiles());
  }, [dispatch]);

  const [isFetching, setIsFetching] = useState(false);
  const [isPopulated, setIsPopulated] = useState(false);
  const [fetchError, setFetchError] = useState<string | null>(null);
  const [slots, setSlots] = useState<MovieEditionSlot[]>([]);

  const movieFiles = useSelector((state: AppState) => state.movieFiles.items);
  const [newEditionName, setNewEditionName] = useState('');
  const [newSearchTerm, setNewSearchTerm] = useState('');
  const [isAdding, setIsAdding] = useState(false);
  const [interactiveSearchSlot, setInteractiveSearchSlot] =
    useState<MovieEditionSlot | null>(null);
  const [pendingDeleteSlot, setPendingDeleteSlot] =
    useState<PendingDeleteSlot | null>(null);
  const [pendingConvertSlot, setPendingConvertSlot] =
    useState<PendingConvertSlot | null>(null);
  const [pendingAssignSlot, setPendingAssignSlot] =
    useState<PendingAssignSlot | null>(null);

  const fetchSlots = useCallback(() => {
    setIsFetching(true);
    setIsPopulated(false);
    setFetchError(null);

    const { request } = createAjaxRequest({
      url: '/movieeditionslot',
      data: { movieId },
      traditional: true,
    });

    request.done((data: MovieEditionSlot[]) => {
      setSlots(data);
      setIsFetching(false);
      setIsPopulated(true);
    });

    request.fail(() => {
      setFetchError(translate('LoadingMovieEditionSlotsFailed'));
      setIsFetching(false);
    });
  }, [movieId]);

  useEffect(() => {
    fetchSlots();
  }, [fetchSlots]);

  const assignedEditionFileIds = useMemo(() => {
    return new Set(
      slots.flatMap((slot) => {
        const movieFileId = slot.movieFile?.id ?? slot.movieFileId;
        return movieFileId ? [movieFileId] : [];
      })
    );
  }, [slots]);

  const assignableFiles = useMemo(() => {
    return movieFiles.filter((file) => {
      const fileMovieEditionSlotId = file.movieEditionSlotId;
      return (
        file.movieId === movieId &&
        !assignedEditionFileIds.has(file.id) &&
        !fileMovieEditionSlotId
      );
    });
  }, [assignedEditionFileIds, movieFiles, movieId]);

  const handleNewEditionNameChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      setNewEditionName(event.target.value);
    },
    []
  );

  const handleNewSearchTermChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      setNewSearchTerm(event.target.value);
    },
    []
  );

  const isSearchingAll = useMemo(() => {
    return isCommandExecuting(
      findCommand(commands, {
        name: commandNames.MOVIE_EDITION_SEARCH,
        movieId,
      })
    );
  }, [commands, movieId]);

  const handleSearchAllPress = useCallback(() => {
    dispatch(
      executeCommand({
        name: commandNames.MOVIE_EDITION_SEARCH,
        movieId,
      })
    );
  }, [dispatch, movieId]);

  const handleRowSearchPress = useCallback(
    (slotId: number) => {
      dispatch(
        executeCommand({
          name: commandNames.MOVIE_EDITION_SEARCH,
          movieId,
          movieEditionSlotId: slotId,
        })
      );
    },
    [dispatch, movieId]
  );

  const handleInteractiveSearchPress = useCallback((slot: MovieEditionSlot) => {
    setInteractiveSearchSlot(slot);
  }, []);

  const handleInteractiveSearchModalClose = useCallback(() => {
    setInteractiveSearchSlot(null);
    fetchSlots();
  }, [fetchSlots]);

  const handleMonitorToggle = useCallback(
    (slot: MovieEditionSlot, monitored: boolean) => {
      setSlots((prev) =>
        prev.map((s) => (s.id === slot.id ? { ...s, isSaving: true } : s))
      );

      const { request } = createAjaxRequest({
        url: `/movieeditionslot/${slot.id}`,
        method: 'PUT',
        dataType: 'json',
        data: JSON.stringify({
          editionName: slot.editionName,
          searchTerm: slot.searchTerm,
          aliases: slot.aliases,
          monitored,
          qualityProfileId: slot.qualityProfileId,
          minimumCustomFormatScore: slot.minimumCustomFormatScore,
        }),
      });

      request.done(() => {
        setSlots((prev) =>
          prev.map((s) =>
            s.id === slot.id ? { ...s, monitored, isSaving: false } : s
          )
        );
      });

      request.fail(() => {
        setSlots((prev) =>
          prev.map((s) => (s.id === slot.id ? { ...s, isSaving: false } : s))
        );
      });
    },
    []
  );

  const handleAdd = useCallback(() => {
    if (!newEditionName.trim()) {
      return;
    }

    setIsAdding(true);

    const { request } = createAjaxRequest({
      url: '/movieeditionslot',
      method: 'POST',
      dataType: 'json',
      data: JSON.stringify({
        movieId,
        editionName: newEditionName.trim(),
        searchTerm: newSearchTerm.trim() || null,
        monitored: true,
      }),
    });

    request.done((data: unknown) => {
      if (data && typeof data === 'object' && !Array.isArray(data)) {
        setSlots((prev) => [...prev, data as MovieEditionSlot]);
      } else {
        fetchSlots();
      }
      setNewEditionName('');
      setNewSearchTerm('');
      setIsAdding(false);
    });

    request.fail(() => {
      setIsAdding(false);
    });
  }, [movieId, newEditionName, newSearchTerm, fetchSlots]);

  const handleSaveSlot = useCallback(
    (
      slot: MovieEditionSlot,
      editionName: string,
      searchTerm: string,
      qualityProfileId: number | null,
      minimumCustomFormatScore: number | null
    ) => {
      setSlots((prev) =>
        prev.map((s) => (s.id === slot.id ? { ...s, isSaving: true } : s))
      );

      const { request } = createAjaxRequest({
        url: `/movieeditionslot/${slot.id}`,
        method: 'PUT',
        dataType: 'json',
        data: JSON.stringify({
          editionName,
          searchTerm: searchTerm || null,
          qualityProfileId,
          minimumCustomFormatScore,
        }),
      });

      request.done((data: MovieEditionSlot) => {
        setSlots((prev) =>
          prev.map((s) => (s.id === slot.id ? { ...data, isSaving: false } : s))
        );
      });

      request.fail(() => {
        setSlots((prev) =>
          prev.map((s) => (s.id === slot.id ? { ...s, isSaving: false } : s))
        );
      });
    },
    []
  );

  const handleAssignSlotPress = useCallback(
    (slot: MovieEditionSlot) => {
      const defaultFile = assignableFiles[0];
      setPendingAssignSlot({ slot, movieFileId: defaultFile?.id ?? null });
    },
    [assignableFiles]
  );

  const handleAssignFileChange = useCallback(
    (event: React.ChangeEvent<HTMLSelectElement>) => {
      const movieFileId = parseInt(event.target.value);

      setPendingAssignSlot((pending) =>
        pending
          ? {
              ...pending,
              movieFileId: Number.isNaN(movieFileId) ? null : movieFileId,
            }
          : pending
      );
    },
    []
  );

  const handleAssignModalClose = useCallback(() => {
    setPendingAssignSlot(null);
  }, []);

  const handleConfirmAssignSlot = useCallback(() => {
    if (!pendingAssignSlot?.movieFileId) {
      return;
    }

    const { slot, movieFileId } = pendingAssignSlot;

    setSlots((prev) =>
      prev.map((s) => (s.id === slot.id ? { ...s, isAssigning: true } : s))
    );

    const { request } = createAjaxRequest({
      url: `/movieeditionslot/${slot.id}/assignfile`,
      method: 'POST',
      dataType: 'json',
      data: JSON.stringify({ movieId, movieFileId }),
    });

    request.done(() => {
      setPendingAssignSlot(null);
      fetchSlots();
    });

    request.fail(() => {
      setSlots((prev) =>
        prev.map((s) => (s.id === slot.id ? { ...s, isAssigning: false } : s))
      );
      setPendingAssignSlot(null);
    });
  }, [fetchSlots, movieId, pendingAssignSlot]);

  const handleUnassignSlotPress = useCallback(
    (slot: MovieEditionSlot) => {
      setSlots((prev) =>
        prev.map((s) => (s.id === slot.id ? { ...s, isAssigning: true } : s))
      );

      const { request } = createAjaxRequest({
        url: `/movieeditionslot/${slot.id}/unassignfile`,
        method: 'POST',
        dataType: 'json',
      });

      request.done(() => {
        fetchSlots();
      });

      request.fail(() => {
        setSlots((prev) =>
          prev.map((s) => (s.id === slot.id ? { ...s, isAssigning: false } : s))
        );
      });
    },
    [fetchSlots]
  );

  const handleConvertToMainSlotPress = useCallback((slot: MovieEditionSlot) => {
    setPendingConvertSlot({ slot, existingMainFileAction: 'reject' });
  }, []);

  const handleExistingMainActionChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      const existingMainFileAction = event.target
        .value as ExistingMainFileAction;

      setPendingConvertSlot((pending) =>
        pending ? { ...pending, existingMainFileAction } : pending
      );
    },
    []
  );

  const handleConvertModalClose = useCallback(() => {
    setPendingConvertSlot(null);
  }, []);

  const handleConfirmConvertToMain = useCallback(() => {
    if (!pendingConvertSlot) {
      return;
    }

    const { slot, existingMainFileAction } = pendingConvertSlot;

    setSlots((prev) =>
      prev.map((s) => (s.id === slot.id ? { ...s, isConverting: true } : s))
    );

    const { request } = createAjaxRequest({
      url: `/movieeditionslot/${slot.id}/converttomain`,
      method: 'POST',
      dataType: 'json',
      data: JSON.stringify({ existingMainFileAction }),
    });

    request.done(() => {
      setPendingConvertSlot(null);
      fetchSlots();
    });

    request.fail(() => {
      setSlots((prev) =>
        prev.map((s) => (s.id === slot.id ? { ...s, isConverting: false } : s))
      );
      setPendingConvertSlot(null);
    });
  }, [fetchSlots, pendingConvertSlot]);

  const handleDeleteSlotPress = useCallback((slot: MovieEditionSlot) => {
    setPendingDeleteSlot({ slot, attachedFileAction: 'keepUnassigned' });
  }, []);

  const handleDeleteModalClose = useCallback(() => {
    setPendingDeleteSlot(null);
  }, []);

  const handleDeleteFileActionChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      const attachedFileAction = event.target.value as EditionFileAction;

      setPendingDeleteSlot((pending) =>
        pending ? { ...pending, attachedFileAction } : pending
      );
    },
    []
  );

  const handleConfirmDeleteSlot = useCallback(() => {
    if (!pendingDeleteSlot) {
      return;
    }

    const { slot, attachedFileAction } = pendingDeleteSlot;

    setSlots((prev) =>
      prev.map((s) => (s.id === slot.id ? { ...s, isDeleting: true } : s))
    );

    const { request } = createAjaxRequest({
      url: `/movieeditionslot/${slot.id}`,
      method: 'DELETE',
      dataType: 'json',
      data: JSON.stringify({ attachedFileAction }),
    });

    request.done(() => {
      setSlots((prev) => prev.filter((s) => s.id !== slot.id));
      setPendingDeleteSlot(null);
    });

    request.fail(() => {
      setSlots((prev) =>
        prev.map((s) => (s.id === slot.id ? { ...s, isDeleting: false } : s))
      );
      setPendingDeleteSlot(null);
    });
  }, [pendingDeleteSlot]);

  const isRowSearching = useCallback(
    (slotId: number) => {
      return isCommandExecuting(
        findCommand(commands, {
          name: commandNames.MOVIE_EDITION_SEARCH,
          movieId,
          movieEditionSlotId: slotId,
        })
      );
    },
    [commands, movieId]
  );

  const downloadedCount = slots.filter(
    (slot) => slot.status === 'assigned' || !!slot.movieFile
  ).length;
  const monitoredCount = slots.filter((slot) => slot.monitored).length;
  const missingCount = slots.filter(
    (slot) => slot.status === 'missing' || (!slot.movieFile && slot.monitored)
  ).length;

  return (
    <FieldSet legend={translate('MovieEditions')}>
      <div className={styles.container}>
        {isFetching && <LoadingIndicator />}

        {!isFetching && fetchError ? (
          <div className={styles.emptyMessage}>{fetchError}</div>
        ) : null}

        {isPopulated && (
          <>
            <div className={styles.header}>
              <div className={styles.summary}>
                <span>
                  {translate('EditionSlotCount', { count: slots.length })}
                </span>
                <span>
                  {translate('EditionSlotMonitoredCount', {
                    count: monitoredCount,
                  })}
                </span>
                <span>
                  {translate('EditionSlotDownloadedCount', {
                    count: downloadedCount,
                  })}
                </span>
                <span>
                  {translate('EditionSlotMissingCount', {
                    count: missingCount,
                  })}
                </span>
              </div>

              <SpinnerIconButton
                className={styles.actionButton}
                name={icons.SEARCH}
                title={translate('SearchAllMonitoredEditions')}
                isSpinning={isSearchingAll}
                isDisabled={!monitoredCount}
                onPress={handleSearchAllPress}
              />
            </div>

            <div className={styles.addForm}>
              <label className={styles.addField}>
                <span>{translate('EditionName')}</span>
                <input
                  className={styles.addInput}
                  type="text"
                  value={newEditionName}
                  placeholder={translate('EditionNamePlaceholder')}
                  onChange={handleNewEditionNameChange}
                />
              </label>

              <label className={styles.addField}>
                <span>{translate('SearchTerm')}</span>
                <input
                  className={styles.addInput}
                  type="text"
                  value={newSearchTerm}
                  placeholder={translate('SearchTermPlaceholder')}
                  onChange={handleNewSearchTermChange}
                />
              </label>

              <SpinnerIconButton
                className={styles.actionButton}
                name={icons.ADD}
                title={translate('AddEditionSlot')}
                isSpinning={isAdding}
                isDisabled={!newEditionName.trim()}
                onPress={handleAdd}
              />
            </div>

            {!slots.length && !fetchError ? (
              <div className={styles.emptyMessage}>
                {translate('NoMovieEditionSlots')}
              </div>
            ) : null}

            {!!slots.length && (
              <table className={styles.table}>
                <thead>
                  <tr>
                    <th className={styles.monitorCell} />
                    <th>{translate('Edition')}</th>
                    <th>{translate('SearchTerm')}</th>
                    <th>{translate('Status')}</th>
                    <th>{translate('File')}</th>
                    <th>{translate('Quality')}</th>
                    <th>{translate('CustomFormats')}</th>
                    <th>{translate('CustomFormatScore')}</th>
                    <th>{translate('QualityProfile')}</th>
                    <th>{translate('MinimumCustomFormatScore')}</th>
                    <th>{translate('LastSearch')}</th>
                    <th className={styles.actionsCell} />
                  </tr>
                </thead>
                <tbody>
                  {slots.map((slot) => (
                    <MovieEditionSlotRow
                      key={slot.id}
                      slot={slot}
                      isSearching={isRowSearching(slot.id)}
                      onMonitorToggle={handleMonitorToggle}
                      onSearchPress={handleRowSearchPress}
                      onInteractiveSearchPress={handleInteractiveSearchPress}
                      onAssignPress={handleAssignSlotPress}
                      onUnassignPress={handleUnassignSlotPress}
                      onConvertToMainPress={handleConvertToMainSlotPress}
                      onSave={handleSaveSlot}
                      onDeletePress={handleDeleteSlotPress}
                    />
                  ))}
                </tbody>
              </table>
            )}
          </>
        )}

        {interactiveSearchSlot ? (
          <MovieEditionSlotInteractiveSearchModal
            isOpen={true}
            movieId={movieId}
            movieEditionSlotId={interactiveSearchSlot.id}
            editionName={interactiveSearchSlot.editionName}
            onModalClose={handleInteractiveSearchModalClose}
          />
        ) : null}

        <ConfirmModal
          isOpen={!!pendingAssignSlot}
          title={translate('AssignEditionFile')}
          message={
            <div className={styles.modalForm}>
              <p>
                {translate('AssignEditionFileMessage', {
                  editionName: pendingAssignSlot?.slot.editionName ?? '',
                })}
              </p>

              {assignableFiles.length ? (
                <select
                  className={styles.assignSelect}
                  value={pendingAssignSlot?.movieFileId ?? ''}
                  onChange={handleAssignFileChange}
                >
                  {assignableFiles.map((file: MovieFile) => (
                    <option key={file.id} value={file.id}>
                      {file.relativePath}
                    </option>
                  ))}
                </select>
              ) : (
                <div className={styles.emptyMessage}>
                  {translate('NoUnassignedMovieFiles')}
                </div>
              )}
            </div>
          }
          confirmLabel={translate('Assign')}
          isSpinning={!!pendingAssignSlot?.slot.isAssigning}
          onConfirm={handleConfirmAssignSlot}
          onCancel={handleAssignModalClose}
        />

        <ConfirmModal
          isOpen={!!pendingConvertSlot}
          kind={kinds.DANGER}
          title={translate('ConvertEditionFileToMain')}
          message={
            <div>
              <p>
                {translate('ConvertEditionFileToMainMessage', {
                  editionName: pendingConvertSlot?.slot.editionName ?? '',
                })}
              </p>

              <div className={styles.deleteOptions}>
                <div className={styles.deleteOptionsLabel}>
                  {translate('ExistingMainFileAction')}
                </div>

                <label>
                  <input
                    type="radio"
                    value="reject"
                    checked={
                      pendingConvertSlot?.existingMainFileAction === 'reject'
                    }
                    onChange={handleExistingMainActionChange}
                  />
                  {translate('RejectIfMainExists')}
                </label>

                <label>
                  <input
                    type="radio"
                    value="keepUnassigned"
                    checked={
                      pendingConvertSlot?.existingMainFileAction ===
                      'keepUnassigned'
                    }
                    onChange={handleExistingMainActionChange}
                  />
                  {translate('KeepExistingMainUnassigned')}
                </label>

                <label>
                  <input
                    type="radio"
                    value="deleteRecycle"
                    checked={
                      pendingConvertSlot?.existingMainFileAction ===
                      'deleteRecycle'
                    }
                    onChange={handleExistingMainActionChange}
                  />
                  {translate('DeleteExistingMainRecycle')}
                </label>
              </div>
            </div>
          }
          confirmLabel={translate('Convert')}
          isSpinning={!!pendingConvertSlot?.slot.isConverting}
          onConfirm={handleConfirmConvertToMain}
          onCancel={handleConvertModalClose}
        />

        <ConfirmModal
          isOpen={!!pendingDeleteSlot}
          kind={kinds.DANGER}
          title={translate('DeleteEditionSlot')}
          message={
            <div>
              <p>
                {translate('DeleteEditionSlotMessage', {
                  editionName: pendingDeleteSlot?.slot.editionName ?? '',
                })}
              </p>

              {pendingDeleteSlot?.slot.movieFile ? (
                <div className={styles.deleteOptions}>
                  <div className={styles.deleteOptionsLabel}>
                    {translate('AttachedEditionFileAction')}
                  </div>

                  <label>
                    <input
                      type="radio"
                      value="keepUnassigned"
                      checked={
                        pendingDeleteSlot.attachedFileAction ===
                        'keepUnassigned'
                      }
                      onChange={handleDeleteFileActionChange}
                    />
                    {translate('KeepFileUnassigned')}
                  </label>

                  <label>
                    <input
                      type="radio"
                      value="deleteRecycle"
                      checked={
                        pendingDeleteSlot.attachedFileAction === 'deleteRecycle'
                      }
                      onChange={handleDeleteFileActionChange}
                    />
                    {translate('DeleteFileRecycle')}
                  </label>
                </div>
              ) : null}
            </div>
          }
          confirmLabel={translate('Delete')}
          isSpinning={!!pendingDeleteSlot?.slot.isDeleting}
          onConfirm={handleConfirmDeleteSlot}
          onCancel={handleDeleteModalClose}
        />
      </div>
    </FieldSet>
  );
}

export default MovieEditionSlotsTable;
