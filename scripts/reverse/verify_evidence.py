"""Cross-check the independent PE inventory against retained REA resource evidence."""
import hashlib
import json
from pathlib import Path


def main():
    root = Path(__file__).resolve().parents[2]
    reports = root / "docs/kairos/reverse"
    inventory = json.loads((reports / "static/inventory.json").read_text(encoding="utf-8-sig"))
    bundle = json.loads((reports / "rea-evidence.json").read_text(encoding="utf-8-sig"))
    resource_records = [r for r in bundle["records"] if r["operation"] == "inspect_pe_resources"]
    assert len(resource_records) == 1, "Expected one resource inspection for the selected EXE"
    record = resource_records[0]
    resource_data = record["normalized_result"]
    exe = next(f for f in inventory["files"] if f["path"] == "SunnyCapturer.exe")
    digest = inventory["target_sha256"]
    assert digest == exe["sha256"] == resource_data["artifact"]["sha256"]
    assert exe["size"] == resource_data["artifact"]["bytes"]
    target = Path(inventory["install_root"]) / exe["path"]
    assert hashlib.sha256(target.read_bytes()).hexdigest() == digest, "Installed EXE changed"
    assert inventory["file_count"] == len(inventory["files"])
    assert inventory["total_bytes"] == sum(f["size"] for f in inventory["files"])
    independent = {(r["payload"]["offset"], r["payload"]["size"], r["sha256"])
                   for r in exe["resources"]}
    rea = {(r["payload"]["location"]["offset"], r["payload"]["location"]["bytes"], r["payload"]["sha256"])
           for r in resource_data["resources"]}
    assert independent == rea, "Resource offsets, sizes or digests disagree"
    assert len(rea) == len(exe["resources"]) == resource_data["coverage"]["resources"]
    runtime = json.loads((reports / "runtime-modules.json").read_text(encoding="utf-8-sig"))
    assert runtime["target_sha256"] == digest, "Runtime sample refers to a different on-disk target"
    qt = json.loads((reports / "qt/inventory.json").read_text(encoding="utf-8-sig"))
    assert qt["target"]["sha256"] == digest
    keys = {k["id"]: k for k in qt["source_index"]}
    spans = 0
    messages = 0
    for file in qt["files"]:
        raw = (target.parent / file["path"]).read_bytes()
        assert len(raw) == file["size"] and hashlib.sha256(raw).hexdigest() == file["sha256"]
        evidence = list(file["blocks"])
        for record in file["records"]:
            messages += 1
            evidence.extend([record, record["hash_entry"], *record["translations"]])
            for name in ("source", "context", "comment"):
                field = record[name]
                evidence.append(field)
                observed = None if field["null"] else raw[field["offset"]:field["offset"] + field["length"]].decode(field["encoding"])
                assert observed == keys[record["key_id"]][name], "QM source index disagrees with original bytes"
        for span in evidence:
            start, length = span["offset"], span["length"]
            assert 0 <= start <= len(raw) and 0 <= length <= len(raw) - start
            assert hashlib.sha256(raw[start:start + length]).hexdigest() == span["sha256"]
            spans += 1
    assert messages == qt["summary"]["messages"] and len(qt["files"]) == qt["summary"]["qm_files"]
    print(f"PASS: selected EXE identity, {inventory['file_count']} file totals, "
          f"{len(rea)} resource payloads independently matched, runtime identity.")
    print(f"PASS: {len(qt['files'])} QM files, {messages} messages, {spans} raw byte spans and source-index fields.")
    print("Coverage: static structure and one process sample; no editor or OCR behavioral equivalence asserted.")


if __name__ == "__main__":
    main()
