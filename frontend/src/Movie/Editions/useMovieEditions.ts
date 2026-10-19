import { useQueryClient } from '@tanstack/react-query';
import { useCallback } from 'react';
import useApiQuery, { ApiError } from 'Helpers/Hooks/useApiQuery';
import MovieEdition from './MovieEdition';

const apiRoot = window.Radarr.apiRoot;
const path = '/movieedition';

export function useMovieEditions(movieId: number) {
  return useApiQuery<MovieEdition[]>({
    path: `${path}?movieId=${movieId}`,
  });
}

export interface SaveMovieEdition {
  id?: number;
  movieId: number;
  title: string;
  monitored: boolean;
  qualityProfileId: number;
  searchForEdition?: boolean;
}

async function send(url: string, method: string, body?: object) {
  const response = await fetch(apiRoot + url, {
    method,
    headers: {
      'Content-Type': 'application/json',
      'X-Api-Key': window.Radarr.apiKey,
    },
    body: body ? JSON.stringify(body) : undefined,
  });

  if (!response.ok) {
    let message: string | undefined = undefined;

    try {
      const json = await response.json();

      // Validation failures come as a list, other errors carry their text in content
      if (Array.isArray(json)) {
        message = json
          .map((e: { errorMessage: string }) => e.errorMessage)
          .join(', ');
      } else {
        message =
          typeof json.content === 'string' ? json.content : json.message;
      }
    } catch {
      // No details to show
    }

    throw new ApiError(url, response.status, response.statusText, {
      message: message ?? response.statusText,
      details: '',
    });
  }
}

// Changes go straight to the API, then every editions list is fetched again
export function useMovieEditionActions() {
  const queryClient = useQueryClient();

  const refresh = useCallback(() => {
    return queryClient.invalidateQueries({
      predicate: (query) =>
        String(query.queryKey[0]).startsWith(apiRoot + path),
    });
  }, [queryClient]);

  const saveEdition = useCallback(
    async (edition: SaveMovieEdition) => {
      if (edition.id) {
        await send(`${path}/${edition.id}`, 'PUT', edition);
      } else {
        await send(path, 'POST', edition);
      }

      await refresh();
    },
    [refresh]
  );

  const deleteEdition = useCallback(
    async (id: number, deleteFiles: boolean) => {
      await send(`${path}/${id}?deleteFiles=${deleteFiles}`, 'DELETE');
      await refresh();
    },
    [refresh]
  );

  return { saveEdition, deleteEdition, refresh };
}
