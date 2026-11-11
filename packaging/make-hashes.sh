#!/usr/bin/env bash
# =============================================================================
#  TaoKeeper package hash generator (SHA-256) - Linux / sandbox version
#  Package: PC-FULL-V1 / MSI 1.0.0.0 (2026-11-12)
#  Generates hashes.txt next to this script (place this script in out/).
#  Format: "<lowercase sha256>  <relative path>", "/" separators, LF endings.
#  See hashes-spec.md. Exit non-zero on any hash failure (no partial list).
# =============================================================================
set -euo pipefail
cd "$(dirname "$0")"

OUT="hashes.txt"
TMP="${OUT}.tmp"

rm -f "$TMP"

count=0
while IFS= read -r -d '' f; do
    [ "$f" = "./$OUT" ] && continue
    rel="${f#./}"
    sha256sum "$rel" >> "$TMP"
    count=$((count + 1))
done < <(find . -type f -print0 | LC_ALL=C sort -z)

# sha256sum output already matches: "<hash>  <path>" (two spaces), LF endings,
# "/" separators. Verify every line is well-formed before promoting.
if [ "$(grep -cE '^[0-9a-f]{64}  ' "$TMP")" -ne "$count" ]; then
    echo "[ERROR] malformed lines in $TMP - aborting, no hashes.txt written" >&2
    rm -f "$TMP"
    exit 1
fi

mv "$TMP" "$OUT"
echo "[OK] hashes.txt written: $(pwd)/$OUT"
echo "[OK] files hashed: $count"
