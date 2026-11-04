#!/usr/bin/env python3
"""Jellyfin plugin repository manifest (repository.json on gh-pages).

  plugin-repository.py generate [--tag TAG] [--dry-run]
      Rebuild the manifest from the GitHub releases and commit it.
      With --tag, wait until that release has its zip and md5, check the
      zip against the md5, and fail rather than publish an entry without
      them. A complete entry is never replaced by an incomplete one, and
      an entry that cannot be completed is left out.
  plugin-repository.py verify [--url URL] [--count N]
      Check the newest N entries the way a Jellyfin server uses them.

Needs GITHUB_TOKEN and GITHUB_REPOSITORY (owner/repo) for generate.
Prints versions, sizes and checksums only.
"""
import argparse, base64, hashlib, io, json, os, re, sys, time, urllib.error, urllib.request, zipfile

import yaml

API = "https://api.github.com"
BRANCH = os.environ.get("PAGES_BRANCH", "gh-pages")
FILE = os.environ.get("PAGES_FILE", "repository.json")


def request(url, accept="application/vnd.github+json", data=None, method=None, token=True):
    headers = {"Accept": accept, "User-Agent": "plugin-repository", "X-GitHub-Api-Version": "2022-11-28"}
    if token and os.environ.get("GITHUB_TOKEN") and url.startswith(API):
        headers["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]
    body = json.dumps(data).encode() if data is not None else None
    if body is not None:
        headers["Content-Type"] = "application/json"
    for attempt in range(5):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, body, headers, method=method), timeout=60) as r:
                return r.status, r.read(), r.headers
        except urllib.error.HTTPError as e:
            if e.code < 500 or attempt == 4:
                return e.code, e.read(), e.headers
        except (urllib.error.URLError, TimeoutError):
            if attempt == 4:
                raise
        time.sleep(5 * 3 ** attempt if attempt < 3 else 45)


def api_json(path):
    status, body, _ = request(API + path)
    if status != 200:
        sys.exit(f"GET {path}: HTTP {status}")
    return json.loads(body)


# The newest releases only, as many as the manifest always listed (the
# action this replaces read one page of 30).
MAX_RELEASES = int(os.environ.get("MAX_RELEASES", "30"))


def recent_releases(repo):
    return api_json(f"/repos/{repo}/releases?per_page={MAX_RELEASES}")


def build_yaml(repo, ref=None):
    path = f"/repos/{repo}/contents/build.yaml" + (f"?ref={ref}" if ref else "")
    return yaml.safe_load(base64.b64decode(api_json(path)["content"]))


def asset_bytes(repo, asset):
    status, body, _ = request(f"{API}/repos/{repo}/releases/assets/{asset['id']}", accept="application/octet-stream")
    if status != 200:
        raise RuntimeError(f"asset {asset['name']}: HTTP {status}")
    return body


def entry_for(repo, release, check_zip=False):
    cfg = build_yaml(repo, release["tag_name"])
    for a in release["assets"]:
        if a["name"] == "build.yaml":
            cfg = yaml.safe_load(asset_bytes(repo, a))
    zips = [a for a in release["assets"] if a["name"].endswith(".zip")]
    md5s = [a for a in release["assets"] if a["name"].endswith(".md5")]
    checksum = asset_bytes(repo, md5s[0])[:32].decode() if md5s else ""
    source = zips[0]["browser_download_url"] if zips else ""
    if check_zip and zips and checksum:
        actual = hashlib.md5(asset_bytes(repo, zips[0])).hexdigest()
        if actual != checksum:
            raise RuntimeError(f"{zips[0]['name']} has md5 {actual}, the .md5 asset says {checksum}")
    return {
        "changelog": release["body"],
        "checksum": checksum,
        "sourceUrl": source,
        "targetAbi": cfg["targetAbi"],
        "timestamp": release["published_at"],
        "version": str(cfg["version"]),
    }


def complete(e):
    return bool(e.get("sourceUrl")) and bool(re.fullmatch(r"[0-9a-f]{32}", e.get("checksum") or ""))


def version_key(v):
    return [int(p) if p.isdigit() else p for p in re.split(r"[.\-]", v)]


def generate(args):
    repo = os.environ["GITHUB_REPOSITORY"]
    for attempt in range(3):
        if args.tag:
            for wait in [0, 10, 20, 30, 60, 60, 120]:
                time.sleep(wait)
                rel = api_json(f"/repos/{repo}/releases/tags/{args.tag}")
                try:
                    e = entry_for(repo, rel, check_zip=True)
                except RuntimeError as err:
                    print(f"{args.tag}: {err}")
                    continue
                if complete(e):
                    print(f"{args.tag}: zip and md5 present, md5 {e['checksum']} matches the zip")
                    break
                print(f"{args.tag}: assets not attached yet, waiting")
            else:
                sys.exit(f"{args.tag}: no complete zip and md5 after waiting; not publishing an entry without them")

        status, body, _ = request(f"{API}/repos/{repo}/contents/{FILE}?ref={BRANCH}")
        current_sha, previous = None, {}
        if status == 200:
            meta = json.loads(body)
            current_sha = meta["sha"]
            old = json.loads(base64.b64decode(meta["content"]))
            previous = {v["version"]: v for v in (old[0]["versions"] if old else [])}

        versions = []
        for rel in recent_releases(repo):
            if rel["draft"] or (rel["prerelease"] and args.ignore_prereleases):
                continue
            e = entry_for(repo, rel)
            if not complete(e):
                kept = previous.get(e["version"])
                if kept and complete(kept):
                    print(f"{e['version']}: assets missing right now, keeping the published entry")
                    e = kept
                else:
                    print(f"{e['version']}: no zip or md5, left out")
                    continue
            versions.append(e)
        versions.sort(key=lambda v: version_key(v["version"]), reverse=True)
        if args.tag and not any(v["timestamp"] and complete(v) for v in versions):
            sys.exit("nothing complete to publish")

        cfg = build_yaml(repo)
        plugin = {k: cfg[k] for k in ("category", "description", "guid", "name", "overview", "owner")}
        if cfg.get("imageUrl"):
            plugin["imageUrl"] = cfg["imageUrl"]
        plugin["versions"] = versions
        text = json.dumps([plugin], indent=2, sort_keys=True, ensure_ascii=False)
        print(f"{len(versions)} versions, newest {versions[0]['version'] if versions else '-'}")
        if args.dry_run:
            sys.stdout.write(text + "\n") if args.print else None
            return
        if status == 200 and base64.b64decode(json.loads(body)["content"]).decode() == text:
            print("repository.json unchanged")
            return
        put = {
            "message": "Regenerate Jellyfin plugin repository.",
            "content": base64.b64encode(text.encode()).decode(),
            "branch": BRANCH,
            "committer": {"name": "github-actions[bot]", "email": "41898282+github-actions[bot]@users.noreply.github.com"},
        }
        if current_sha:
            put["sha"] = current_sha
        st, resp, _ = request(f"{API}/repos/{repo}/contents/{FILE}", data=put, method="PUT")
        if st in (200, 201):
            print("repository.json committed")
            return
        if st in (409, 422):
            print("repository.json changed meanwhile, merging again")
            continue
        sys.exit(f"commit failed: HTTP {st}")
    sys.exit("repository.json kept changing; giving up")


def verify(args):
    status, body, _ = request(args.url + f"?t={int(time.time())}", token=False)
    if status != 200:
        sys.exit(f"{args.url}: HTTP {status}")
    versions = json.loads(body)[0]["versions"][: args.count]
    failed = 0
    for v in versions:
        line = f"{v['version']:<12} abi {v.get('targetAbi', '?'):<9}"
        if not v.get("sourceUrl"):
            print(line + " FAIL sourceUrl empty"); failed += 1; continue
        st, data, _ = request(v["sourceUrl"], accept="application/octet-stream", token=False)
        md5 = hashlib.md5(data).hexdigest() if st == 200 else ""
        meta_version = ""
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as z:
                meta_version = str(json.loads(z.read("meta.json")).get("version", ""))
        except Exception:
            pass
        problems = []
        if st != 200: problems.append(f"HTTP {st}")
        if len(data) <= 100 * 1024: problems.append("size <= 100 KB")
        if md5 != v.get("checksum"): problems.append("md5 differs from checksum")
        # 1.105.1 and 1.105.1.0 are the same version to Jellyfin.
        same = lambda a, b: a.split(".") + ["0"] * (4 - len(a.split("."))) == b.split(".") + ["0"] * (4 - len(b.split(".")))
        if not meta_version or not same(meta_version, v["version"]):
            problems.append(f"meta.json says {meta_version or 'nothing'}")
        print(f"{line} HTTP {st} {len(data):>9} bytes md5 {md5} checksum {v.get('checksum') or '-':<32} meta.json {meta_version or '-':<12} {'OK' if not problems else 'FAIL ' + ', '.join(problems)}")
        failed += bool(problems)
    sys.exit(1 if failed else 0)


p = argparse.ArgumentParser()
sub = p.add_subparsers(dest="cmd", required=True)
g = sub.add_parser("generate")
g.add_argument("--tag")
g.add_argument("--dry-run", action="store_true")
g.add_argument("--print", action="store_true")
g.add_argument("--ignore-prereleases", action="store_true")
v = sub.add_parser("verify")
v.add_argument("--url", default=None)
v.add_argument("--count", type=int, default=5)
a = p.parse_args()
if a.cmd == "verify" and not a.url:
    repo = os.environ.get("GITHUB_REPOSITORY", "")
    a.url = f"https://raw.githubusercontent.com/{repo}/refs/heads/{BRANCH}/{FILE}"
generate(a) if a.cmd == "generate" else verify(a)
