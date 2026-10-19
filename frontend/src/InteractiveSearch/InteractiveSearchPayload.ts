interface MovieSearchPayload {
  movieId: number;
  movieEditionId?: number;
}

type InteractiveSearchPayload = MovieSearchPayload;

export default InteractiveSearchPayload;
