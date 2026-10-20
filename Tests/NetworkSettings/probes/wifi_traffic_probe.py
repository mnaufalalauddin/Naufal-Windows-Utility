"""Read-only Windows DNS/HTTPS probe; no system resolver or default-route fallback."""
import argparse
import datetime
import ipaddress
import json
import secrets
import socket
import ssl
import struct


def emit(**values):
    print(json.dumps({'time': datetime.datetime.now().astimezone().isoformat(), **values}), flush=True)


def bound_socket(family, kind, source, index, source_port=0):
    sock = socket.socket(family, kind)
    try:
        sock.settimeout(8)
        level = socket.IPPROTO_IP if family == socket.AF_INET else socket.IPPROTO_IPV6
        # Windows IP_UNICAST_IF takes network order; IPV6_UNICAST_IF takes host order.
        sock.setsockopt(level, 31, struct.pack('!I' if family == socket.AF_INET else '=I', index))
        if sock.getsockopt(level, 31) != index:
            raise RuntimeError('Outgoing interface readback mismatch')
        scope = index if ipaddress.ip_address(source).is_link_local else 0
        sock.bind((source, source_port) if family == socket.AF_INET else (source, source_port, 0, scope))
        return sock
    except Exception:
        sock.close()
        raise


def skip_name(data, offset):
    for _ in range(128):
        n = data[offset]
        if n & 0xC0 == 0xC0:
            if offset + 1 >= len(data):
                raise ValueError('Truncated DNS pointer')
            return offset + 2
        if n & 0xC0:
            raise ValueError('Invalid DNS label')
        offset += 1
        if n == 0:
            return offset
        offset += n
        if offset >= len(data):
            raise ValueError('Truncated DNS name')
    raise ValueError('DNS name exceeds bounds')


def dns_query(host, qtype, family, source, server, index, source_port=0):
    ident = secrets.randbits(16)
    question = b''.join(bytes([len(x)]) + x.encode('ascii') for x in host.split('.')) + b'\0' + struct.pack('!HH', qtype, 1)
    packet = struct.pack('!6H', ident, 0x100, 1, 0, 0, 0) + question
    with bound_socket(family, socket.SOCK_DGRAM, source, index, source_port) as sock:
        endpoint = (server, 53) if family == socket.AF_INET else (server, 53, 0, index if ipaddress.ip_address(server).is_link_local else 0)
        sock.connect(endpoint)
        if source_port:
            emit(test='DNS_SEND', id=ident, source_port=source_port, host=host, qtype=qtype)
        sock.send(packet)
        data = sock.recv(65535)
        local = sock.getsockname()[0]
    rid, flags, qd, an, _, _ = struct.unpack_from('!6H', data)
    if rid != ident or flags & 0x8000 == 0 or flags & 0x020F or qd != 1:
        raise ValueError('Invalid, truncated or unsuccessful DNS reply')
    end = skip_name(data, 12) + 4
    if data[12:end] != question:
        raise ValueError('DNS question mismatch')
    addresses = []
    for _ in range(an):
        pos = skip_name(data, end)
        kind, cls, _, length = struct.unpack_from('!HHIH', data, pos)
        pos += 10
        end = pos + length
        if end > len(data):
            raise ValueError('Truncated DNS record')
        if kind == qtype and cls == 1 and length == (4 if qtype == 1 else 16):
            addresses.append(str(ipaddress.ip_address(data[pos:end])))
    if not addresses:
        raise ValueError('No address records')
    emit(test='DNS', host=host, query='A' if qtype == 1 else 'AAAA', transport='IPv4' if family == socket.AF_INET else 'IPv6', interface=index, source=local, resolver=server, answers=addresses, result='PASS')
    return addresses


def https(host, address, family, source, index):
    context = ssl.create_default_context()
    with bound_socket(family, socket.SOCK_STREAM, source, index) as raw:
        raw.connect((address, 443) if family == socket.AF_INET else (address, 443, 0, 0))
        with context.wrap_socket(raw, server_hostname=host) as secure:
            secure.sendall(('HEAD / HTTP/1.1\r\nHost: ' + host + '\r\nConnection: close\r\nUser-Agent: NWU-ReadOnly-Network-Test/1.0\r\n\r\n').encode('ascii'))
            response = b''
            while b'\r\n' not in response and len(response) < 8192:
                part = secure.recv(1024)
                if not part:
                    break
                response += part
            status = response.split(b'\r\n')[0].decode('ascii')
            if not status.startswith('HTTP/1.') or not 200 <= int(status.split()[1]) < 400:
                raise ValueError('Unexpected HTTP status: ' + status)
            emit(test='HTTPS', host=host, family='IPv4' if family == socket.AF_INET else 'IPv6', interface=index, source=secure.getsockname()[0], remote=address, tls=secure.version(), certificate_verified=True, status=status, result='PASS')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--index', type=int, required=True)
    for name in ('ipv4', 'ipv6', 'dns4', 'dns6'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--dns6-source', required=True)
    args = parser.parse_args()
    failures = 0
    for host in ('example.com', 'www.microsoft.com'):
        resolved = {}
        for family, source, server in ((socket.AF_INET, args.ipv4, args.dns4), (socket.AF_INET6, args.dns6_source, args.dns6)):
            for qtype in (1, 28):
                try:
                    answers = dns_query(host, qtype, family, source, server, args.index)
                    resolved.setdefault(qtype, answers[0])
                except Exception as exc:
                    failures += 1
                    emit(test='DNS', host=host, transport=family.name, query=qtype, result='FAIL', error=str(exc))
        for qtype, family, source in ((1, socket.AF_INET, args.ipv4), (28, socket.AF_INET6, args.ipv6)):
            try:
                if qtype not in resolved:
                    raise RuntimeError('No Wi-Fi-resolved destination; no default resolver fallback')
                https(host, resolved[qtype], family, source, args.index)
            except Exception as exc:
                failures += 1
                emit(test='HTTPS', host=host, family=family.name, result='FAIL', error=str(exc))
    emit(summary=True, failures=failures, result='PASS' if failures == 0 else 'INCOMPLETE')
    return 0 if failures == 0 else 1


if __name__ == '__main__':
    raise SystemExit(main())
