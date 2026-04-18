"""Start the backend, capture its token, write it to a file, then stay running."""
import subprocess
import sys
import time

if len(sys.argv) < 4:
    print("Usage: start_backend.py <python> <core_py> <token_file>")
    sys.exit(1)

python_exe = sys.argv[1]
core_py = sys.argv[2]
token_file = sys.argv[3]

process = subprocess.Popen(
    [python_exe, core_py, "--server", "--port", "8765"],
    stdout=subprocess.PIPE,
    stderr=subprocess.STDOUT,
    stdin=subprocess.DEVNULL,
    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    text=True,
    bufsize=1,
)

token = None
port_seen = False
deadline = time.monotonic() + 30

print(f"[start_backend] launched, pid={process.pid}")

while time.monotonic() < deadline:
    line = process.stdout.readline()
    if not line:
        rc = process.poll()
        if rc is not None:
            print(f"[start_backend] backend exited early, rc={rc}")
            sys.exit(1)
        time.sleep(0.05)
        continue

    stripped = line.rstrip("\n\r")
    if stripped:
        print(f"[start_backend] {stripped}")

    if stripped.startswith("__DUTY_SERVER_PORT__:"):
        port_seen = True
    if stripped.startswith("__DUTY_SERVER_TOKEN__:"):
        token = stripped.split(":", 1)[1].strip()

    # Wait for BOTH port and token before continuing
    if port_seen and token is not None:
        break

if token:
    try:
        with open(token_file, "w", encoding="utf-8") as f:
            f.write(token)
        print(f"[start_backend] token written to {token_file}")
    except Exception as e:
        print(f"[start_backend] write token failed: {e}")
else:
    print("[start_backend] WARNING: no token captured!")

print("[start_backend] backend running, waiting for Ctrl+C...")
process.wait()
