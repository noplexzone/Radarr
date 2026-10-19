import ModelBase from 'App/ModelBase';
import { MovieFile } from 'MovieFile/MovieFile';

export interface MovieEditionSummary extends ModelBase {
  title: string;
  isDefault: boolean;
  monitored: boolean;
  hasFile: boolean;
  movieFileId: number;
}

export interface MovieEdition extends MovieEditionSummary {
  movieId: number;

  // 0 uses the movie's quality profile
  qualityProfileId: number;
  added: string;
  lastSearchTime?: string;
  movieFile?: MovieFile;
  fileCount: number;
  isDuplicate: boolean;
}

export default MovieEdition;
