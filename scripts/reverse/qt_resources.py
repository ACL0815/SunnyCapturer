#!/usr/bin/env python3
"""Read-only Qt QM evidence index, Python stdlib only; never executes the target.

python -B scripts/reverse/qt_resources.py --self-check
python -B scripts/reverse/qt_resources.py --root <SunnyCapturer> --out docs/kairos/reverse/qt

Format references (reference versions, NOT an installed-Qt version claim):
https://github.com/qt/qttools/blob/v6.8.3/src/linguist/shared/qm.cpp
https://github.com/qt/qtbase/blob/v6.8.3/src/corelib/kernel/qtranslator.cpp
https://github.com/qt/qtbase/blob/v6.8.3/src/corelib/io/qresource.cpp
"""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct


MAGIC = bytes.fromhex("3cb86418caef9c95cd211cbf60a1bddd")
TARGET_SHA256 = "e97cc2612b79ab154076844a13ec01cdeb70be0f14cf20675ce0e39dff81ecc8"
BLOCKS = {0x2F: "contexts", 0x42: "hashes", 0x69: "messages",
          0x88: "numerus_rules", 0x96: "dependencies", 0xA7: "language"}
FIELDS = {3: ("translations", "utf-16-be"), 6: ("source", "utf-8"),
          7: ("context", "utf-8"), 8: ("comment", "utf-8")}
FORMAT_SOURCES = [
    "https://github.com/qt/qttools/blob/v6.8.3/src/linguist/shared/qm.cpp",
    "https://github.com/qt/qtbase/blob/v6.8.3/src/corelib/kernel/qtranslator.cpp",
    "https://github.com/qt/qtbase/blob/v6.8.3/src/corelib/io/qresource.cpp",
]


class FormatError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise FormatError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def span(data, offset, length):
    return {"offset": offset, "length": length,
            "sha256": sha(data[offset:offset + length])}


def u32(data, offset, end):
    require(0 <= offset <= end - 4, f"truncated uint32 at {offset:#x}")
    return struct.unpack_from(">I", data, offset)[0]


def elf_hash(data):
    value = 0
    for byte in data.split(b"\0", 1)[0]:
        value = (value << 4) + byte
        high = value & 0xF0000000
        value = (value ^ (high >> 24)) & ~high
    return value or 1


def parse_qm(data):
    """Strict observed-format parser; missing key fields stay missing, never inherited."""
    require(data.startswith(MAGIC), "QM magic missing or truncated")
    blocks, by_tag, warnings = [], {}, []
    pos = len(MAGIC)
    while pos < len(data):
        require(pos + 5 <= len(data), f"truncated block header at {pos:#x}")
        tag, length = data[pos], u32(data, pos + 1, len(data))
        require(tag != 0 and length != 0, f"unsupported zero block at {pos:#x}")
        require(length <= len(data) - pos - 5, f"block exceeds file at {pos:#x}")
        require(tag not in by_tag, f"duplicate block {tag:#x}")
        block = {"tag": tag, "name": BLOCKS.get(tag, "unknown"),
                 "header_offset": pos, **span(data, pos + 5, length)}
        blocks.append(block)
        by_tag[tag] = block
        if tag not in BLOCKS:
            warnings.append(f"opaque unknown block {tag:#x}")
        if tag in (0x2F, 0x96):
            warnings.append(f"{BLOCKS[tag]} block retained as opaque span, not interpreted")
        pos += 5 + length

    require(0x42 in by_tag and 0x69 in by_tag, "hashes/messages block missing")
    language = None
    if 0xA7 in by_tag:
        b = by_tag[0xA7]
        language = data[b["offset"]:b["offset"] + b["length"]].decode("utf-8")
    b = by_tag[0x69]
    pos, end = b["offset"], b["offset"] + b["length"]
    records, at_offset, tag_counts = [], {}, Counter()
    while pos < end:
        start = pos
        record = {"translations": [], "values": {}}
        while True:
            require(pos < end, f"unterminated message at {start:#x}")
            tag_offset, tag = pos, data[pos]
            pos += 1
            tag_counts[str(tag)] += 1
            if tag == 1:
                break
            if tag == 5:  # Qt Tag_Obsolete1 carries four fixed bytes, no length prefix.
                require(pos + 4 <= end, f"truncated obsolete field at {tag_offset:#x}")
                record.setdefault("obsolete", []).append(span(data, pos, 4))
                pos += 4
                continue
            require(tag in FIELDS, f"unsupported message tag {tag:#x} at {tag_offset:#x}")
            name, encoding = FIELDS[tag]
            encoded_length = u32(data, pos, end)
            pos += 4
            null = encoded_length == 0xFFFFFFFF
            length = 0 if null else encoded_length
            require(length <= end - pos, f"field exceeds messages block at {tag_offset:#x}")
            require(encoding != "utf-16-be" or length % 2 == 0,
                    f"odd UTF-16 byte length at {tag_offset:#x}")
            raw = data[pos:pos + length]
            value = None if null else raw.decode(encoding)
            field = {"tag_offset": tag_offset, **span(data, pos, length),
                     "encoding": encoding, "null": null}
            if tag == 3:
                record[name].append(field)
            else:
                require(name not in record, f"duplicate {name} field at {tag_offset:#x}")
                record[name] = field
                record["values"][name] = value
            pos += length
        require(record["translations"], f"message without translation at {start:#x}")
        record.update(span(data, start, pos - start))
        record["relative_offset"] = start - b["offset"]
        records.append(record)
        at_offset[record["relative_offset"]] = record

    b = by_tag[0x42]
    require(b["length"] % 8 == 0, "hash table length is not a multiple of eight")
    referenced, previous, verified = set(), -1, 0
    for pos in range(b["offset"], b["offset"] + b["length"], 8):
        hash_value, relative = struct.unpack_from(">II", data, pos)
        require(hash_value >= previous, f"unsorted hash table at {pos:#x}")
        previous = hash_value
        require(relative in at_offset, f"hash offset is not a message start at {pos:#x}")
        require(relative not in referenced, f"duplicate message reference at {pos:#x}")
        referenced.add(relative)
        record = at_offset[relative]
        record["hash_entry"] = {**span(data, pos, 8), "elf_hash": hash_value}
        if all(k in record for k in ("source", "comment")):
            raw_key = b"".join(data[record[k]["offset"]:
                                       record[k]["offset"] + record[k]["length"]]
                               for k in ("source", "comment"))
            require(elf_hash(raw_key) == hash_value, f"ELF hash mismatch at {pos:#x}")
            verified += 1
    require(referenced == set(at_offset), "unreferenced message records")
    missing = {key: sum(key not in r for r in records) for key in ("source", "context", "comment")}
    return {"language": language, "blocks": blocks, "records": records,
            "checks": {"message_count": len(records), "hash_entry_count": len(referenced),
                       "elf_hashes_verified": verified, "missing_fields": missing,
                       "message_tags": dict(sorted(tag_counts.items())),
                       "null_translations": sum(t["null"] for r in records for t in r["translations"]),
                       "empty_translations": sum(not t["null"] and t["length"] == 0
                                                 for r in records for t in r["translations"])},
            "warnings": warnings}


def self_check():
    """No fixture files: verify real encoding/null/hash semantics and reject broken inputs."""
    def block(tag, payload):
        return bytes([tag]) + struct.pack(">I", len(payload)) + payload

    def field(tag, text):
        if text is None:
            return bytes([tag]) + b"\xff\xff\xff\xff"
        return block(tag, text.encode("utf-16-be" if tag == 3 else "utf-8"))

    messages, entries = b"", []
    for source, translations in (("Rectangle (Ctrl+1)", ["Rechteck – 🔍", ""]), ("Quit", [None])):
        entries.append((elf_hash(source.encode()), len(messages)))
        messages += (b"".join(field(3, t) for t in translations) + field(8, "")
                     + field(6, source) + field(7, "EditingMenu") + b"\x01")
    good = (MAGIC + block(0xA7, b"de_DE")
            + block(0x42, b"".join(struct.pack(">II", *e) for e in sorted(entries)))
            + block(0x69, messages))
    parsed = parse_qm(good)
    require(parsed["checks"]["message_count"] == 2, "self-check count")
    require(parsed["checks"]["elf_hashes_verified"] == 2, "self-check hashes")
    require(parsed["checks"]["null_translations"] == 1, "self-check null")
    require(parsed["checks"]["empty_translations"] == 1, "self-check empty")
    first = parsed["records"][0]
    require(first["values"]["source"] == "Rectangle (Ctrl+1)", "self-check source")
    require(first["values"]["context"] == "EditingMenu", "self-check context")
    t = first["translations"][0]
    require(good[t["offset"]:t["offset"] + t["length"]].decode(t["encoding"])
            == "Rechteck – 🔍", "self-check UTF-16 and surrogate pair")
    for record in parsed["records"]:
        for evidence in [record, record["hash_entry"], record["source"], record["context"],
                         record["comment"], *record["translations"]]:
            require(evidence["sha256"] == sha(good[evidence["offset"]:
                                                  evidence["offset"] + evidence["length"]]),
                    "self-check evidence spans")
    huge = bytearray(good)
    huge[t["tag_offset"] + 1:t["tag_offset"] + 5] = struct.pack(">I", 0xFFFFFFFE)
    wrong_offset = bytearray(good)
    h = parsed["records"][0]["hash_entry"]["offset"]
    wrong_offset[h + 4:h + 8] = struct.pack(">I", 1)
    odd = bytearray(good)
    odd[t["tag_offset"] + 1:t["tag_offset"] + 5] = struct.pack(">I", t["length"] - 1)
    cases = {"bad_magic": (b"X" + good[1:], "magic"),
             "truncated_messages_block": (good[:-1], "block exceeds file"),
             "oversized_translation": (bytes(huge), "field exceeds messages"),
             "hash_points_into_record": (bytes(wrong_offset), "not a message start"),
             "odd_utf16_length": (bytes(odd), "odd UTF-16")}
    rejected = []
    for name, (broken, expected) in cases.items():
        try:
            parse_qm(broken)
        except FormatError as error:
            require(expected in str(error), f"wrong rejection for {name}: {error}")
            rejected.append({"case": name, "input_sha256": sha(broken), "error": str(error)})
        else:
            raise AssertionError(f"accepted corrupt input: {name}")
    return {"status": "passed", "synthetic_valid_sha256": sha(good),
            "positive": ["UTF-8 source/context", "UTF-16BE including surrogate pair",
                         "multiple translation forms", "null distinct from empty",
                         "record/hash/field offsets and SHA-256", "ELF lookup hashes"],
            "rejected": rejected}


def inventory(root):
    exe = root / "SunnyCapturer.exe"
    binary = exe.read_bytes()
    require(sha(binary) == TARGET_SHA256, "target EXE SHA-256 differs from authorized artifact")
    qres_offsets, pos = [], binary.find(b"qres")
    while pos != -1:
        qres_offsets.append(pos)
        pos = binary.find(b"qres", pos + 1)
    files, keys, occurrences = [], {}, Counter()
    # ponytail: only standalone RCC headers are probed; static table association needs code xrefs.
    candidates = sorted(p for p in root.rglob("*") if p.is_file()
                        and p.suffix.lower() in (".qm", ".qrc", ".rcc"))
    for path in candidates:
        if path.suffix.lower() != ".qm":
            continue
        data = path.read_bytes()
        parsed = parse_qm(data)
        rel = path.relative_to(root).as_posix()
        counts = Counter()
        for record in parsed["records"]:
            values = record.pop("values")
            key = tuple(values.get(k) for k in ("context", "source", "comment"))
            counts[key[0]] += 1
            key_id = "K" + sha(json.dumps(key, ensure_ascii=False).encode())[:16]
            if key_id in keys:
                require(keys[key_id]["key"] == key, "source key identifier collision")
            else:
                keys[key_id] = {"id": key_id, "key": key, "references": []}
            record["key_id"] = key_id
            record["id"] = f"{rel}@0x{record['offset']:x}"
            keys[key_id]["references"].append(record["id"])
            occurrences[key[0]] += 1
        files.append({"path": rel, "size": len(data), "sha256": sha(data),
                      **parsed, "contexts": dict(sorted(counts.items(), key=lambda x: str(x[0])))})
    require(files, "no QM files found")
    source_index = []
    for item in sorted(keys.values(), key=lambda x: tuple(s or "" for s in x["key"])):
        context, source, comment = item.pop("key")
        source_index.append({**item, "context": context, "source": source, "comment": comment})
    contexts = []
    for context in sorted(occurrences, key=lambda x: str(x)):
        selected = [k for k in source_index if k["context"] == context]
        contexts.append({"context": context, "unique_keys": len(selected),
                         "message_occurrences": occurrences[context],
                         "key_ids": [k["id"] for k in selected]})
    return {"schema_version": 1, "target": {"root": str(root), "exe": exe.name,
                                             "size": len(binary), "sha256": sha(binary)},
            "scope": {"read": ["standalone *.qm", "SunnyCapturer.exe digest and exact qres marker"],
                      "names_only": ["*.qrc", "*.rcc"],
                      "excluded": ["imports/exports", "general EXE strings", "runtime/UI",
                                   "configuration/accounts", "historical source tree"]},
            "format_sources": FORMAT_SOURCES,
            "offset_convention": "zero-based absolute byte offsets, half-open [offset, offset+length); fields hash payload bytes; message hash includes tags and End; null has length 0 and marker FFFFFFFF at tag_offset+1",
            "translation_policy": "translation text is decoded for validation but not exported; only spans/hashes/null status; no original QM or visual assets copied",
            "qrc": {"status": "not_reconstructed", "exe_qres_magic_offsets": qres_offsets,
                    "standalone_candidates": [p.relative_to(root).as_posix() for p in candidates
                                              if p.suffix.lower() in (".qrc", ".rcc")],
                    "limit": "qres absence does not exclude compiled tree/name/data arrays; no verified registration pointers or paths; no QRC feature claims"},
            "summary": {"qm_files": len(files), "messages": sum(len(f["records"]) for f in files),
                        "unique_keys": len(source_index), "contexts": contexts},
            "files": files, "source_index": source_index}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-check", action="store_true")
    parser.add_argument("--root", type=Path)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    check = self_check()
    if args.root is None:
        if not args.self_check:
            parser.error("--root and --out required")
        print(json.dumps(check, ensure_ascii=False, indent=2))
        return
    if args.out is None:
        parser.error("--out required with --root")
    # Keep this investigation's write surface inside its explicitly authorized directory.
    allowed = Path(__file__).resolve().parents[2] / "docs/kairos/reverse/qt"
    require(args.out.resolve() == allowed.resolve(), f"output must be {allowed}")
    result = inventory(args.root.resolve())  # Finish validation before writing any output.
    args.out.mkdir(parents=True, exist_ok=True)
    for name, value in (("inventory.json", result), ("selfcheck.json", check)):
        (args.out / name).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n",
                                     encoding="utf-8", newline="\n")
    print(json.dumps({"files": result["summary"]["qm_files"],
                      "messages": result["summary"]["messages"],
                      "unique_keys": result["summary"]["unique_keys"],
                      "self_check": check["status"], "output": str(args.out)}, indent=2))


if __name__ == "__main__":
    main()
