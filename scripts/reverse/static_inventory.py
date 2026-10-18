"""Offline SunnyCapturer inventory; Python 3.11+ standard library only.

Does not load/execute a target, contact REA, inspect user settings or copy binaries.
Run with --self-check for the small parser regression check; see the German report.
"""
import argparse
import hashlib
import json
import mmap
import re
import struct
from collections import Counter
from pathlib import Path

EXPECTED_SHA = "e97cc2612b79ab154076844a13ec01cdeb70be0f14cf20675ce0e39dff81ecc8"
INSTALL = Path(r"C:\Users\Axel_desktop\AppData\Local\Programs\SunnyCapturer")
REPO = Path(__file__).resolve().parents[2]
DETAIL = {"sunnycapturer.exe", "sunny_ocr.dll", "common.dll"}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def file_sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def loc(offset, size):
    return {"offset": offset, "offset_hex": hex(offset), "size": size}


class PE:
    def __init__(self, data):
        self.b = data
        assert data[:2] == b"MZ", "not MZ"
        self.pe = self.u("I", 60)[0]
        assert data[self.pe:self.pe + 4] == b"PE\0\0", "not PE"
        self.machine, n, self.timestamp = self.u("HHI", self.pe + 4)
        self.opt = self.pe + 24
        self.magic = self.u("H", self.opt)[0]
        assert self.magic in (0x10b, 0x20b), "unsupported optional header"
        self.width = 8 if self.magic == 0x20b else 4
        self.base = self.u("Q" if self.width == 8 else "I", self.opt + (24 if self.width == 8 else 28))[0]
        self.header_size = self.u("I", self.opt + 60)[0]
        directory = self.opt + (112 if self.width == 8 else 96)
        count = min(16, self.u("I", directory - 4)[0])
        self.dirs = [self.u("II", directory + 8 * i) for i in range(count)]
        self.dirs += [(0, 0)] * (16 - count)
        section_table = self.opt + self.u("H", self.pe + 20)[0]
        self.sections = []
        for i in range(n):
            o = section_table + 40 * i
            name, vs, va, size, raw = self.u("8sIIII", o)
            assert raw + size <= len(data), "section outside file"
            self.sections.append(dict(name=name.rstrip(b"\0").decode("ascii"), virtual_size=vs,
                                      rva=va, raw_offset=raw, raw_size=size,
                                      characteristics=hex(self.u("I", o + 36)[0])))

    def u(self, fmt, offset):
        assert 0 <= offset <= len(self.b) - struct.calcsize("<" + fmt), "read outside file"
        return struct.unpack_from("<" + fmt, self.b, offset)

    def off(self, rva):
        if 0 <= rva < self.header_size:
            return rva
        for s in self.sections:
            if s["rva"] <= rva < s["rva"] + s["raw_size"]:
                return s["raw_offset"] + rva - s["rva"]
        raise ValueError(f"RVA {rva:#x} has no file bytes")

    def cstr(self, offset):
        end = self.b.find(b"\0", offset, min(offset + 4096, len(self.b)))
        assert end >= offset, "unterminated name"
        return self.b[offset:end].decode("ascii")

    def imports(self, detail):
        result = []
        for kind, index, stride in (("normal", 1, 20), ("delay", 13, 32)):
            rva, size = self.dirs[index]
            if not rva:
                continue
            start = self.off(rva)
            terminated = False
            for o in range(start, start + size - stride + 1, stride):
                row = self.u("I" * (stride // 4), o)
                if not any(row):
                    terminated = True
                    break
                if kind == "normal":
                    lookup, _, _, name, iat = row
                    lookup = lookup or iat
                else:
                    attrs, name, _, iat, lookup, _, _, _ = row
                    if not attrs & 1:
                        name, iat, lookup = name - self.base, iat - self.base, lookup - self.base
                symbols = []
                t = self.off(lookup)
                for j in range(16384):
                    value = self.u("Q" if self.width == 8 else "I", t + j * self.width)[0]
                    if not value:
                        break
                    item = {"lookup_offset": t + j * self.width, "iat_rva": iat + j * self.width}
                    if value & (1 << (self.width * 8 - 1)):
                        item["ordinal"] = value & 0xffff
                    else:
                        q = self.off(value)
                        item.update(name=self.cstr(q + 2), name_offset=q + 2, hint=self.u("H", q)[0])
                    symbols.append(item)
                else:
                    raise ValueError("import thunk limit reached")
                item = dict(kind=kind, dll=self.cstr(self.off(name)), descriptor_offset=o,
                            name_offset=self.off(name), symbol_count=len(symbols))
                if detail:
                    item["symbols"] = symbols
                result.append(item)
            assert terminated, "unterminated import directory"
        return result

    def exports(self, detail):
        rva, size = self.dirs[0]
        if not rva:
            return {"present": False, "entries": []}
        o = self.off(rva)
        _, _, _, _, name, base, nf, nn, funcs, names, ords = self.u("IIHHIIIIIII", o)
        result = dict(present=True, directory=loc(o, size), dll=self.cstr(self.off(name)),
                      ordinal_base=base, function_slots=nf, named_count=nn)
        if detail:
            assert nf < 100000 and nn <= nf, "export count limit"
            labels = {}
            for i in range(nn):
                no = self.off(self.u("I", self.off(names) + i * 4)[0])
                labels.setdefault(self.u("H", self.off(ords) + i * 2)[0], []).append(
                    dict(name=self.cstr(no), name_offset=no))
            entries = []
            for i in range(nf):
                q = self.off(funcs) + i * 4
                target = self.u("I", q)[0]
                if target:
                    row = dict(ordinal=base + i, rva=hex(target), address_table_offset=q,
                               names=labels.get(i, []))
                    if rva <= target < rva + size:
                        row["forwarder"] = self.cstr(self.off(target))
                    else:
                        try:
                            row["file_offset"] = self.off(target)
                        except ValueError:
                            # Exported zero-initialized data may have no bytes on disk.
                            row.update(file_offset=None, mapping="not file-backed")
                    entries.append(row)
            result["entries"] = entries
        return result

    def resources(self):
        rva, size = self.dirs[2]
        if not rva:
            return []
        root, leaves, seen = self.off(rva), [], set()

        def walk(relative, path):
            assert relative not in seen and len(path) <= 3, "resource tree cycle/depth"
            seen.add(relative)
            o = root + relative
            assert relative + 16 <= size, "resource directory outside bound"
            named, ids = self.u("HH", o + 12)
            assert relative + 16 + 8 * (named + ids) <= size
            for i in range(named + ids):
                entry = o + 16 + i * 8
                name, child = self.u("II", entry)
                if name & 0x80000000:
                    q = root + (name & 0x7fffffff)
                    n = self.u("H", q)[0]
                    name = self.b[q + 2:q + 2 + n * 2].decode("utf-16le")
                branch = path + [name]
                if child & 0x80000000:
                    walk(child & 0x7fffffff, branch)
                else:
                    data_entry = root + child
                    target, length, cp, _ = self.u("IIII", data_entry)
                    q = self.off(target)
                    assert q + length <= len(self.b)
                    row = dict(path=branch, data_entry_offset=data_entry,
                               payload=loc(q, length), codepage=cp, sha256=sha(self.b[q:q + length]))
                    if branch[0] == 16:
                        signature = self.b.find(b"\xbd\x04\xef\xfe", q, q + length)
                        if signature >= 0:
                            a, b = self.u("II", signature + 8)
                            row["fixed_file_version"] = [a >> 16, a & 65535, b >> 16, b & 65535]
                            row["fixed_version_offset"] = signature
                    leaves.append(row)
        walk(0, [])
        return leaves

    def summary(self, detail=False):
        return dict(machine=hex(self.machine), pe_offset=self.pe, optional_magic=hex(self.magic),
                    image_base=hex(self.base), entry_rva=hex(self.u("I", self.opt + 16)[0]),
                    coff_timestamp_raw=self.timestamp, sections=self.sections,
                    directories=[dict(index=i, address=hex(r), size=n,
                                      address_kind="file_offset" if i == 4 else "rva")
                                 for i, (r, n) in enumerate(self.dirs) if r],
                    imports=self.imports(detail), exports=self.exports(detail))


def qt_objects(pe):
    """Bounded Qt6 revision-12 static QMetaObject/stringdata decoder, not a disassembler."""
    b, result = pe.b, []
    s = next(s for s in pe.sections if s["name"] == ".rdata")
    for o in range(s["raw_offset"], s["raw_offset"] + s["raw_size"] - 48 + 1, 8):
        string_va, data_va = pe.u("QQ", o + 8)
        try:
            st, dt = pe.off(string_va - pe.base), pe.off(data_va - pe.base)
        except ValueError:
            continue
        h = pe.u("14I", dt)
        if h[0] != 12 or h[1] != 0 or h[2] > 64 or h[4] > 256 or h[13] > h[4]:
            continue
        first, length = pe.u("II", st)
        if not (8 <= first <= 8192 and first % 8 == 0 and 1 <= length <= 80):
            continue
        if not re.fullmatch(rb"[A-Za-z_][A-Za-z0-9_:]*", b[st + first:st + first + length]):
            continue
        strings, expected = [], first
        for i in range(first // 8):
            relative, length = pe.u("II", st + 8 * i)
            assert relative == expected and length <= 4096 and st + relative + length < len(b)
            assert b[st + relative + length] == 0
            strings.append(dict(text=b[st + relative:st + relative + length].decode("utf-8"),
                                **loc(st + relative, length), pair_offset=st + 8 * i))
            expected += length + 1
        methods = []
        for i in range(h[4]):
            q = dt + (h[5] + i * 6) * 4
            name, argc, params, tag, flags, types = pe.u("6I", q)
            assert name < len(strings) and argc <= 64
            raw = pe.u("I" * (1 + argc * 2), dt + params * 4)
            methods.append(dict(name=strings[name]["text"], name_offset=strings[name]["offset"],
                                record_offset=q, argc=argc, flags=hex(flags),
                                kind={0: "method", 4: "signal", 8: "slot"}.get(flags & 12, "constructor"),
                                return_type_raw=raw[0], argument_types_raw=list(raw[1:argc + 1]),
                                argument_names=[strings[j]["text"] for j in raw[argc + 1:]],
                                parameter_data_offset=dt + params * 4))
        assert sum(m["kind"] == "signal" for m in methods) == h[13], "Qt signal count mismatch"
        result.append(dict(class_name=strings[0]["text"], object_offset=o,
                           super_metaobject_va=hex(pe.u("Q", o)[0]),
                           data_offset=dt, stringtable_offset=st, revision=h[0],
                           header_raw=list(h), signal_count=h[13], method_count=h[4],
                           property_count=h[6], enum_count=h[8], strings=strings, methods=methods))
    by_va = {}
    for obj in result:
        for section in pe.sections:
            q = obj["object_offset"]
            if section["raw_offset"] <= q < section["raw_offset"] + section["raw_size"]:
                by_va[hex(pe.base + section["rva"] + q - section["raw_offset"])] = obj["class_name"]
    for obj in result:
        obj["super_class_if_local"] = by_va.get(obj["super_metaobject_va"])
    return dict(search=loc(s["raw_offset"], s["raw_size"]), alignment=8,
                supported_revision=12, objects=result)


LITERALS = {
    "capture": ["Capturer", "onCpature", "ScreenShoter", "screenshoter.cpp", "Selector",
                "WindowsGraphicsCapturer", "Windows.Graphics.Capture.h", "Repeat Last Screenshot",
                "ScreenShot", "CaptureHelper"],
    "editor": ["EditingMenu", "GraphicsItem", "GraphicsTextItem", "CreatedCommand", "MoveCommand",
               "ResizeCommand", "RotateCommand", "DeleteCommand", "FontChangedCommand",
               "Rectangle (Ctrl+1)", "Ellipse (Ctrl+2)", "Arrow (Ctrl+3)", "Pencil (Ctrl+4) (B)",
               "Blur/Mosaic (Ctrl+5)", "Text (Ctrl+6) (T)", "Image (Ctrl+7)", "Counter (Ctrl+8)",
               "Eraser (E)", "Undo (Ctrl+Z)", "Redo (Ctrl+Y)", "PaintBar"],
    "ocr": ["OcrUI", "PaddleOCR", "/sunny_ocr.dll", "ch_PP-OCRv4_det_infer",
            "ch_PP-OCRv4_rec_infer", "ppocr_keys_v1.txt", "Starting OCR...", "OCR commandList:",
            "ImageOcrProcess", "ImageOcrProcessWithArgs", "FreeReturnPtr", "BaiduOcrChannel", "cbbOcrEngine",
            "NetworkOCR", "QLibrary", "LoadLibraryW", "GetProcAddress"],
    "hotkeys": ["QHotkey", "RegisterHotKey", "UnregisterHotKey", "UpdateHotkeys", "HotkeyEdit",
                "Preview Clipboard", "Toggle Previews", "Quick Look"],
    "configuration": ["config.cpp", "Settings File", "capturer_run", "Config File", "config.json",
                      "settings.json", "config.ini", "nlohmann", "le_quick_save_path", "cb_auto_save_enable",
                      "ConfigJson", "ConfigManager"],
    "export": ["onSave", "sigSaved", "Save to file (Ctrl+S)", "Copy to clipboard (Ctrl+C)",
               "Manual Save", "Quick Save", "Auto Save", "Ctrl + Shift + S", "Image quality",
               "SunnyCapturer_$yyyy-MM-dd_HH-mm-ss$.png", "failed to save the captured image.",
               ".png", ".jpg", ".bmp", ".webp", "QImageWriter"],
}


def literal_search(data):
    groups = {}
    for group, terms in LITERALS.items():
        groups[group] = []
        for term in terms:
            hits = []
            for encoding in ("ascii", "utf-16le"):
                needle = term.encode(encoding)
                start = 0
                while (q := data.find(needle, start)) >= 0:
                    hits.append(dict(encoding=encoding, **loc(q, len(needle))))
                    start = q + len(needle)
            groups[group].append(dict(literal=term, hits=hits))
    rtti = [dict(name=m.group().decode("ascii"), **loc(m.start(), len(m.group())))
            for m in re.finditer(rb"\.\?AV[A-Za-z_][A-Za-z0-9_:@]*@@(?=\x00)", data)]
    return dict(search=loc(0, len(data)), matching="case-sensitive substrings; ASCII and UTF-16LE; no hit cap",
                groups=groups, simple_msvc_rtti_names=rtti)


SOURCE_QUERIES = {
    "history-version-code/5th-SunnyCapturer-v5.x-Code/README.md": r"not open-sourced",
    "history-version-code/4th-SunnyCapturer-v3.x-Code/README.md": r"not open-sourced",
    "history-version-code/2nd-SunnyCapturer-v1.x-Code/src/ui/screenshot/screenshot.h":
        r"class ScreenShot|void btnOCR|void btnUndo|void btnRedo|void btnSave|void onOCR|m_undo;|m_redo;",
    "history-version-code/2nd-SunnyCapturer-v1.x-Code/src/ui/screenshot/screenshot.cpp":
        r"QScreen::grabWindow|grabWindow\(|connect\(m_bars, &PaintBar::sigScreenshotUpdate",
    "history-version-code/2nd-SunnyCapturer-v1.x-Code/src/ui/paint_bar/toolbar_level/paintbarhelper.h":
        r"enum class PaintType|PT_\w+[, ]|enum class OcrChannel|OCR_baidu_",
    "history-version-code/2nd-SunnyCapturer-v1.x-Code/src/ui/commom/ocr/networkocr.h":
        r"class NetworkOCR|void sendBaiDuOcrRequest|void onRequestFinished",
    "history-version-code/2nd-SunnyCapturer-v1.x-Code/src/ui/commom/communication/communication.cpp":
        r"new QHotkey\(|connect\(m_hk.*QHotkey::activated",
    "history-version-code/2nd-SunnyCapturer-v1.x-Code/src/data/configjson.h": r"class ConfigJson",
    "history-version-code/2nd-SunnyCapturer-v1.x-Code/src/data/configmanager.h": r"class ConfigManager",
}


def source_comparison():
    result = []
    for relative, pattern in SOURCE_QUERIES.items():
        data = (REPO / relative).read_bytes()
        hits, offset = [], 0
        for number, line in enumerate(data.splitlines(keepends=True), 1):
            text = line.decode("utf-8-sig").rstrip("\r\n")
            if re.search(pattern, text):
                hits.append(dict(line=number, **loc(offset, len(line)), text=text.strip()))
            offset += len(line)
        result.append(dict(path=relative, sha256=sha(data), search=loc(0, len(data)),
                           regex=pattern, hits=hits))
    return result


def self_check():
    # A hand-built export with a forwarded target checks RVA mapping and export ordinals.
    b = bytearray(2048)
    b[:2] = b"MZ"
    struct.pack_into("<I", b, 60, 128)
    b[128:132] = b"PE\0\0"
    struct.pack_into("<HH", b, 132, 0x8664, 1)
    struct.pack_into("<H", b, 148, 240)
    struct.pack_into("<H", b, 152, 0x20b)
    struct.pack_into("<Q", b, 176, 0x140000000)
    struct.pack_into("<I", b, 212, 512)
    struct.pack_into("<I", b, 260, 16)
    struct.pack_into("<II", b, 264, 0x1000, 128)
    struct.pack_into("<8sIIII", b, 392, b".rdata\0\0", 1536, 0x1000, 1536, 512)
    struct.pack_into("<IIHHIIIIIII", b, 512, 0, 0, 0, 0, 0x1080, 7, 1, 1, 0x1028, 0x102c, 0x1030)
    struct.pack_into("<IIH", b, 552, 0x1040, 0x1090, 0)
    b[576:591] = b"KERNEL32.Sleep\0"
    b[640:649] = b"fake.dll\0"
    b[656:661] = b"Test\0"
    pe = PE(bytes(b))
    assert pe.off(0x1000) == 512 and pe.off(20) == 20
    entry = pe.exports(True)["entries"][0]
    assert entry["ordinal"] == 7 and entry["forwarder"] == "KERNEL32.Sleep"
    assert entry["names"][0]["name"] == "Test"
    assert pe.imports(True) == []
    struct.pack_into("<I", b, 552, 0x1700)
    assert PE(bytes(b)).exports(True)["entries"][0]["file_offset"] is None
    struct.pack_into("<II", b, 272, 0x1200, 40)
    struct.pack_into("<IIIII", b, 1024, 0x1280, 0, 0, 0x1250, 0x12c0)
    b[1104:1117] = b"fake_dep.dll\0"
    struct.pack_into("<QQQ", b, 1152, 0x1300, (1 << 63) | 9, 0)
    b[1280:1291] = b"\0\0Function\0"
    imported = PE(bytes(b)).imports(True)[0]
    assert imported["dll"] == "fake_dep.dll" and imported["symbol_count"] == 2
    assert imported["symbols"][0]["name"] == "Function" and imported["symbols"][1]["ordinal"] == 9
    try:
        pe.off(0x9000)
    except ValueError:
        pass
    else:
        raise AssertionError("unbacked RVA accepted")
    print("self-check: OK (PE64, RVA bounds, named/forwarded/unbacked exports, named/ordinal imports)")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", type=Path, default=INSTALL)
    parser.add_argument("--output", type=Path, default=REPO / "docs/kairos/reverse/static/inventory.json")
    parser.add_argument("--self-check", action="store_true")
    args = parser.parse_args()
    if args.self_check:
        self_check()
        return
    root = args.install.resolve()
    data = (root / "SunnyCapturer.exe").read_bytes()
    assert len(data) == 3507712 and sha(data) == EXPECTED_SHA, "target identity mismatch; stopped"
    files, total, groups = [], 0, Counter()
    for path in sorted(root.rglob("*")):
        assert not path.is_symlink(), f"symlink outside inventory contract: {path.name}"
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        row = dict(path=relative, size=size, sha256=file_sha(path))
        with path.open("rb") as stream:
            if stream.read(2) == b"MZ":
                with mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as mapped:
                    pe = PE(mapped)
                    row["pe"] = pe.summary(path.name.lower() in DETAIL)
                    if path.name.lower() in DETAIL or path.name == "Qt6Core.dll":
                        row["resources"] = pe.resources()
        files.append(row)
        total += size
        groups[relative.split("/")[0] if "/" in relative else "<root>"] += size
    names = {Path(f["path"]).name.lower(): f["path"] for f in files}
    for row in files:
        for module in row.get("pe", {}).get("imports", []):
            module["installed_leaf_match"] = names.get(module["dll"].lower())
    output = dict(schema_version=1, tool="static_inventory.py; Python standard library",
                  script_sha256=file_sha(Path(__file__)), target_sha256=EXPECTED_SHA,
                  install_root=str(root), scope="installed artifacts plus explicit checkout source files; static only",
                  size_kind="sum of logical file lengths; not allocated disk size or download size",
                  file_count=len(files), total_bytes=total, total_gib=total / 1024 ** 3,
                  top_level_bytes=dict(sorted(groups.items())), files=files,
                  qt=qt_objects(PE(data)), strings=literal_search(data), checkout=source_comparison(),
                  limits=["No execution, decompilation, native xrefs or live settings inspection.",
                          "No REA session calls; provider absence is supplied task context, not re-tested.",
                          "Detailed import/export symbols only for SunnyCapturer.exe, sunny_ocr.dll, common.dll.",
                          "All other PE files: full imported module names/counts, export counts; no exported name list.",
                          "Qt scan: main EXE .rdata, 8-byte aligned, revision 12 only; methods/strings decoded, no code graph.",
                          "Strings: explicit literals only, plus simple MSVC class RTTI names; no compressed resource extraction.",
                          "No account/token/config values collected; inventories contain names, sizes and hashes.",
                          "Installed leaf matches do not simulate Windows DLL search order or LoadLibrary calls."])
    dest = args.output.resolve()
    assert dest.is_relative_to((REPO / "docs/kairos/reverse/static").resolve()), "output outside allowed docs path"
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(json.dumps(output, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(dict(output=str(dest), files=len(files), bytes=total,
                          qt_objects=len(output["qt"]["objects"])), ensure_ascii=False))


if __name__ == "__main__":
    main()
