#!/usr/bin/env python3
"""Sample SOCKS5 process and cgroup resources to JSONL during a Linux load run."""

import glob
import json
import os
import sys
import time
import urllib.request
from datetime import datetime, timezone


def read_proxy_status(port: int) -> dict:
    with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/status", timeout=2) as response:
        status = json.load(response)
    keys = (
        "activeConns", "totalConns", "peakActiveConns", "rejectedConns", "maxActiveSessions",
        "httpActiveRequests", "httpPendingRequests", "httpRejectedRequests", "httpQueueTimeouts",
    )
    return {"status": {key: status.get(key) for key in keys}, "runtime": status.get("runtime") or {},
            "dns": status.get("dns"), "phases": status.get("phases")}


def find_proxy_pid(status: dict, proxy_port: int) -> int | None:
    runtime_pid = status.get("runtime", {}).get("pid")
    if runtime_pid:
        return int(runtime_pid)
    for cmdline_path in glob.glob("/proc/[0-9]*/cmdline"):
        try:
            pid = int(cmdline_path.split("/")[2])
            if pid == os.getpid():
                continue
            arguments = open(cmdline_path, "rb").read().split(bytes([0]))
            if any(
                argument in (b"--listen", b"-listen")
                and index + 1 < len(arguments)
                and arguments[index + 1].rsplit(b":", 1)[-1] == str(proxy_port).encode("ascii")
                for index, argument in enumerate(arguments)
            ):
                return pid
        except (OSError, ValueError):
            pass
    return None


def process_metrics(pid: int | None) -> dict:
    if not pid:
        return {"available": False}
    base = f"/proc/{pid}"
    result = {"pid": pid, "available": True}
    try:
        with open(f"{base}/status", encoding="ascii") as status_file:
            for line in status_file:
                if line.startswith(("VmRSS:", "Threads:")):
                    fields = line.split()
                    result[fields[0].rstrip(":")] = int(fields[1]) * (1024 if fields[0] == "VmRSS:" else 1)
        result["fdCount"] = len(os.listdir(f"{base}/fd"))
        with open(f"{base}/limits", encoding="ascii") as limits_file:
            for line in limits_file:
                if line.startswith("Max open files"):
                    fields = line.split()
                    result["nofileSoft"], result["nofileHard"] = fields[3], fields[4]
                    break
    except (OSError, ValueError, IndexError) as error:
        result["readError"] = type(error).__name__
    return result


def cgroup_metrics() -> dict:
    result = {}
    for name in ("cpu.max", "cpu.stat", "memory.current", "memory.max", "memory.events", "pids.current", "pids.max"):
        try:
            result[name] = open(f"/sys/fs/cgroup/{name}", encoding="ascii").read().strip()
        except OSError:
            pass
    return result


def main() -> None:
    if len(sys.argv) != 6:
        raise SystemExit("usage: sampler.py <metrics-port> <proxy-port> <output> <stop-file> <interval-seconds>")
    metrics_port, proxy_port = int(sys.argv[1]), int(sys.argv[2])
    output_path, stop_path, interval = sys.argv[3], sys.argv[4], float(sys.argv[5])
    while not os.path.exists(stop_path):
        try:
            data = read_proxy_status(metrics_port)
            pid = find_proxy_pid(data, proxy_port)
            record = {
                "timeUtc": datetime.now(timezone.utc).isoformat(),
                **data,
                "process": process_metrics(pid),
                "cgroup": cgroup_metrics(),
            }
        except Exception as error:  # Keep the sampler alive and make a missing sample visible.
            record = {"timeUtc": datetime.now(timezone.utc).isoformat(), "sampleError": type(error).__name__}
        with open(output_path, "a", encoding="utf-8") as output:
            output.write(json.dumps(record, separators=(",", ":")) + "\n")
        time.sleep(interval)


if __name__ == "__main__":
    main()
