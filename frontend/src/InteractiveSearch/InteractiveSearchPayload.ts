interface MovieSearchPayload {
  movieId: number;
}

interface MovieEditionSlotSearchPayload {
  movieId: number;
  movieEditionSlotId: number;
}

type InteractiveSearchPayload =
  | MovieSearchPayload
  | MovieEditionSlotSearchPayload;

export type { MovieEditionSlotSearchPayload };
export default InteractiveSearchPayload;
