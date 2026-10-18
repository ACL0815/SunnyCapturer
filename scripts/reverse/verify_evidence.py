"""Cross-check retained PE, Qt and native REA evidence against independent bytes."""
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
    native_root = reports / "native"
    native_bundle = json.loads((native_root / "rea-evidence-bundle.json").read_text(encoding="utf-8-sig"))
    native_records = {r["evidence_id"]: r for r in native_bundle["records"]}
    assert len(native_records) == len(native_bundle["records"]) == 44, "Native record set changed"
    index = json.loads((native_root / "evidence-index.json").read_text(encoding="utf-8-sig"))
    retained_ids = set()
    assert len(index) == len(list((native_root / "evidence").glob("*.json"))) == 49
    for item in index:
        path = native_root / item["file"]
        raw = path.read_bytes()
        assert hashlib.sha256(raw).hexdigest() == item["sha256"], f"Native evidence bytes changed: {item['file']}"
        response = json.loads(raw.decode("utf-8-sig"))
        assert not response["isError"] and not item["isError"]
        assert response["request"] == {"name": item["tool"], "arguments": item["arguments"]}
        if item["evidence_id"]:
            evidence = response["response"]
            assert evidence["evidence_id"] == item["evidence_id"]
            assert evidence == native_records[item["evidence_id"]], "Bundle lost or changed a complete record"
            retained_ids.add(item["evidence_id"])
    assert retained_ids == set(native_records)
    dossiers = [r for r in native_records.values() if r["operation"] == "analyze_function"]
    assert len(dossiers) == 5
    for record in native_records.values():
        assert record["subject"]["digest"]["sha256"] == digest
        assert record["analysis_profile"]["digest"] == "249564812a8d83b9b3f461098fc485ccde60c516d4497547a124081c91d654bf"
    for dossier in dossiers:
        result = dossier["normalized_result"]
        assert result["procedure"]["body"]["available"] and result["procedure"]["body"]["contains_entry"]
        flow = result["native_value_flow"]
        assert flow["available"] and not flow["truncated"]
        assert flow["known_omitted_edges"] == flow["known_omitted_inputs"] == flow["omitted_operations_lower_bound"] == 0
    close = json.loads((native_root / "evidence/049-close.json").read_text(encoding="utf-8-sig"))
    assert close["request"]["name"] == "close_binary" and close["response"]["result"] is None
    print(f"PASS: selected EXE identity, {inventory['file_count']} file totals, "
          f"{len(rea)} resource payloads independently matched, runtime identity.")
    print(f"PASS: {len(qt['files'])} QM files, {messages} messages, {spans} raw byte spans and source-index fields.")
    print(f"PASS: {len(index)} native response hashes, {len(native_records)} complete records, {len(dossiers)} full dossiers and recorded session close.")
    print("Coverage: static structure and one process sample; no editor or OCR behavioral equivalence asserted.")


if __name__ == "__main__":
    main()
