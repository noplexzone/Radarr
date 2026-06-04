import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import * as commandNames from 'Commands/commandNames';
import FieldSet from 'Components/FieldSet';
import Icon from 'Components/Icon';
import SpinnerIconButton from 'Components/Link/SpinnerIconButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import MonitorToggleButton from 'Components/MonitorToggleButton';
import RelativeDateCell from 'Components/Table/Cells/RelativeDateCell';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds } from 'Helpers/Props';
import { executeCommand } from 'Store/Actions/commandActions';
import createCommandsSelector from 'Store/Selectors/createCommandsSelector';
import { findCommand, isCommandExecuting } from 'Utilities/Command';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import styles from './MovieEditionSlotsTable.css';

interface MovieEditionSlot {
  id: number;
  movieId: number;
  editionName: string;
  searchTerm: string | null;
  monitored: boolean;
  movieFileId: number | null;
  lastSearchTime: string | null;
  dateAdded: string;
  isSaving?: boolean;
}

interface MovieEditionSlotRowProps {
  slot: MovieEditionSlot;
  isSearching: boolean;
  onMonitorToggle: (slot: MovieEditionSlot, monitored: boolean) => void;
  onSearchPress: (slotId: number) => void;
}

function MovieEditionSlotRow(props: MovieEditionSlotRowProps) {
  const { slot, isSearching, onMonitorToggle, onSearchPress } = props;

  const handleMonitorTogglePress = useCallback(
    (monitored: boolean) => {
      onMonitorToggle(slot, monitored);
    },
    [slot, onMonitorToggle]
  );

  const handleSearchPress = useCallback(() => {
    onSearchPress(slot.id);
  }, [slot.id, onSearchPress]);

  return (
    <TableRow>
      <TableRowCell className={styles.monitorCell}>
        <MonitorToggleButton
          monitored={slot.monitored}
          isSaving={slot.isSaving}
          onPress={handleMonitorTogglePress}
        />
      </TableRowCell>

      <TableRowCell>{slot.editionName}</TableRowCell>

      <TableRowCell>{slot.searchTerm ?? '-'}</TableRowCell>

      <TableRowCell>
        {slot.movieFileId ? (
          <span className={styles.statusHasFile}>
            <Icon
              name={icons.CHECK}
              kind={kinds.SUCCESS}
              title={translate('HasFile')}
            />{' '}
            {translate('HasFile')}
          </span>
        ) : (
          <span className={styles.statusMissing}>
            <Icon
              name={icons.MISSING}
              kind={kinds.DANGER}
              title={translate('Missing')}
            />{' '}
            {translate('Missing')}
          </span>
        )}
      </TableRowCell>

      <RelativeDateCell
        date={slot.lastSearchTime ?? undefined}
        includeTime={true}
      />

      <TableRowCell className={styles.actionsCell}>
        <SpinnerIconButton
          name={icons.SEARCH}
          title={translate('SearchEdition')}
          isSpinning={isSearching}
          onPress={handleSearchPress}
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

  const [isFetching, setIsFetching] = useState(false);
  const [isPopulated, setIsPopulated] = useState(false);
  const [fetchError, setFetchError] = useState<string | null>(null);
  const [slots, setSlots] = useState<MovieEditionSlot[]>([]);

  useEffect(() => {
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

  const handleMonitorToggle = useCallback(
    (slot: MovieEditionSlot, monitored: boolean) => {
      setSlots((prev) =>
        prev.map((s) => (s.id === slot.id ? { ...s, isSaving: true } : s))
      );

      const { request } = createAjaxRequest({
        url: `/movieeditionslot/${slot.id}`,
        method: 'PUT',
        dataType: 'json',
        data: JSON.stringify({ ...slot, monitored }),
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

  return (
    <FieldSet legend={translate('MovieEditions')}>
      <div className={styles.container}>
        {isFetching && <LoadingIndicator />}

        {!isFetching && fetchError ? (
          <div className={styles.emptyMessage}>{fetchError}</div>
        ) : null}

        {isPopulated && !slots.length && !fetchError ? (
          <div className={styles.emptyMessage}>
            {translate('NoMovieEditionSlots')}
          </div>
        ) : null}

        {isPopulated && !!slots.length && (
          <>
            <div className={styles.searchAllButton}>
              <SpinnerIconButton
                name={icons.SEARCH}
                title={translate('SearchAllMonitoredEditions')}
                isSpinning={isSearchingAll}
                onPress={handleSearchAllPress}
              />
            </div>

            <table>
              <thead>
                <tr>
                  <th className={styles.monitorCell} />
                  <th>{translate('Edition')}</th>
                  <th>{translate('SearchTerm')}</th>
                  <th>{translate('Status')}</th>
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
                  />
                ))}
              </tbody>
            </table>
          </>
        )}
      </div>
    </FieldSet>
  );
}

export default MovieEditionSlotsTable;
