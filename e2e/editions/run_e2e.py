#!/usr/bin/env python3
"""End-to-end tests for movie editions against a real Radarr build and a Postgres database.

Radarr runs as a normal process with a fresh data folder and two fresh Postgres databases. A mock
Newznab indexer and a mock SABnzbd (see mocks.py) stand in for the outside world, so grabs, completed
download handling, imports and upgrades all run through Radarr's real code paths. Nothing touches a
real library: every movie folder lives under the work folder.

Usage:
  run_e2e.py --radarr /path/to/Radarr --work /tmp/radarr-e2e [--pg-host localhost --pg-user radarr --pg-password radarr]

Needs python3, ffmpeg and psql. Writes results.json and results.md to the work folder.
"""

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mocks  # noqa: E402

API_KEY = "e2etestkey000000000000000000000a"


class Radarr:
    def __init__(self, port):
        self.base = f"http://127.0.0.1:{port}/api/v3"

    def call(self, method, path, body=None, ok=(200, 201, 202)):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.base + path, data=data, method=method,
                                     headers={"X-Api-Key": API_KEY, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=60) as resp:
                text = resp.read().decode()
                status = resp.status
        except urllib.error.HTTPError as e:
            text = e.read().decode()
            status = e.code

        if status not in ok:
            raise RuntimeError(f"{method} {path} returned {status}: {text[:500]}")

        return json.loads(text) if text else None

    def get(self, path):
        return self.call("GET", path)

    def command(self, name, timeout=180, **body):
        cmd = self.call("POST", "/command", dict(name=name, **body))
        deadline = time.time() + timeout

        while time.time() < deadline:
            state = self.get(f"/command/{cmd['id']}")
            if state["status"] in ("completed", "failed", "aborted", "cancelled", "orphaned"):
                if state["status"] != "completed":
                    raise RuntimeError(f"Command {name} ended as {state['status']}: {state.get('message')}")
                return state
            time.sleep(0.5)

        raise RuntimeError(f"Command {name} did not finish in {timeout}s")


class Results:
    def __init__(self):
        self.checks = []
        self.scenario = None

    def check(self, name, passed, detail=""):
        self.checks.append({"scenario": self.scenario, "check": name, "passed": bool(passed), "detail": detail})
        print(f"  [{'PASS' if passed else 'FAIL'}] {name}" + (f" ({detail})" if detail and not passed else ""))
        return passed


def psql(args, sql, db="postgres"):
    env = dict(os.environ, PGPASSWORD=args.pg_password)
    out = subprocess.run(["psql", "-h", args.pg_host, "-p", str(args.pg_port), "-U", args.pg_user, "-d", db,
                          "-v", "ON_ERROR_STOP=1", "-t", "-A", "-c", sql],
                         env=env, check=True, capture_output=True, text=True)
    return out.stdout.strip()


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def snapshot(folder):
    """Hashes of every file under a folder, by relative path."""
    result = {}
    for root, _, files in os.walk(folder):
        for name in files:
            full = os.path.join(root, name)
            result[os.path.relpath(full, folder)] = sha256(full)
    return result


def write_config(data_dir, args):
    os.makedirs(data_dir, exist_ok=True)
    with open(os.path.join(data_dir, "config.xml"), "w") as f:
        f.write(f"""<Config>
  <BindAddress>127.0.0.1</BindAddress>
  <Port>{args.port}</Port>
  <ApiKey>{API_KEY}</ApiKey>
  <AuthenticationMethod>External</AuthenticationMethod>
  <AuthenticationRequired>DisabledForLocalAddresses</AuthenticationRequired>
  <AnalyticsEnabled>False</AnalyticsEnabled>
  <LogLevel>debug</LogLevel>
  <PostgresUser>{args.pg_user}</PostgresUser>
  <PostgresPassword>{args.pg_password}</PostgresPassword>
  <PostgresPort>{args.pg_port}</PostgresPort>
  <PostgresHost>{args.pg_host}</PostgresHost>
  <PostgresMainDb>{args.pg_prefix}_main</PostgresMainDb>
  <PostgresLogDb>{args.pg_prefix}_log</PostgresLogDb>
</Config>
""")


def schema_item(radarr, kind, implementation):
    return next(s for s in radarr.get(f"/{kind}/schema") if s["implementation"] == implementation)


def set_fields(item, **values):
    for field in item["fields"]:
        if field["name"] in values:
            field["value"] = values[field["name"]]
    return item


def setup(radarr, args, mock_port, root):
    radarr.call("POST", "/rootfolder", {"path": root})

    indexer = set_fields(schema_item(radarr, "indexer", "Newznab"),
                         baseUrl=f"http://127.0.0.1:{mock_port}/newznab", apiPath="/api", apiKey="e2e", categories=[2000, 2040])
    indexer.update(name="Mock Newznab", enableRss=False, enableAutomaticSearch=True, enableInteractiveSearch=True)
    radarr.call("POST", "/indexer?forceSave=true", indexer)

    client = set_fields(schema_item(radarr, "downloadclient", "Sabnzbd"),
                        host="127.0.0.1", port=mock_port, urlBase="/sab", apiKey="e2e", movieCategory="movies")
    client.update(name="Mock SABnzbd", enable=True)
    radarr.call("POST", "/downloadclient?forceSave=true", client)

    media = radarr.get("/config/mediamanagement")
    media.update(recycleBin=os.path.join(args.work, "recycle"), autoUnmonitorPreviouslyDownloadedMovies=False)
    radarr.call("PUT", f"/config/mediamanagement/{media['id']}", media)

    # One profile for every movie: 720p and 1080p allowed, upgrades until Bluray-1080p
    profile = next(p for p in radarr.get("/qualityprofile") if p["name"] == "HD - 720p/1080p")
    profile.update(upgradeAllowed=True, cutoff=7)
    radarr.call("PUT", f"/qualityprofile/{profile['id']}", profile)

    return profile["id"]


def add_movie(args, radarr, root, title, year, tmdb_id, profile_id):
    """Movies are added straight to the database: adding them through the API would need TMDb."""
    db = f"{args.pg_prefix}_main"
    title_sql = title.replace("'", "''")
    clean = "".join(c for c in title.lower() if c.isalnum())
    metadata_id = psql(args, f"""INSERT INTO "MovieMetadata" ("TmdbId", "Images", "Genres", "Title", "SortTitle", "CleanTitle", "OriginalTitle",
        "CleanOriginalTitle", "OriginalLanguage", "Status", "Runtime", "Year", "Recommendations", "Overview", "Ratings", "Keywords",
        "InCinemas", "PhysicalRelease", "DigitalRelease")
        VALUES ({tmdb_id}, '[]', '[]', '{title_sql}', '{title_sql.lower()}', '{clean}', '{title_sql}', '{clean}', 1, 3, 3, {year}, '[]',
        'e2e', '{{}}', '[]', '{year}-06-01', '{year}-12-01', '{year}-12-01') RETURNING "Id" """, db).splitlines()[0]

    path = os.path.join(root, f"{title} ({year})")
    os.makedirs(path, exist_ok=True)

    movie_id = psql(args, f"""INSERT INTO "Movies" ("Path", "Monitored", "QualityProfileId", "Added", "Tags", "AddOptions", "MovieFileId",
        "MinimumAvailability", "MovieMetadataId", "AutoAddEditions")
        VALUES ('{path.replace("'", "''")}', true, {profile_id}, now(), '[]', '{{}}', 0, 3, {metadata_id}, false) RETURNING "Id" """, db).splitlines()[0]

    psql(args, f"""INSERT INTO "MovieEditions" ("MovieId", "Title", "Key", "IsDefault", "Monitored", "QualityProfileId", "Added")
        VALUES ({movie_id}, NULL, '', true, true, 0, now())""", db)

    return int(movie_id), path


def editions(radarr, movie_id):
    return {e["title"]: e for e in radarr.get(f"/movieedition?movieId={movie_id}")}


def files(radarr, movie_id):
    return radarr.get(f"/moviefile?movieId={movie_id}")


def files_by_edition(radarr, movie_id):
    by_id = {e["id"]: title for title, e in editions(radarr, movie_id).items()}
    result = {}
    for f in files(radarr, movie_id):
        result.setdefault(by_id.get(f["movieEditionId"], "?"), []).append(f)
    return result


def imported_count(radarr):
    return radarr.get("/history?eventType=3&pageSize=1")["totalRecords"]


def wait_for_imports(radarr, before, expected, wait=90):
    """Lets completed download handling run until the expected number of new imports shows up."""
    deadline = time.time() + wait
    while time.time() < deadline:
        radarr.command("RefreshMonitoredDownloads")
        if imported_count(radarr) - before >= expected:
            return True
        time.sleep(1)
    return False


def wait_for_health(radarr, condition, wait=30):
    """Health checks run in the background after a change, so give them a moment."""
    deadline = time.time() + wait
    while True:
        sources = [h["source"] for h in radarr.get("/health")]
        if condition(sources) or time.time() > deadline:
            return sources
        time.sleep(1)


def search_and_import(radarr, state, command, **body):
    """Runs a search command, then waits for everything it grabbed to be imported."""
    grabs_before, imports_before = len(state.grabs), imported_count(radarr)
    radarr.command(command, **body)
    new = state.grabbed_titles()[grabs_before:]

    if new:
        wait_for_imports(radarr, imports_before, len(new))

    return new


def run(args):
    work = os.path.abspath(args.work)
    shutil.rmtree(work, ignore_errors=True)
    for d in ("data", "movies", "complete", "recycle"):
        os.makedirs(os.path.join(work, d))

    root = os.path.join(work, "movies")
    results = Results()

    for suffix in ("main", "log"):
        psql(args, f'DROP DATABASE IF EXISTS "{args.pg_prefix}_{suffix}"')
        psql(args, f'CREATE DATABASE "{args.pg_prefix}_{suffix}"')

    state, server = mocks.start(args.mock_port, os.path.join(work, "complete"))
    write_config(os.path.join(work, "data"), args)

    log = open(os.path.join(work, "radarr.log"), "w")
    proc = subprocess.Popen([args.radarr, "-nobrowser", f"-data={os.path.join(work, 'data')}"],
                            cwd=os.path.dirname(args.radarr), stdout=log, stderr=subprocess.STDOUT)
    radarr = Radarr(args.port)

    try:
        for _ in range(120):
            try:
                radarr.get("/system/status")
                break
            except Exception:
                time.sleep(1)
        else:
            raise RuntimeError("Radarr did not start")

        status = radarr.get("/system/status")
        print(f"Radarr {status['version']} on {status['databaseType']} {status.get('databaseVersion', '')}")
        # Adding the indexer runs a test search that has to return something
        state.publish("Some.Other.Movie.2020.1080p.BluRay.x264-GRP")
        profile_id = setup(radarr, args, args.mock_port, root)

        scenarios = [
            scenario_single_edition,
            scenario_adoption,
            scenario_second_edition,
            scenario_upgrade_within_edition,
            scenario_untracked_edition,
            scenario_auto_add_edition,
            scenario_ambiguous_edition,
            scenario_manual_import,
            scenario_rename_guard,
            scenario_database,
        ]

        context = {"args": args, "radarr": radarr, "state": state, "root": root, "profile_id": profile_id, "work": work}

        for scenario in scenarios:
            results.scenario = scenario.__doc__.strip().splitlines()[0]
            print(f"\n{results.scenario}")
            try:
                scenario(context, results)
            except Exception as e:
                results.check("scenario ran without errors", False, f"{e}\n{traceback.format_exc()}")

        results.scenario = "Logs"
        # Radarr's own services (notifications, update checks, TMDb) aren't reachable from a test run
        offline = ("ServerSideNotificationService", "RadarrAPIClient", "SkyHook", "UpdatePackageProvider", "CheckUpdateService")
        errors = [line for line in open(os.path.join(work, "radarr.log"), errors="replace")
                  if ("|Error|" in line or "[Error]" in line) and not any(o in line for o in offline)]
        results.check("no errors in Radarr's log", not errors, "".join(errors[:20]))
    finally:
        proc.terminate()
        try:
            proc.wait(30)
        except subprocess.TimeoutExpired:
            proc.kill()
        server.shutdown()

    passed = sum(c["passed"] for c in results.checks)
    summary = {"radarr": status if "status" in dir() else None, "passed": passed, "failed": len(results.checks) - passed,
               "checks": results.checks}

    with open(os.path.join(work, "results.json"), "w") as f:
        json.dump(summary, f, indent=2)

    with open(os.path.join(work, "results.md"), "w") as f:
        f.write(f"# Editions end-to-end results\n\n{passed} of {len(results.checks)} checks passed.\n")
        current = None
        for c in results.checks:
            if c["scenario"] != current:
                current = c["scenario"]
                f.write(f"\n## {current}\n\n")
            f.write(f"- {'✅' if c['passed'] else '❌'} {c['check']}\n")

    print(f"\n{passed} of {len(results.checks)} checks passed")
    return 0 if passed == len(results.checks) else 1


# Scenarios share one Radarr instance and run in order, later ones build on earlier state.

def scenario_single_edition(ctx, r):
    """Single-edition movie behaves like stock Radarr"""
    radarr, state = ctx["radarr"], ctx["state"]
    movie_id, path = add_movie(ctx["args"], radarr, ctx["root"], "Arrival", 2016, 329865, ctx["profile_id"])
    ctx["arrival"] = movie_id

    state.publish("Arrival.2016.720p.BluRay.x264-GRP", "Arrival.2016.1080p.BluRay.x264-GRP")
    grabbed = search_and_import(radarr, state, "MoviesSearch", movieIds=[movie_id])

    r.check("best release grabbed once", grabbed == ["Arrival.2016.1080p.BluRay.x264-GRP"], str(grabbed))
    eds = editions(radarr, movie_id)
    r.check("only the default edition exists", list(eds) == ["Standard"], str(list(eds)))
    movie_files = files(radarr, movie_id)
    r.check("one file imported", len(movie_files) == 1, str(len(movie_files)))
    movie = radarr.get(f"/movie/{movie_id}")
    r.check("movie has its file", movie["hasFile"] and movie["movieFileId"] == movie_files[0]["id"] if movie_files else False)

    grabs = [h for h in radarr.get(f"/history/movie?movieId={movie_id}") if h["eventType"] == "grabbed"]
    r.check("grab history records the default edition",
            grabs and grabs[0]["data"].get("movieEditionId") == str(eds["Standard"]["id"]), str(grabs[0]["data"] if grabs else None))

    # Nothing better available, a second search grabs nothing
    grabbed = search_and_import(radarr, state, "MoviesSearch", movieIds=[movie_id])
    r.check("no re-grab when the cutoff is met", grabbed == [], str(grabbed))


def scenario_adoption(ctx, r):
    """An existing folder of Plex-tagged editions is adopted without touching it"""
    radarr = ctx["radarr"]
    movie_id, path = add_movie(ctx["args"], radarr, ctx["root"], "Dune", 2021, 438631, ctx["profile_id"])
    names = ["Theatrical", "Director's Cut", "Final Cut"]
    for name in names:
        mocks.make_video(os.path.join(path, f"Dune (2021) {{edition-{name}}} Bluray-1080p.mkv"), f"dune {name}")
    before = snapshot(path)

    radarr.command("RescanMovie", movieId=movie_id)

    by_edition = files_by_edition(radarr, movie_id)
    r.check("each tagged file in its own edition", all(len(by_edition.get(n, [])) == 1 for n in names),
            str({k: len(v) for k, v in by_edition.items()}))
    r.check("no other edition holds a file", sum(len(v) for v in by_edition.values()) == 3,
            str({k: len(v) for k, v in by_edition.items()}))
    r.check("folder unchanged", snapshot(path) == before)

    # The bulk movie editor monitors or unmonitors the Standard edition
    updated = radarr.call("PUT", "/movie/editor", {"movieIds": [movie_id], "standardEditionMonitored": False}, ok=(200, 202))
    standard = editions(radarr, movie_id)["Standard"]
    returned = [e for e in updated[0].get("editions") or [] if e["isDefault"]]
    r.check("bulk editor unmonitors only the Standard edition",
            not standard["monitored"] and all(e["monitored"] for t, e in editions(radarr, movie_id).items() if t != "Standard")
            and returned and not returned[0]["monitored"],
            str({t: e["monitored"] for t, e in editions(radarr, movie_id).items()}))
    radarr.call("PUT", "/movie/editor", {"movieIds": [movie_id], "standardEditionMonitored": True}, ok=(200, 202))
    r.check("bulk editor monitors the Standard edition again", editions(radarr, movie_id)["Standard"]["monitored"])


def scenario_second_edition(ctx, r):
    """A second edition is grabbed and imported next to the existing file"""
    radarr, state = ctx["radarr"], ctx["state"]
    movie_id, path = add_movie(ctx["args"], radarr, ctx["root"], "Blade Runner", 1982, 78, ctx["profile_id"])
    ctx["blade"], ctx["blade_path"] = movie_id, path

    original = os.path.join(path, "Blade.Runner.1982.1080p.BluRay.x264-ORIG.mkv")
    mocks.make_video(original, "original")
    ctx["original_hash"] = sha256(original)
    radarr.command("RescanMovie", movieId=movie_id)

    r.check("existing file picked up as the default edition", list(files_by_edition(radarr, movie_id)) == ["Standard"],
            str(files_by_edition(radarr, movie_id)))

    state.publish("Blade.Runner.1982.1080p.BluRay.x264-OTHER", "Blade.Runner.1982.The.Final.Cut.720p.BluRay.x264-GRP")
    grabs_before, imports_before = len(state.grabs), imported_count(radarr)

    # Adding the edition with "search" ticked queues an edition search on its own
    radarr.call("POST", "/movieedition", {"movieId": movie_id, "title": "The Final Cut", "monitored": True, "qualityProfileId": 0,
                                          "searchForEdition": True})

    deadline = time.time() + 60
    while time.time() < deadline and len(state.grabs) == grabs_before:
        time.sleep(0.5)

    wait_for_imports(radarr, imports_before, len(state.grabs) - grabs_before)
    grabbed = state.grabbed_titles()[grabs_before:]

    r.check("only the Final Cut release was grabbed", grabbed == ["Blade.Runner.1982.The.Final.Cut.720p.BluRay.x264-GRP"], str(grabbed))
    by_edition = files_by_edition(radarr, movie_id)
    r.check("Standard keeps its one file", len(by_edition.get("Standard", [])) == 1, str(by_edition.keys()))
    r.check("The Final Cut has the new file", len(by_edition.get("The Final Cut", [])) == 1, str(by_edition.keys()))
    r.check("original file untouched", os.path.exists(original) and sha256(original) == ctx["original_hash"])

    queue_history = radarr.get(f"/history/movie?movieId={movie_id}")
    imported = [h for h in queue_history if h["eventType"] == "downloadFolderImported"]
    r.check("import history records The Final Cut", imported and imported[0]["data"].get("movieEditionId") == str(editions(radarr, movie_id)["The Final Cut"]["id"]),
            str(imported[0]["data"] if imported else None))


def scenario_upgrade_within_edition(ctx, r):
    """An upgrade replaces only the file of its own edition"""
    radarr, state, movie_id = ctx["radarr"], ctx["state"], ctx["blade"]
    before = files_by_edition(radarr, movie_id)
    final_cut_old = before["The Final Cut"][0]["path"]
    final_cut_old_hash = sha256(final_cut_old)

    state.publish("Blade.Runner.1982.The.Final.Cut.1080p.BluRay.x264-GRP", "Blade.Runner.1982.1080p.BluRay.x264-NEWER")
    edition_id = editions(radarr, movie_id)["The Final Cut"]["id"]
    grabbed = search_and_import(radarr, state, "MovieEditionSearch", movieId=movie_id, movieEditionId=edition_id)

    r.check("edition search grabbed only the Final Cut upgrade", grabbed == ["Blade.Runner.1982.The.Final.Cut.1080p.BluRay.x264-GRP"], str(grabbed))
    after = files_by_edition(radarr, movie_id)
    r.check("The Final Cut still has one file", len(after.get("The Final Cut", [])) == 1)
    r.check("The Final Cut is now 1080p", after.get("The Final Cut", [{}])[0].get("quality", {}).get("quality", {}).get("resolution") == 1080)
    r.check("Standard file untouched", sha256(os.path.join(ctx["blade_path"], "Blade.Runner.1982.1080p.BluRay.x264-ORIG.mkv")) == ctx["original_hash"])
    recycled = snapshot(os.path.join(ctx["work"], "recycle"))
    r.check("replaced Final Cut file went to the recycle bin", final_cut_old_hash in recycled.values(), str(recycled))

    # A movie-wide search afterwards finds nothing to do for either edition
    grabbed = search_and_import(radarr, state, "MoviesSearch", movieIds=[movie_id])
    r.check("no further grabs once both editions meet the cutoff", grabbed == [], str(grabbed))


def scenario_untracked_edition(ctx, r):
    """A release of an edition the movie doesn't track is left alone"""
    radarr, state, movie_id = ctx["radarr"], ctx["state"], ctx["blade"]
    state.publish("Blade.Runner.1982.Directors.Cut.1080p.BluRay.x264-GRP")

    grabbed = search_and_import(radarr, state, "MoviesSearch", movieIds=[movie_id])
    r.check("not grabbed while auto-add is off", grabbed == [], str(grabbed))
    r.check("no edition created", "Directors Cut" not in editions(radarr, movie_id))

    releases = radarr.get(f"/release?movieId={movie_id}")
    rejections = [x for rel in releases for x in rel.get("rejections", [])]
    # Interactive search lets the user grab a new edition on purpose, so it isn't rejected there
    r.check("interactive search doesn't reject the new edition", not any("not tracked" in x for x in rejections), str(rejections))
    r.check("interactive search shows the release as a new edition", any(rel.get("editionMatch") == "new" for rel in releases),
            str([(rel["title"], rel.get("editionMatch")) for rel in releases]))


def scenario_auto_add_edition(ctx, r):
    """With auto-add on, an unknown edition is grabbed and becomes a new edition"""
    radarr, state, movie_id = ctx["radarr"], ctx["state"], ctx["blade"]
    movie = radarr.get(f"/movie/{movie_id}")
    movie["autoAddEditions"] = True
    radarr.call("PUT", f"/movie/{movie_id}", movie)

    before = snapshot(ctx["blade_path"])
    grabbed = search_and_import(radarr, state, "MoviesSearch", movieIds=[movie_id])

    r.check("Directors Cut grabbed", grabbed == ["Blade.Runner.1982.Directors.Cut.1080p.BluRay.x264-GRP"], str(grabbed))
    eds = editions(radarr, movie_id)
    r.check("Directors Cut edition created", any(t.lower().startswith("directors cut") for t in eds), str(list(eds)))
    after = snapshot(ctx["blade_path"])
    r.check("existing files untouched", all(after.get(k) == v for k, v in before.items()), str(set(before) - set(after)))
    r.check("three editions with one file each", sorted(len(v) for v in files_by_edition(radarr, movie_id).values()) == [1, 1, 1],
            str({k: len(v) for k, v in files_by_edition(radarr, movie_id).items()}))


def scenario_ambiguous_edition(ctx, r):
    """A release naming two cuts is rejected instead of guessed"""
    radarr, state, movie_id = ctx["radarr"], ctx["state"], ctx["blade"]
    state.publish("Blade.Runner.1982.The.Final.Cut.Directors.Cut.2160p.UHD.BluRay.x265-GRP",
                  "Blade.Runner.1982.Final.Cut.Directors.Cut.1080p.WEB-DL.x264-GRP")

    grabbed = search_and_import(radarr, state, "MoviesSearch", movieIds=[movie_id])
    r.check("nothing grabbed", grabbed == [], str(grabbed))
    releases = radarr.get(f"/release?movieId={movie_id}")
    r.check("releases marked ambiguous", releases and all(rel.get("editionMatch") == "ambiguous" for rel in releases),
            str([(rel["title"], rel.get("editionMatch"), rel.get("rejections")) for rel in releases]))


def scenario_manual_import(ctx, r):
    """Manual import puts a file into the edition the user picks"""
    radarr, movie_id = ctx["radarr"], ctx["blade"]
    edition = radarr.call("POST", "/movieedition", {"movieId": movie_id, "title": "Workprint", "monitored": True, "qualityProfileId": 0})
    folder = os.path.join(ctx["work"], "manual")
    source = os.path.join(folder, "blade runner 1982 1080p bluray.mkv")
    mocks.make_video(source, "workprint")
    source_hash = sha256(source)
    before = snapshot(ctx["blade_path"])

    # Like the UI's manual import: browse the folder, then assign the movie and edition per file.
    # (Passing movieId without a downloadId lists the movie's own folder instead.)
    items = radarr.get(f"/manualimport?folder={urllib.parse.quote(folder)}&filterExistingFiles=false")
    r.check("file offered for import", len(items) == 1, str(items))
    if not items:
        return

    item = items[0]
    radarr.command("ManualImport", importMode="copy", files=[{
        "path": item["path"], "movieId": movie_id, "quality": item["quality"], "languages": item["languages"],
        "releaseGroup": item.get("releaseGroup"), "indexerFlags": 0, "movieEditionId": edition["id"]}])

    by_edition = files_by_edition(radarr, movie_id)
    r.check("file imported into Workprint", len(by_edition.get("Workprint", [])) == 1, str({k: len(v) for k, v in by_edition.items()}))
    after = snapshot(ctx["blade_path"])
    r.check("other editions' files untouched", all(after.get(k) == v for k, v in before.items()))
    r.check("source file kept (copy mode)", os.path.exists(source) and sha256(source) == source_hash)


def scenario_rename_guard(ctx, r):
    """Renaming never lets two editions' files collide"""
    radarr, movie_id = ctx["radarr"], ctx["blade"]
    naming = radarr.get("/config/naming")
    naming.update(renameMovies=True, standardMovieFormat="{Movie Title} ({Release Year}) {Quality Full}")
    radarr.call("PUT", f"/config/naming/{naming['id']}", naming)

    hashes_before = sorted(snapshot(ctx["blade_path"]).values())
    health = wait_for_health(radarr, lambda sources: "EditionNamingCheck" in sources)
    r.check("health check warns about the naming format", "EditionNamingCheck" in health, str(health))

    file_ids = [f["id"] for f in files(radarr, movie_id)]
    radarr.command("RenameFiles", movieId=movie_id, files=file_ids)
    hashes_after = sorted(snapshot(ctx["blade_path"]).values())
    r.check("every file still exists with the same content", hashes_before == hashes_after)
    names = [os.path.basename(f["path"]) for f in files(radarr, movie_id)]
    r.check("no two files share a name", len(set(names)) == len(names), str(names))

    naming["standardMovieFormat"] = "{Movie Title} ({Release Year}) {edition-{Edition Tags}} {Quality Full}"
    radarr.call("PUT", f"/config/naming/{naming['id']}", naming)
    radarr.command("RenameFiles", movieId=movie_id, files=file_ids)
    names = sorted(os.path.basename(f["path"]) for f in files(radarr, movie_id))
    r.check("with Edition Tags every file is renamed apart", all("{edition-" in n or n.startswith("Blade Runner (1982)") for n in names)
            and len(set(names)) == len(names), str(names))
    r.check("files still intact after renaming", sorted(snapshot(ctx["blade_path"]).values()) == hashes_before)
    health = wait_for_health(radarr, lambda sources: "EditionNamingCheck" not in sources)
    r.check("naming warning cleared", "EditionNamingCheck" not in health, str(health))


def scenario_database(ctx, r):
    """Database stays consistent on Postgres"""
    args, radarr = ctx["args"], ctx["radarr"]
    db = f"{args.pg_prefix}_main"

    # Radarr's scheduled maintenance must keep every edition's file, not just each movie's main one
    file_count = psql(args, 'SELECT count(*) FROM "MovieFiles"', db)
    before = snapshot(ctx["root"])
    radarr.command("Housekeeping")
    radarr.command("RescanMovie")
    r.check("housekeeping and disk scan keep every edition's file", psql(args, 'SELECT count(*) FROM "MovieFiles"', db) == file_count,
            f"{file_count} before")
    r.check("files on disk untouched by maintenance", snapshot(ctx["root"]) == before)

    orphans = psql(args, 'SELECT count(*) FROM "MovieFiles" f WHERE f."MovieEditionId" <> 0 AND NOT EXISTS '
                         '(SELECT 1 FROM "MovieEditions" e WHERE e."Id" = f."MovieEditionId" AND e."MovieId" = f."MovieId")', db)
    r.check("every file points at an edition of its own movie", orphans == "0", orphans)
    defaults = psql(args, 'SELECT count(*) FROM "Movies" m WHERE (SELECT count(*) FROM "MovieEditions" e '
                          'WHERE e."MovieId" = m."Id" AND e."IsDefault") <> 1', db)
    r.check("every movie has exactly one default edition", defaults == "0", defaults)
    stale = psql(args, 'SELECT count(*) FROM "Movies" m WHERE m."MovieFileId" <> 0 AND NOT EXISTS '
                       '(SELECT 1 FROM "MovieFiles" f WHERE f."Id" = m."MovieFileId" AND f."MovieId" = m."Id")', db)
    r.check("every movie's main file exists", stale == "0", stale)
    versions = psql(args, 'SELECT "Version" FROM "VersionInfo" WHERE "Version" >= 10000', db)
    r.check("fork migrations applied once", versions.splitlines() == ["10000", "10001"], versions)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--radarr", required=True, help="Path to the Radarr executable")
    parser.add_argument("--work", default="/tmp/radarr-e2e")
    parser.add_argument("--port", type=int, default=17979)
    parser.add_argument("--mock-port", type=int, default=17980)
    parser.add_argument("--pg-host", default="localhost")
    parser.add_argument("--pg-port", type=int, default=5432)
    parser.add_argument("--pg-user", default="radarr")
    parser.add_argument("--pg-password", default="radarr")
    parser.add_argument("--pg-prefix", default="radarr_e2e")
    sys.exit(run(parser.parse_args()))
