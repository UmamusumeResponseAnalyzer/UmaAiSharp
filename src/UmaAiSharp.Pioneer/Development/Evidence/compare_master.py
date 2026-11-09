"""Read-only comparison of the Pioneer tables in two local SQLite masters."""

import argparse
import hashlib
import json
import sqlite3
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--left", type=Path, required=True)
parser.add_argument("--right", type=Path, required=True)
args = parser.parse_args()
connections = [sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)
               for path in (args.left, args.right)]
tables = [dict(connection.execute("select name, sql from sqlite_master "
                                 "where type='table' and name like 'single_mode_11_%'"))
          for connection in connections]
comparisons = []
for name in sorted(tables[0].keys() | tables[1].keys()):
    identifier = '"' + name.replace('"', '""') + '"'
    rows = [connection.execute("select * from " + identifier + " order by 1").fetchall()
            if name in schema else None for connection, schema in zip(connections, tables)]
    comparisons.append({"table": name, "leftRows": len(rows[0]) if rows[0] is not None else None,
                        "rightRows": len(rows[1]) if rows[1] is not None else None,
                        "schemaMatches": tables[0].get(name) == tables[1].get(name),
                        "rowsMatch": rows[0] == rows[1]})
hashes = []
for path in (args.left, args.right):
    with path.open("rb") as source:
        hashes.append(hashlib.file_digest(source, "sha256").hexdigest())
print(json.dumps({"leftSha256": hashes[0], "rightSha256": hashes[1], "tablesCompared": comparisons}, indent=2))
