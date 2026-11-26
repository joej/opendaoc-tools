#!/usr/bin/env python3
"""Send one request to the OpenDAoC admin host and print the answer (server-scripts/admin-host/AdminHost.cs).

Run it as the server's user, on the server's machine; the socket is mode 0600.

    adminctl.py server.status
    adminctl.py player.list
    adminctl.py player.get name=LabChar
    adminctl.py player.spec.set name=LabChar spec=Slash level=5
    adminctl.py player.spec.set name=LabChar spec=Slash level=5 queue=true   # offline: at next login
    adminctl.py player.move name=LabChar region=1 x=560000 y=511000 z=2500 heading=2048   # queue=true too
    adminctl.py player.money.set name=LabChar platinum=5                                   # absolute; queue=true too
    adminctl.py player.item.set name=LabChar id=adder_venom count=20                       # backpack count; queue=true too
    adminctl.py player.level.set name=LabChar level=20                                     # lowering respecs; queue=true too
    adminctl.py player.revive name=LabChar clearPenalty=true                               # online only
    adminctl.py spawn.boat near=LabChar                                                    # also spawn.npc, spawn.ram size=light
    adminctl.py spawn.board id=s-1a2b3c4d name=LabChar
    adminctl.py spawn.path id=s-1a2b3c4d points="560600,511000,2500;560600,511600,2500" speed=300
    adminctl.py spawn.list
    adminctl.py spawn.delete all=true
    adminctl.py player.pending
    adminctl.py player.pending.cancel id=p-1a2b3c4d
    adminctl.py cache.reload what=item id=0_Demon_Bound_Bracelet

Arguments are key=value; a whole number is sent as a number, true and false as booleans. Socket: --socket, else
$OPENDAOC_ADMIN_SOCKET, else ./admin.sock. Actor (who is acting, for the audit log): --actor, else $USER.
Exit code 0 when the answer is ok, 1 when it is an error, 2 for usage or connection problems.
"""

import argparse
import json
import os
import socket
import sys


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("op")
    ap.add_argument("args", nargs="*", metavar="key=value")
    ap.add_argument("--socket", default=os.environ.get("OPENDAOC_ADMIN_SOCKET", "admin.sock"))
    ap.add_argument("--actor", default=os.environ.get("USER", "unknown"))
    ap.add_argument("--timeout", type=float, default=10.0)
    a = ap.parse_args()

    args = {}
    for kv in a.args:
        key, sep, value = kv.partition("=")
        if not sep or not key:
            print(f"bad argument {kv!r}: expected key=value", file=sys.stderr)
            return 2
        if value.lstrip("-").isdigit():
            args[key] = int(value)
        elif value in ("true", "false"):
            args[key] = value == "true"
        else:
            args[key] = value

    request = {"id": "1", "actor": a.actor, "op": a.op, "args": args}
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
            s.settimeout(a.timeout)
            s.connect(a.socket)
            s.sendall((json.dumps(request) + "\n").encode())
            buf = b""
            while not buf.endswith(b"\n"):
                chunk = s.recv(65536)
                if not chunk:
                    break
                buf += chunk
    except OSError as e:
        print(f"can't reach the admin host at {a.socket}: {e}", file=sys.stderr)
        return 2

    if not buf:
        print("the admin host closed the connection without answering", file=sys.stderr)
        return 2
    answer = json.loads(buf)
    print(json.dumps(answer, indent=2))
    return 0 if answer.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
