#!/usr/bin/env python3
"""Two stand-in network printers for the local till: the kitchen's on 127.0.0.1:9101, the
receipts' on 127.0.0.1:9102. They take what the till sends (ESC/POS over TCP, as a real
thermal printer does), and show each ticket as plain text here and in dev/printouts/.

    python3 dev/fake-printer.py

Stop with Ctrl+C. See "Printers" in README.md.
"""

import datetime
import os
import socket
import sys
import threading

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "printouts")
PRINTERS = {"kitchen": 9101, "receipt": 9102}

# The commands the till sends (EscPosDocument): ESC or GS, a command letter, and one parameter,
# except ESC @ (reset), which has none.
ESC, GS = 0x1B, 0x1D
HTTP_METHODS = {b"GET", b"HEAD", b"POST", b"OPTIONS"}
LOCK = threading.Lock()


def readable(data: bytes) -> str:
    text = bytearray()
    i = 0
    while i < len(data):
        byte = data[i]
        if byte in (ESC, GS):
            i += 2 if data[i + 1:i + 2] == b"@" else 3
            continue
        if byte == 0x0A or byte >= 0x20:
            text.append(byte)
        i += 1
    return text.decode("cp437", errors="replace").rstrip()


def serve(name: str, port: int) -> None:
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", port))
    listener.listen()
    while True:
        connection, _ = listener.accept()
        # Each in its own thread: a connection left open and silent (the Claude app checking the
        # port is up) must not hold up the till's tickets behind it.
        threading.Thread(target=take, args=(name, connection), daemon=True).start()


def take(name: str, connection: socket.socket) -> None:
    connection.settimeout(10)
    chunks = []
    with connection:
        try:
            while chunk := connection.recv(4096):
                chunks.append(chunk)
        except (socket.timeout, OSError):
            pass
    data = b"".join(chunks)
    # Nothing sent, or a browser looking at the port: not a print job.
    if not data or data.split(b" ", 1)[0] in HTTP_METHODS:
        return
    stamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    ticket = f"===== {name.upper()} {stamp} =====\n{readable(data)}\n"
    with LOCK:
        print(ticket, flush=True)
        with open(os.path.join(OUT, f"{name}.txt"), "a", encoding="utf-8") as printout:
            printout.write(ticket + "\n")


def main() -> None:
    os.makedirs(OUT, exist_ok=True)
    for name, port in PRINTERS.items():
        threading.Thread(target=serve, args=(name, port), daemon=True).start()
        print(f"{name} printer listening on 127.0.0.1:{port}", flush=True)
    try:
        threading.Event().wait()
    except KeyboardInterrupt:
        sys.exit(0)


if __name__ == "__main__":
    main()
