"""Mock Newznab indexer and SABnzbd download client for the editions end-to-end tests.

The indexer returns whatever releases the test has published. The download client "finishes" every
download straight away by writing a short but valid video file named after the release, so Radarr's
completed download handling imports it like a real one.
"""

import email.utils
import itertools
import json
import os
import re
import subprocess
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from xml.sax.saxutils import escape

CAPS = """<?xml version="1.0" encoding="UTF-8"?>
<caps>
  <server title="e2e" />
  <limits max="100" default="100" />
  <searching>
    <search available="yes" supportedParams="q" />
    <movie-search available="yes" supportedParams="q,imdbid,tmdbid" />
  </searching>
  <categories>
    <category id="2000" name="Movies">
      <subcat id="2040" name="HD" />
    </category>
  </categories>
</caps>"""


class MockState:
    def __init__(self, complete_dir):
        self.complete_dir = complete_dir
        self.releases = []
        self.grabs = []
        self.history = []
        self.lock = threading.Lock()
        self.ids = itertools.count(1)

    def publish(self, *titles):
        """Replace the indexer's results with these release titles."""
        with self.lock:
            self.releases = [{"id": next(self.ids), "title": t} for t in titles]

    def grabbed_titles(self):
        with self.lock:
            return [g["title"] for g in self.grabs]


def make_video(path, title):
    """A 20 second video with sound, long enough not to count as a sample for a short movie.

    The resolution follows the release name so Radarr's media info agrees with the parsed quality,
    and the title tag makes every file's content unique."""
    height = 2160 if "2160p" in title else 720 if "720p" in title else 1080
    width = height * 16 // 9
    os.makedirs(os.path.dirname(path), exist_ok=True)
    subprocess.run(
        ["ffmpeg", "-loglevel", "error", "-y",
         "-f", "lavfi", "-i", f"color=black:s={width}x{height}:r=1",
         "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono",
         "-t", "20", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-b:a", "8k",
         "-metadata", f"comment={title}", path],
        check=True)


class Handler(BaseHTTPRequestHandler):
    state = None

    def log_message(self, *args):
        pass

    def send(self, body, content_type="application/json", status=200):
        data = body.encode() if isinstance(body, str) else body
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        url = urllib.parse.urlparse(self.path)
        query = dict(urllib.parse.parse_qsl(url.query))

        if url.path.startswith("/newznab"):
            return self.newznab(url.path, query)
        if url.path.startswith("/sab"):
            return self.sabnzbd(query, None)

        self.send("not found", "text/plain", 404)

    def do_POST(self):
        url = urllib.parse.urlparse(self.path)
        query = dict(urllib.parse.parse_qsl(url.query))
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))

        if url.path.startswith("/sab"):
            return self.sabnzbd(query, body)

        self.send("not found", "text/plain", 404)

    def newznab(self, path, query):
        if path.endswith(".nzb"):
            nzb = '<?xml version="1.0"?><nzb xmlns="http://www.newzbin.com/DTD/2003/nzb"><file subject="e2e"><segments><segment number="1">e2e@e2e</segment></segments></file></nzb>'
            return self.send(nzb, "application/x-nzb")

        if query.get("t") == "caps":
            return self.send(CAPS, "application/xml")

        base = f"http://{self.headers['Host']}/newznab"
        items = []

        with self.state.lock:
            releases = list(self.state.releases)

        for r in releases:
            items.append(f"""<item>
  <title>{escape(r['title'])}</title>
  <guid isPermaLink="true">{base}/details/{r['id']}</guid>
  <link>{base}/get/{r['id']}.nzb</link>
  <comments>{base}/details/{r['id']}</comments>
  <pubDate>{email.utils.formatdate(time.time() - 3600)}</pubDate>
  <size>209715200</size>
  <enclosure url="{base}/get/{r['id']}.nzb" length="209715200" type="application/x-nzb" />
  <newznab:attr name="category" value="2040" />
  <newznab:attr name="size" value="209715200" />
</item>""")

        rss = f"""<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
<channel><title>e2e</title>
<newznab:response offset="0" total="{len(items)}" />
{''.join(items)}
</channel></rss>"""
        self.send(rss, "application/rss+xml")

    def sabnzbd(self, query, body):
        mode = query.get("mode")
        state = self.state

        if mode == "version":
            return self.send(json.dumps({"version": "4.3.3"}))

        if mode == "get_config":
            return self.send(json.dumps({"config": {
                "misc": {"complete_dir": state.complete_dir, "tv_categories": [], "movie_categories": [], "date_categories": [],
                         "enable_tv_sorting": False, "enable_movie_sorting": False, "enable_date_sorting": False,
                         "pre_check": False, "history_retention": "", "history_retention_option": "all", "history_retention_number": 0},
                "categories": [{"name": "*", "dir": "", "priority": 0, "pp": "3", "script": "None"},
                               {"name": "movies", "dir": "", "priority": -100, "pp": "", "script": "Default"}],
                "servers": [], "sorters": []}}))

        if mode == "fullstatus":
            return self.send(json.dumps({"status": {"completedir": state.complete_dir}}))

        if mode == "queue":
            return self.send(json.dumps({"queue": {"paused": False, "my_home": state.complete_dir, "slots": []}}))

        if mode == "history":
            if query.get("name") == "delete":
                with state.lock:
                    state.history = [h for h in state.history if h["nzo_id"] != query.get("value")]
                return self.send(json.dumps({"status": True}))

            with state.lock:
                slots = list(state.history)
            return self.send(json.dumps({"history": {"paused": False, "slots": slots}}))

        if mode == "addfile":
            match = re.search(rb'filename="([^"]+)"', body or b"")
            title = match.group(1).decode() if match else "unknown"
            title = re.sub(r"\.nzb$", "", title)
            nzo_id = f"SABnzbd_nzo_e2e{next(state.ids)}"
            folder = os.path.join(state.complete_dir, title)
            make_video(os.path.join(folder, title + ".mkv"), title)

            with state.lock:
                state.grabs.append({"nzo_id": nzo_id, "title": title})
                state.history.append({
                    "nzo_id": nzo_id, "name": title, "nzb_name": title + ".nzb", "category": "movies",
                    "bytes": os.path.getsize(os.path.join(folder, title + ".mkv")), "download_time": 1,
                    "storage": folder, "status": "Completed", "fail_message": ""})

            return self.send(json.dumps({"status": True, "nzo_ids": [nzo_id]}))

        self.send(json.dumps({"status": True}))


def start(port, complete_dir):
    state = MockState(complete_dir)
    handler = type("BoundHandler", (Handler,), {"state": state})
    server = ThreadingHTTPServer(("127.0.0.1", port), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return state, server
