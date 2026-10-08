"""A minimal Remote Access peer that connects exactly as NVDA's TCPTransport/RelayTransport does.

Usage: nvda_peer.py HOST PORT KEY MODE
Prints every received message as a JSON line on stdout. Reads JSON lines from stdin and sends them.
Exits when stdin closes.
"""

import json
import select
import socket
import ssl
import sys
import threading


def main():
	host, port, key, mode = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
	address = socket.getaddrinfo(host, port)[0]
	raw = socket.socket(*address[:3])
	raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
	# NVDA's client: TLS 1.2 only; here verification is skipped as NVDA does for self-hosted servers.
	ctx = ssl.SSLContext(ssl.PROTOCOL_TLSv1_2)
	ctx.check_hostname = False
	ctx.verify_mode = ssl.CERT_NONE
	sock = ctx.wrap_socket(raw, server_hostname=host)
	sock.connect(address[4])
	lock = threading.Lock()

	def send(obj):
		with lock:
			sock.sendall(json.dumps(obj).encode("utf-8") + b"\n")

	send({"version": 2, "type": "protocol_version"})
	send({"channel": key, "connection_type": mode, "type": "join"})
	print(json.dumps({"type": "__connected", "cipher": sock.version()}), flush=True)

	def pump_stdin():
		for line in sys.stdin:
			line = line.strip()
			if line:
				send(json.loads(line))
		sock.close()

	threading.Thread(target=pump_stdin, daemon=True).start()
	buffer = b""
	while True:
		try:
			readers, _, _ = select.select([sock], [], [], 0.5)
		except (OSError, ValueError):
			return
		if not readers:
			continue
		try:
			data = sock.recv(16384)
		except ssl.SSLWantReadError:
			continue
		except OSError:
			return
		if not data:
			return
		buffer += data
		while b"\n" in buffer:
			line, _, buffer = buffer.partition(b"\n")
			print(line.decode("utf-8"), flush=True)


if __name__ == "__main__":
	main()
