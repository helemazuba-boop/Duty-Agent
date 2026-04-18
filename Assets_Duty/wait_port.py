"""Wait for a TCP port to become available, then exit."""
import sys
import time
import socket

if len(sys.argv) < 2:
    print("Usage: wait_port.py <port>")
    sys.exit(1)

port = int(sys.argv[1])
timeout = float(sys.argv[2]) if len(sys.argv) > 2 else 30
deadline = time.monotonic() + timeout

# Vite dev server listens on IPv6 (::1) by default on Windows
targets = [("::1", port), ("127.0.0.1", port)]

while time.monotonic() < deadline:
    for host, _ in targets:
        try:
            family = socket.AF_INET6 if ":" in host else socket.AF_INET
            s = socket.socket(family, socket.SOCK_STREAM)
            s.settimeout(1)
            s.connect((host, port))
            s.close()
            print("READY")
            sys.exit(0)
        except (OSError, socket.error):
            pass
    time.sleep(0.5)

print("TIMEOUT")
sys.exit(1)
