#!/usr/bin/env python3
"""Post-processes Kaitai Struct C# output for RekordboxPdb/RekordboxAnlz so it compiles.

The Kaitai C# target (0.10 and 0.11) emits a handful of type errors for these
particular .ksy files; every fix here is a pure type-level adjustment with no
semantic change. Run: patch_kaitai.py <dir containing RekordboxPdb.cs and RekordboxAnlz.cs>
"""
import re
import sys
from pathlib import Path


HEADER = (
    "// Patched by tools/patch_kaitai.py after generation; see that script for details.\n"
    "#nullable disable\n"
    "#pragma warning disable 1591  // generated code carries no XML docs\n"
)


def patch(path: Path, replacements: list[tuple[str, str, int]]) -> None:
    text = path.read_text()
    for pattern, replacement, expected in replacements:
        text, count = re.subn(pattern, replacement, text)
        if count != expected:
            raise SystemExit(
                f"{path.name}: pattern {pattern!r} matched {count} times, expected {expected}"
            )
    if not text.startswith(HEADER):
        text = HEADER + text
    path.write_text(text)
    print(f"patched {path.name}")


def main(directory: str) -> None:
    root = Path(directory)

    patch(root / "RekordboxAnlz.cs", [
        # A byte[] literal built from signed-byte casts (Java bytes are signed).
        (r"\(\(sbyte\) \(\((\d+ \+ C)\)\)\)", r"((byte) ((\1)))", 19),
        # Conditionally-read length fields are nullable; ReadBytes/GetString need concrete longs.
        (r"m_io\.ReadBytes\(LenComment\)", "m_io.ReadBytes((long) LenComment)", 1),
        (r"m_io\.ReadBytes\(\(\(LenEntry - 48\) - LenComment\)\)",
         "m_io.ReadBytes((long) ((LenEntry - 48) - LenComment))", 1),
    ])

    patch(root / "RekordboxPdb.cs", [
        # RowBase is computed lazily as a nullable; Seek needs a long.
        (r"m_io\.Seek\(\(M_Parent\.RowBase \+ \(\(Subtype & 4\) == 4 \? OfsNameFar : OfsNameNear\)\)\);",
         "m_io.Seek((long) (M_Parent.RowBase + ((Subtype & 4) == 4 ? OfsNameFar : OfsNameNear)));", 3),
        # Loop counters are int; the generated constructors take ushort indexes.
        (r"_rowGroups\.Add\(new RowGroup\(i, m_io, this, m_root\)\);",
         "_rowGroups.Add(new RowGroup((ushort) i, m_io, this, m_root));", 1),
        (r"_rows\.Add\(new RowRef\(i, m_io, this, m_root\)\);",
         "_rows.Add(new RowRef((ushort) i, m_io, this, m_root));", 1),
    ])


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
