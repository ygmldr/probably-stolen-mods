#!/usr/bin/env python3
"""Print the SHA256 and CLI module version id (MVID) of a .NET assembly.

Why this exists: the release checklist has to prove that the DLL a player
installs is byte-identical to the one this repository builds. A SHA256 answers
"identical"; the MVID answers "is this the assembly the log/health reported",
because the game-side bridge and the adapter tests read the same value from
`Assembly.ManifestModule.ModuleVersionId`.

The MVID is the first entry of the metadata `#GUID` stream (ECMA-335 II.24.2.1),
so this only needs the PE/CLI headers, not a .NET runtime.

Usage:
    python3 scripts/audit/dll_fingerprint.py <dll> [<dll> ...]

Exit code 0 on success, 1 when a file is not a readable .NET assembly.
"""

import hashlib
import struct
import sys

#: PE data directory index of the CLI header (ECMA-335 II.25.3.3).
CLI_HEADER_DIRECTORY = 14


class NotAnAssembly(Exception):
    pass


def _read(data, offset, size):
    if offset < 0 or offset + size > len(data):
        raise NotAnAssembly("truncated file at offset %d" % offset)
    return data[offset:offset + size]


def _u16(data, offset):
    return struct.unpack("<H", _read(data, offset, 2))[0]


def _u32(data, offset):
    return struct.unpack("<I", _read(data, offset, 4))[0]


def _pe_header_offset(data):
    if data[:2] != b"MZ":
        raise NotAnAssembly("missing DOS signature")
    return _u32(data, 0x3C)


def _sections(data, pe_offset):
    if data[pe_offset:pe_offset + 4] != b"PE\0\0":
        raise NotAnAssembly("missing PE signature")
    coff = pe_offset + 4
    section_count = _u16(data, coff + 2)
    optional_size = _u16(data, coff + 16)
    optional = coff + 20
    magic = _u16(data, optional)
    if magic == 0x10B:          # PE32
        directories = optional + 96
    elif magic == 0x20B:        # PE32+
        directories = optional + 112
    else:
        raise NotAnAssembly("unknown optional header magic 0x%x" % magic)

    cli_rva = _u32(data, directories + CLI_HEADER_DIRECTORY * 8)
    cli_size = _u32(data, directories + CLI_HEADER_DIRECTORY * 8 + 4)
    if cli_rva == 0 or cli_size == 0:
        raise NotAnAssembly("no CLI header: managed assembly not found")

    table = optional + optional_size
    sections = []
    for index in range(section_count):
        entry = table + index * 40
        name = _read(data, entry, 8).rstrip(b"\0").decode("ascii", "replace")
        virtual_size = _u32(data, entry + 8)
        virtual_address = _u32(data, entry + 12)
        raw_size = _u32(data, entry + 16)
        raw_pointer = _u32(data, entry + 20)
        sections.append((name, virtual_address, virtual_size, raw_pointer, raw_size))
    return sections, cli_rva


def _rva_to_offset(sections, rva):
    for _name, virtual_address, virtual_size in [(s[0], s[1], s[2]) for s in sections]:
        span = max(virtual_size, 1)
        if virtual_address <= rva < virtual_address + span:
            for _n, va, vs, raw_pointer, raw_size in sections:
                if va == virtual_address:
                    return raw_pointer + (rva - va)
    raise NotAnAssembly("RVA 0x%x is not inside any section" % rva)


def _metadata_streams(data, sections, cli_rva):
    cli_offset = _rva_to_offset(sections, cli_rva)
    metadata_rva = _u32(data, cli_offset + 8)
    metadata = _rva_to_offset(sections, metadata_rva)

    if _u32(data, metadata) != 0x424A5342:      # BSJB
        raise NotAnAssembly("missing metadata root signature")
    version_length = _u32(data, metadata + 12)
    offset = metadata + 16 + version_length + 2  # + flags
    stream_count = _u16(data, offset)
    offset += 2

    streams = {}
    for _ in range(stream_count):
        stream_offset = _u32(data, offset)
        stream_size = _u32(data, offset + 4)
        offset += 8
        end = offset
        while end < len(data) and data[end] != 0:
            end += 1
        name = data[offset:end].decode("ascii", "replace")
        offset = end + 1
        offset = (offset + 3) & ~3          # 4-byte aligned
        streams[name] = (metadata + stream_offset, stream_size)
    return streams


def mvid(data):
    sections, cli_rva = _sections(data, _pe_header_offset(data))
    streams = _metadata_streams(data, sections, cli_rva)
    if "#GUID" not in streams:
        raise NotAnAssembly("no #GUID stream")
    start, size = streams["#GUID"]
    if size < 16:
        raise NotAnAssembly("#GUID stream is too small for an MVID")
    return _format_guid(_read(data, start, 16))


def _format_guid(raw):
    """Render the 16-byte GUID the way .NET's Guid.ToString("D") does."""
    a, b, c = struct.unpack("<IHH", raw[:8])
    tail = raw[8:]
    return "%08x-%04x-%04x-%s-%s" % (
        a, b, c, tail[:2].hex(), tail[2:].hex())


def fingerprint(path):
    with open(path, "rb") as handle:
        data = handle.read()
    return hashlib.sha256(data).hexdigest(), mvid(data)


def main(argv):
    if len(argv) < 2:
        print("usage: dll_fingerprint.py <dll> [<dll> ...]", file=sys.stderr)
        return 2

    status = 0
    for path in argv[1:]:
        try:
            digest, module_id = fingerprint(path)
        except (OSError, NotAnAssembly) as error:
            print("%s: NOT_AN_ASSEMBLY (%s)" % (path, error))
            status = 1
            continue
        print("path:   %s" % path)
        print("sha256: %s" % digest)
        print("mvid:   %s" % module_id)
    return status


if __name__ == "__main__":
    sys.exit(main(sys.argv))
