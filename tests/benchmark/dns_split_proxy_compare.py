#!/usr/bin/env python3
"""Compare two loopback proxy instances without logging target or DNS addresses."""

import base64
import concurrent.futures
import hashlib
import http.client
import json
import os
import statistics
import sys
import time
import urllib.parse
import urllib.request


def parse_target(raw):
    target = urllib.parse.urlsplit(raw)
    try:
        port = target.port
    except ValueError as error:
        raise ValueError("invalid target port") from error
    if target.scheme != "https" or not target.hostname or target.username or target.password or port not in (None, 443):
        raise ValueError("target must be HTTPS on port 443 without URL credentials")
    return target


def summarize_results(rows):
    groups = {}
    for label in ("default", "split"):
        group = [row for row in rows if row["group"] == label]
        done = [row for row in group if row.get("complete") and row.get("status") == 200]
        groups[label] = {
            "attempts": len(group),
            "complete": len(done),
            "medianMs": statistics.median(row["elapsedMs"] for row in done) if done else None,
            "maxMs": max((row["elapsedMs"] for row in done), default=None),
            "partialBytes": [row["bytes"] for row in group if not row.get("complete")],
        }
    hashes = {row["sha256"] for row in rows if row.get("complete") and row.get("status") == 200}
    return {"groups": groups, "allHashesEqual": len(hashes) == 1 if hashes else None}


def failed_gate(rounds, allow_incomplete):
    for row in rounds:
        for phase in ("before", "after"):
            status = row[phase]
            if status["default"].get("rules") != 0 or not isinstance(status["split"].get("rules"), int) or status["split"]["rules"] < 1:
                return True
        summary = row["summary"]
        if summary["allHashesEqual"] is False:
            return True
        if not allow_incomplete and (summary["allHashesEqual"] is not True or any(group["complete"] != group["attempts"] for group in summary["groups"].values())):
            return True
    return False


def snapshot(port):
    try:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(f"http://127.0.0.1:{port + 1000}/api/status", timeout=2) as response:
            status = json.load(response)
        dns = status.get("dns") or {}
        return {
            "active": status.get("activeConns"), "rejected": status.get("rejectedConns"),
            "rules": status.get("dnsRuleCount"),
            "dns": {key: dns.get(key) for key in ("hits", "misses", "staleHits", "evictions", "cacheSize")},
        }
    except Exception as error:
        return {"error": type(error).__name__}


def fetch(item, target, timeout, max_bytes):
    label, port = item
    start = time.monotonic()
    result = {"group": label, "bytes": 0, "complete": False}
    digest = hashlib.sha256()
    conn = http.client.HTTPSConnection("127.0.0.1", port, timeout=min(timeout, 5))
    conn.set_tunnel(target.hostname, 443)
    request_path = urllib.parse.urlunsplit(("", "", target.path or "/", target.query, ""))
    try:
        conn.request("GET", request_path, headers={"Host": target.hostname, "Connection": "close", "Accept-Encoding": "identity"})
        response = conn.getresponse()
        result["status"] = response.status
        result["headersMs"] = round((time.monotonic() - start) * 1000)
        expected = response.length
        while time.monotonic() - start < timeout:
            data = response.read1(32768)
            if not data:
                result["complete"] = expected is None or result["bytes"] == expected
                if result["complete"]:
                    result["sha256"] = digest.hexdigest()
                else:
                    result["error"] = "incomplete_body"
                break
            result["bytes"] += len(data)
            digest.update(data)
            if result["bytes"] > max_bytes:
                result["error"] = "byte_limit"
                break
        if not result["complete"] and "error" not in result:
            result["error"] = "deadline"
    except Exception as error:
        result["error"] = type(error).__name__
    finally:
        conn.close()
    result["elapsedMs"] = round((time.monotonic() - start) * 1000)
    return result


def bounded_int(name, default, minimum, maximum):
    value = int(os.environ.get(name, default))
    if not minimum <= value <= maximum:
        raise ValueError(f"invalid {name}")
    return value


def main():
    raw = os.environ.get("DNS_AB_URL", "")
    if os.environ.get("DNS_AB_URL_B64"):
        raw = base64.b64decode(os.environ["DNS_AB_URL_B64"], validate=True).decode("utf-8")
    target = parse_target(raw)
    default_port = bounded_int("DNS_AB_DEFAULT_PORT", 0, 1, 64535)
    split_port = bounded_int("DNS_AB_SPLIT_PORT", 0, 1, 64535)
    if default_port == split_port:
        raise ValueError("proxy ports must differ")
    concurrency = bounded_int("DNS_AB_CONNECTIONS", 5, 1, 20)
    repeats = bounded_int("DNS_AB_REPEATS", 2, 1, 5)
    timeout = bounded_int("DNS_AB_TIMEOUT_SECONDS", 15, 1, 60)
    max_bytes = bounded_int("DNS_AB_MAX_BYTES", 8 * 1024 * 1024, 1, 32 * 1024 * 1024)
    ports = {"default": default_port, "split": split_port}
    rounds = []
    for number in range(repeats):
        before = {label: snapshot(port) for label, port in ports.items()}
        order = list(ports.items()) if number % 2 == 0 else list(reversed(list(ports.items())))
        jobs = [item for _ in range(concurrency) for item in order]
        with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency * 2) as pool:
            rows = list(pool.map(lambda item: fetch(item, target, timeout, max_bytes), jobs))
        after = {label: snapshot(port) for label, port in ports.items()}
        rounds.append({"round": number, "before": before, "after": after, "summary": summarize_results(rows), "samples": rows})
    print(json.dumps({"method": "loopback-proxy-https-connect", "rounds": rounds}, separators=(",", ":")))
    if failed_gate(rounds, os.environ.get("DNS_AB_ALLOW_INCOMPLETE") == "1"):
        return 1
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, UnicodeError) as error:
        print(type(error).__name__ + ": invalid benchmark configuration", file=sys.stderr)
        sys.exit(2)
