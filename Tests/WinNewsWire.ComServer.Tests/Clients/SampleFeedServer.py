import argparse
import datetime
import html
import http.server
import json
import pathlib
import threading


parser = argparse.ArgumentParser()
parser.add_argument("--output", required=True, type=pathlib.Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
log_lock = threading.Lock()


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        with log_lock:
            with (args.output / "http-requests.jsonl").open("a", encoding="utf-8") as log:
                log.write(json.dumps({"path": self.path, "time": datetime.datetime.now(datetime.timezone.utc).isoformat()}) + "\n")
        status = 200
        kind = "text/html; charset=utf-8"
        body = "<html><head><title>COM reading-list sample</title></head><body><p>Local sample publication.</p></body></html>"
        if self.path.startswith("/rss"):
            version_path = args.output / "feed-version.txt"
            version = int(version_path.read_text()) if version_path.exists() else 1
            count = 320 if version == 1 else 321
            kind = "application/rss+xml"
            items = []
            now = datetime.datetime(2026, 11, 18, 12, 0, tzinfo=datetime.timezone.utc)
            for index in range(count):
                date = (now - datetime.timedelta(minutes=index)).strftime("%a, %d %b %Y %H:%M:%S GMT")
                text = html.escape(f"<p>Sample article {index}, revision {version}. " + ("Reading-list sample text. " * 120) + "</p>")
                items.append(f"<item><guid isPermaLink='false'>sample-{index}</guid>"
                             f"<title>Sample Article {index}</title><link>{base}/article/{index}</link>"
                             f"<pubDate>{date}</pubDate><description>{text}</description></item>")
            body = (f"<?xml version='1.0'?><rss version='2.0'><channel><title>COM Sample Publication</title>"
                    f"<link>{base}/</link><description>Real RSS provider scenario</description>"
                    + "".join(items) + "</channel></rss>")
        elif self.path.startswith("/broken"):
            status, body = 503, "Deliberate sample feed failure"
        elif self.path.startswith("/favicon"):
            status, body = 404, ""
        encoded = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(encoded)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(encoded)

    def log_message(self, *_):
        pass


server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
base = f"http://127.0.0.1:{server.server_port}"
(args.output / "feed-server.json").write_text(json.dumps({"url": base}), encoding="utf-8")
print(base, flush=True)
server.serve_forever()
