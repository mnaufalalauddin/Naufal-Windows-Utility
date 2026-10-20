"""Explicitly authorized DNS comparison, Wi-Fi-bound; no settings changes."""
import argparse
import socket
import time
from wifi_traffic_probe import dns_query, emit

p = argparse.ArgumentParser()
p.add_argument('--source', required=True)
p.add_argument('--index', type=int, required=True)
a = p.parse_args()
failures = 0
for server in ('2606:4700:4700::1111', '2606:4700:4700::1001'):
    for host in ('example.com', 'www.microsoft.com'):
        for qtype in (1, 28):
            start = time.monotonic()
            try:
                dns_query(host, qtype, socket.AF_INET6, a.source, server, a.index)
                emit(test='CLOUDFLARE', result='PASS', resolver=server, seconds=round(time.monotonic()-start, 3))
            except Exception as e:
                failures += 1
                emit(test='CLOUDFLARE', result='FAIL', resolver=server, host=host, qtype=qtype, error=str(e), seconds=round(time.monotonic()-start, 3))
emit(summary=True, failures=failures, result='PASS' if failures == 0 else 'INCOMPLETE')
raise SystemExit(1 if failures else 0)
