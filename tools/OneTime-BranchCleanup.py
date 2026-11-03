#!/usr/bin/env python3
"""One-time, checksum-backed branch cleanup for the verified LaunchFlow branch."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request

REPO = "KamiBuilds/LaunchFlow"
SURVIVOR = "validated/v0.7.46-three-checks-20261103"
KEEP = {"main", SURVIVOR}
MANIFEST = Path("launchflow-precleanup-branches.json")
BUNDLE = Path("launchflow-precleanup-history.bundle")
SUMS = Path("launchflow-precleanup-history.sha256")


def api(method, path):
    token = os.environ.get("GH_TOKEN")
    if not token:
        raise RuntimeError("GITHUB_TOKEN was not provided")
    uri = "https://api.github.com/repos/" + REPO + "/" + path
    request = urllib.request.Request(uri, method=method, headers={
        "Authorization": "Bearer " + token,
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "launchflow-one-time-branch-cleanup",
    })
    with urllib.request.urlopen(request, timeout=30) as response:
        body = response.read()
    return json.loads(body) if body else None


def branches():
    result = []
    page = 1
    while True:
        batch = api("GET", f"branches?per_page=100&page={page}")
        if not isinstance(batch, list):
            raise RuntimeError("Could not enumerate branches")
        result.extend({
            "name": item["name"],
            "sha": item["commit"]["sha"],
            "protected": bool(item["protected"]),
        } for item in batch)
        if len(batch) < 100:
            break
        page += 1
    if len({b["name"] for b in result}) != len(result):
        raise RuntimeError("Duplicate branch names in API result")
    return sorted(result, key=lambda b: b["name"])


def ref(name):
    encoded = urllib.parse.quote(name, safe="/")
    data = api("GET", "git/ref/heads/" + encoded)
    return data["object"]["sha"]


def safe_start():
    if os.environ.get("GITHUB_REPOSITORY") != REPO:
        raise RuntimeError("Wrong repository; refusing cleanup")
    if os.environ.get("GITHUB_EVENT_NAME") != "push":
        raise RuntimeError("Only an explicit branch push may trigger cleanup")
    if os.environ.get("GITHUB_REF") != "refs/heads/" + SURVIVOR:
        raise RuntimeError("Wrong branch; refusing cleanup")
    sha = os.environ.get("GITHUB_SHA", "")
    if len(sha) != 40 or ref(SURVIVOR) != sha:
        raise RuntimeError("Validated branch advanced since tested run")
    return sha


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as file:
        while block := file.read(1024 * 1024):
            h.update(block)
    return h.hexdigest()


def snapshot():
    sha = safe_start()
    current = branches()
    if set(KEEP) - {b["name"] for b in current}:
        raise RuntimeError("Required surviving branch is missing")
    if any(b["protected"] and b["name"] not in KEEP for b in current):
        raise RuntimeError("An obsolete branch is protected; abort before deletion")
    if any(ref(b["name"]) != b["sha"] for b in current):
        raise RuntimeError("Branch head changed during initial enumeration")
    subprocess.run([
        "git", "fetch", "--force", "origin",
        "+refs/heads/*:refs/remotes/origin/*",
    ], check=True)
    for b in current:
        local = subprocess.check_output([
            "git", "rev-parse", "refs/remotes/origin/" + b["name"],
        ], text=True).strip()
        if local != b["sha"]:
            raise RuntimeError("Local backup did not fetch " + b["name"])
    subprocess.run(["git", "bundle", "create", str(BUNDLE), "--all"], check=True)
    subprocess.run(["git", "bundle", "verify", str(BUNDLE)], check=True)
    bundle_hash = digest(BUNDLE)
    SUMS.write_text(bundle_hash + "  " + BUNDLE.name + "\n", encoding="ascii")
    payload = {
        "repository": REPO,
        "verifiedSha": sha,
        "survivor": SURVIVOR,
        "mainSha": next(b["sha"] for b in current if b["name"] == "main"),
        "bundleSha256": bundle_hash,
        "branchCount": len(current),
        "branches": current,
    }
    MANIFEST.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print("Archived", len(current), "branch refs in bundle SHA-256", bundle_hash)
    print("Deletion is gated until the archive artifact upload succeeds.")


def cleanup():
    sha = safe_start()
    data = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if data["repository"] != REPO or data["survivor"] != SURVIVOR or data["verifiedSha"] != sha:
        raise RuntimeError("Manifest is not bound to the current tested commit")
    if digest(BUNDLE) != data["bundleSha256"]:
        raise RuntimeError("Pre-cleanup backup bundle has changed")
    existing = branches()
    if existing != data["branches"]:
        raise RuntimeError("Branch state changed after snapshot; refusing deletion")
    obsolete = [b for b in existing if b["name"] not in KEEP]
    if not obsolete:
        raise RuntimeError("No obsolete branches found; refusing unexpected repeated cleanup")
    deleted = []
    for item in obsolete:
        name = item["name"]
        if name in KEEP or item["protected"]:
            raise RuntimeError("Protected branch in delete set")
        if ref(name) != item["sha"]:
            raise RuntimeError("Concurrent change to " + name + "; stop before deletion")
        api("DELETE", "git/refs/heads/" + urllib.parse.quote(name, safe="/"))
        deleted.append(name)
        print("Deleted:", name)
    left = branches()
    if {b["name"] for b in left} != KEEP or len(left) != len(KEEP):
        raise RuntimeError("Unexpected remaining branch inventory: " + json.dumps(left))
    if ref("main") != data["mainSha"] or ref(SURVIVOR) != sha:
        raise RuntimeError("A preserved branch head changed unexpectedly")
    result = {
        "deletedCount": len(deleted),
        "deleted": deleted,
        "retained": {item["name"]: item["sha"] for item in left},
        "archiveSha256": data["bundleSha256"],
    }
    Path("launchflow-branch-cleanup-result.json").write_text(
        json.dumps(result, indent=2) + "\n", encoding="utf-8"
    )
    print("Verified cleanup: retained only main and " + SURVIVOR)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as output:
            output.write(
                f"Deleted {len(deleted)} obsolete branches after archiving and "
                f"verifying all three gated checks. Preserved main and {SURVIVOR}.\n"
            )


if __name__ == "__main__":
    if len(sys.argv) != 2 or sys.argv[1] not in {"snapshot", "delete"}:
        raise SystemExit("Usage: branch_cleanup.py snapshot|delete")
    if sys.argv[1] == "snapshot":
        snapshot()
    else:
        cleanup()
