#!/usr/bin/env bash
# Regenerates the Kaitai Struct parsers for the rekordbox PDB and ANLZ formats from the
# upstream crate-digger definitions, then applies the type-level patches needed for the
# C# target to compile (see patch_kaitai.py). The generated files are committed, so this
# only needs to be re-run when tracking a new crate-digger release.
#
# Requires: kaitai-struct-compiler (brew install kaitai-struct-compiler), curl, python3.
set -euo pipefail

# The crate-digger commit whose definitions the committed parsers were generated from.
CRATE_DIGGER_REF="d95c444bde4bec6763cbbffd1a405dda0cc08750"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="$SCRIPT_DIR/../src/BeatLinkNet/CrateDigger/Pdb"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

for f in rekordbox_pdb.ksy rekordbox_anlz.ksy; do
    curl -sfL "https://raw.githubusercontent.com/Deep-Symmetry/crate-digger/$CRATE_DIGGER_REF/src/main/kaitai/$f" -o "$WORK_DIR/$f"
done

kaitai-struct-compiler --target csharp --outdir "$WORK_DIR/gen" \
    --dotnet-namespace BeatLink.CrateDigger.Pdb \
    "$WORK_DIR/rekordbox_pdb.ksy" "$WORK_DIR/rekordbox_anlz.ksy"

python3 "$SCRIPT_DIR/patch_kaitai.py" "$WORK_DIR/gen"

mkdir -p "$OUT_DIR"
cp "$WORK_DIR/gen/RekordboxPdb.cs" "$WORK_DIR/gen/RekordboxAnlz.cs" "$OUT_DIR/"
echo "Updated $OUT_DIR from crate-digger $CRATE_DIGGER_REF"
