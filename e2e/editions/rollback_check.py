#!/usr/bin/env python3
"""Switching an existing stock Radarr install to the editions build, and rolling it back.

Follows the deployment guide on a throwaway library and Postgres database:
  1. stock Radarr owns the database and library, as on Unraid today
  2. the database is backed up with pg_dump
  3. the editions build starts on the same database, migrates it and must keep every file
  4. rollback: the backup is restored and stock Radarr starts on it again
  5. what happens if stock Radarr is started on the migrated database instead of the backup

Usage:
  rollback_check.py --stock /path/to/stock/Radarr --fork /path/to/editions/Radarr --work /tmp/radarr-rollback

Needs python3, ffmpeg, psql, pg_dump and pg_restore. Writes results.json and results.md to the work folder.
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import mocks  # noqa: E402
import run_e2e as e2e  # noqa: E402


def pg_env(args):
    return dict(os.environ, PGPASSWORD=args.pg_password)


def pg_conn(args):
    return ["-h", args.pg_host, "-p", str(args.pg_port), "-U", args.pg_user]


def recreate_databases(args):
    for suffix in ("main", "log"):
        e2e.psql(args, f'DROP DATABASE IF EXISTS "{args.pg_prefix}_{suffix}"')
        e2e.psql(args, f'CREATE DATABASE "{args.pg_prefix}_{suffix}"')


def backup(args, folder):
    os.makedirs(folder, exist_ok=True)
    for suffix in ("main", "log"):
        subprocess.run(["pg_dump", *pg_conn(args), "-Fc", "-f", os.path.join(folder, f"{suffix}.dump"), f"{args.pg_prefix}_{suffix}"],
                       env=pg_env(args), check=True)


def restore(args, folder):
    recreate_databases(args)
    for suffix in ("main", "log"):
        subprocess.run(["pg_restore", *pg_conn(args), "--no-owner", "-d", f"{args.pg_prefix}_{suffix}", os.path.join(folder, f"{suffix}.dump")],
                       env=pg_env(args), check=True)


class Instance:
    """One Radarr process on the shared data folder and database."""

    def __init__(self, binary, data, log_path, port):
        self.log_path = log_path
        self.offset = os.path.getsize(log_path) if os.path.exists(log_path) else 0
        self.log = open(log_path, "a")
        self.proc = subprocess.Popen([binary, "-nobrowser", f"-data={data}"], cwd=os.path.dirname(binary),
                                     stdout=self.log, stderr=subprocess.STDOUT)
        self.radarr = e2e.Radarr(port)

        for _ in range(120):
            if self.proc.poll() is not None:
                raise RuntimeError(f"{binary} exited with {self.proc.returncode}, see {log_path}")
            try:
                self.status = self.radarr.get("/system/status")
                return
            except Exception:
                time.sleep(1)

        raise RuntimeError(f"{binary} did not start, see {log_path}")

    def errors(self):
        offline = ("ServerSideNotificationService", "RadarrAPIClient", "SkyHook", "UpdatePackageProvider", "CheckUpdateService")
        with open(self.log_path, errors="replace") as f:
            f.seek(self.offset)
            return [line for line in f if ("|Error|" in line or "|Fatal|" in line) and not any(o in line for o in offline)]

    def stop(self):
        self.proc.terminate()
        try:
            self.proc.wait(30)
        except subprocess.TimeoutExpired:
            self.proc.kill()
        self.log.close()


def add_stock_movie(args, root, title, year, tmdb_id, profile_id):
    """Like run_e2e.add_movie, for stock Radarr's schema (no editions)."""
    db = f"{args.pg_prefix}_main"
    title_sql = title.replace("'", "''")
    clean = "".join(c for c in title.lower() if c.isalnum())
    metadata_id = e2e.psql(args, f"""INSERT INTO "MovieMetadata" ("TmdbId", "Images", "Genres", "Title", "SortTitle", "CleanTitle", "OriginalTitle",
        "CleanOriginalTitle", "OriginalLanguage", "Status", "Runtime", "Year", "Recommendations", "Overview", "Ratings", "Keywords",
        "InCinemas", "PhysicalRelease", "DigitalRelease")
        VALUES ({tmdb_id}, '[]', '[]', '{title_sql}', '{title_sql.lower()}', '{clean}', '{title_sql}', '{clean}', 1, 3, 3, {year}, '[]',
        'e2e', '{{}}', '[]', '{year}-06-01', '{year}-12-01', '{year}-12-01') RETURNING "Id" """, db).splitlines()[0]

    path = os.path.join(root, f"{title} ({year})")
    os.makedirs(path, exist_ok=True)

    movie_id = e2e.psql(args, f"""INSERT INTO "Movies" ("Path", "Monitored", "QualityProfileId", "Added", "Tags", "AddOptions", "MovieFileId",
        "MinimumAvailability", "MovieMetadataId")
        VALUES ('{path.replace("'", "''")}', true, {profile_id}, now(), '[]', '{{}}', 0, 3, {metadata_id}) RETURNING "Id" """, db).splitlines()[0]

    return int(movie_id), path


SETTINGS_TABLES = ["Config", "QualityProfiles", "Indexers", "DownloadClients", "RootFolders", "NamingConfig", "Tags", "CustomFormats"]


def settings(args):
    db = f"{args.pg_prefix}_main"
    return {t: e2e.psql(args, f'SELECT * FROM "{t}" ORDER BY 1', db) for t in SETTINGS_TABLES}


def db_state(args):
    db = f"{args.pg_prefix}_main"
    movies = e2e.psql(args, 'SELECT "Id", "MovieFileId" FROM "Movies" ORDER BY "Id"', db)
    movie_files = e2e.psql(args, 'SELECT "Id", "MovieId", "RelativePath" FROM "MovieFiles" ORDER BY "Id"', db)
    return movies.splitlines(), movie_files.splitlines()


def run(args):
    work = os.path.abspath(args.work)
    shutil.rmtree(work, ignore_errors=True)
    data, root, log_path = os.path.join(work, "data"), os.path.join(work, "movies"), os.path.join(work, "radarr.log")
    for d in (root, os.path.join(work, "complete"), os.path.join(work, "recycle")):
        os.makedirs(d)
    args.work = work
    e2e.write_config(data, args)
    recreate_databases(args)

    r = e2e.Results()
    instance = None
    state, server = mocks.start(args.mock_port, os.path.join(work, "complete"))

    try:
        r.scenario = "Stock Radarr owns the library"
        instance = Instance(args.stock, data, log_path, args.port)
        print(f"Stock Radarr {instance.status['version']}\n\n{r.scenario}")
        radarr = instance.radarr
        # The usual settings of a real install: root folder, indexer, download client, tuned profile
        state.publish("Some.Other.Movie.2020.1080p.BluRay.x264-GRP")
        profile_id = e2e.setup(radarr, args, args.mock_port, root)
        radarr.call("POST", "/tag", {"label": "kids"})

        arrival, arrival_path = add_stock_movie(args, root, "Arrival", 2016, 329865, profile_id)
        blade, blade_path = add_stock_movie(args, root, "Blade Runner", 1982, 78, profile_id)
        mocks.make_video(os.path.join(arrival_path, "Arrival.2016.1080p.BluRay.x264-GRP.mkv"), "arrival")
        mocks.make_video(os.path.join(blade_path, "Blade.Runner.1982.The.Final.Cut.1080p.BluRay.x264-GRP.mkv"), "final cut")
        radarr.command("RescanMovie")

        stock_movies, stock_files = db_state(args)
        stock_settings = settings(args)
        library = e2e.snapshot(root)
        r.check("both movies have their file", all(not line.endswith("|0") for line in stock_movies), str(stock_movies))
        stock_version = instance.status["version"]
        r.check("no errors in stock Radarr's log", not instance.errors(), "".join(instance.errors()[:10]))
        instance.stop()
        instance = None

        r.scenario = "Back up before switching"
        print(f"\n{r.scenario}")
        backup(args, os.path.join(work, "backup-before-editions"))
        r.check("pg_dump backup written", all(os.path.getsize(os.path.join(work, "backup-before-editions", f"{s}.dump")) > 0 for s in ("main", "log")))

        r.scenario = "Editions build takes over the same database"
        print(f"\n{r.scenario}")
        instance = Instance(args.fork, data, log_path, args.port)
        radarr = instance.radarr
        r.check("editions build starts and migrates", instance.status["version"] is not None, str(instance.status.get("version")))
        movies, movie_files = db_state(args)
        r.check("movies keep their main file", movies == stock_movies, f"{stock_movies} -> {movies}")
        r.check("file records kept", movie_files == stock_files, f"{stock_files} -> {movie_files}")
        changed = [t for t, rows in settings(args).items() if rows != stock_settings[t]]
        r.check("settings, profiles, indexers and download clients preserved", not changed, str(changed))
        eds = e2e.editions(radarr, blade)
        r.check("existing file becomes the default edition", list(eds) == ["Final Cut"] and eds["Final Cut"]["isDefault"]
                and eds["Final Cut"]["movieFileId"] == int(stock_files[1].split("|")[0]), json.dumps(eds))

        # A second edition added after the switch, which stock Radarr knows nothing about
        mocks.make_video(os.path.join(blade_path, "Blade.Runner.1982.Directors.Cut.1080p.BluRay.x264-GRP.mkv"), "directors cut")
        edition = radarr.call("POST", "/movieedition", {"movieId": blade, "title": "Director's Cut", "monitored": True, "qualityProfileId": 0})
        radarr.command("RescanMovie", movieId=blade)
        unmapped = [f for f in e2e.files(radarr, blade) if f["relativePath"].startswith("Blade.Runner.1982.Directors")]
        if unmapped and unmapped[0]["movieEditionId"] != edition["id"]:
            radarr.call("PUT", "/moviefile/bulk", [{"id": unmapped[0]["id"], "movieEditionId": edition["id"]}], ok=(200, 202))
        by_edition = e2e.files_by_edition(radarr, blade)
        r.check("second edition's file imported", len(by_edition.get("Director's Cut", [])) == 1,
                str({k: len(v) for k, v in by_edition.items()}))
        r.check("original files untouched by the switch", all(e2e.snapshot(root).get(k) == v for k, v in library.items()))
        r.check("no errors in the editions build's log", not instance.errors(), "".join(instance.errors()[:10]))
        instance.stop()
        instance = None
        backup(args, os.path.join(work, "backup-with-editions"))
        library_with_editions = e2e.snapshot(root)

        r.scenario = "Rollback: restore the backup and start stock Radarr"
        print(f"\n{r.scenario}")
        restore(args, os.path.join(work, "backup-before-editions"))
        instance = Instance(args.stock, data, log_path, args.port)
        radarr = instance.radarr
        r.check("stock Radarr starts on the restored backup", instance.status["version"] == stock_version, instance.status["version"])
        movies, movie_files = db_state(args)
        r.check("database is exactly as before the switch", movies == stock_movies and movie_files == stock_files)
        r.check("no editions tables in the restored database",
                e2e.psql(args, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'MovieEditions'", f"{args.pg_prefix}_main") == "0")
        radarr.command("RescanMovie")
        radarr.command("Housekeeping")
        r.check("every file still on disk", e2e.snapshot(root) == library_with_editions)
        r.check("no errors in stock Radarr's log", not instance.errors(), "".join(instance.errors()[:10]))
        instance.stop()
        instance = None

        r.scenario = "Not recommended: stock Radarr on the migrated database"
        print(f"\n{r.scenario}")
        restore(args, os.path.join(work, "backup-with-editions"))
        file_rows = e2e.psql(args, 'SELECT count(*) FROM "MovieFiles"', f"{args.pg_prefix}_main")
        instance = Instance(args.stock, data, log_path, args.port)
        radarr = instance.radarr
        r.check("stock Radarr starts on the migrated database", instance.status["version"] == stock_version, instance.status["version"])
        r.check("movies list loads", len(radarr.get("/movie")) == 2)
        radarr.command("Housekeeping")
        after_rows = e2e.psql(args, 'SELECT count(*) FROM "MovieFiles"', f"{args.pg_prefix}_main")
        # Expected: stock housekeeping drops the record of every file that isn't a movie's main file
        r.check("stock housekeeping forgets extra editions' files (expected)", int(after_rows) < int(file_rows), f"{file_rows} -> {after_rows}")
        r.check("files on disk still untouched", e2e.snapshot(root) == library_with_editions)
        instance.stop()
        instance = None
    finally:
        if instance:
            instance.stop()
        server.shutdown()

    passed = sum(c["passed"] for c in r.checks)
    with open(os.path.join(work, "results.json"), "w") as f:
        json.dump({"passed": passed, "failed": len(r.checks) - passed, "checks": r.checks}, f, indent=2)

    with open(os.path.join(work, "results.md"), "w") as f:
        f.write(f"# Editions switch and rollback results\n\n{passed} of {len(r.checks)} checks passed.\n")
        current = None
        for c in r.checks:
            if c["scenario"] != current:
                current = c["scenario"]
                f.write(f"\n## {current}\n\n")
            f.write(f"- {'✅' if c['passed'] else '❌'} {c['check']}\n")

    print(f"\n{passed} of {len(r.checks)} checks passed")
    return 0 if passed == len(r.checks) else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--stock", required=True, help="Path to a stock Radarr executable of the release the fork is based on")
    parser.add_argument("--fork", required=True, help="Path to the editions build's Radarr executable")
    parser.add_argument("--work", default="/tmp/radarr-rollback")
    parser.add_argument("--port", type=int, default=17981)
    parser.add_argument("--mock-port", type=int, default=17982)
    parser.add_argument("--pg-host", default="localhost")
    parser.add_argument("--pg-port", type=int, default=5432)
    parser.add_argument("--pg-user", default="radarr")
    parser.add_argument("--pg-password", default="radarr")
    parser.add_argument("--pg-prefix", default="radarr_rollback")
    args = parser.parse_args()
    sys.exit(run(args))
