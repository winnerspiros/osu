#!/usr/bin/env python3
import argparse
import json
import os
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path

IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".webp", ".ktx2", ".astc"}
AUDIO_EXTS = {".wav", ".mp3", ".ogg", ".m4a", ".flac", ".aac"}
VIDEO_EXTS = {".mp4", ".webm", ".mkv", ".avi", ".mov"}
MEDIA_EXTS = IMAGE_EXTS | AUDIO_EXTS | VIDEO_EXTS


@dataclass
class MediaEntry:
    path: str
    size: int
    ext: str

    @property
    def bucket(self) -> str:
        if self.ext in IMAGE_EXTS:
            return "image"
        if self.ext in AUDIO_EXTS:
            return "audio"
        if self.ext in VIDEO_EXTS:
            return "video"
        return "other"


def collect_from_dir(root: Path) -> list[MediaEntry]:
    entries: list[MediaEntry] = []
    if not root.exists():
        return entries

    for path in root.rglob("*"):
        if not path.is_file():
            continue
        ext = path.suffix.lower()
        if ext not in MEDIA_EXTS:
            continue
        relative_path = str(path.relative_to(root)).replace("\\", "/")
        file_size = path.stat().st_size
        entries.append(MediaEntry(path=relative_path, size=file_size, ext=ext))
    return entries


def collect_from_apk(apk_path: Path) -> list[MediaEntry]:
    entries: list[MediaEntry] = []
    with zipfile.ZipFile(apk_path, "r") as zf:
        for info in zf.infolist():
            if info.is_dir():
                continue
            ext = Path(info.filename).suffix.lower()
            if ext not in MEDIA_EXTS:
                continue
            entries.append(MediaEntry(path=info.filename, size=info.file_size, ext=ext))
    return entries


def sum_bucket(entries: list[MediaEntry], bucket: str) -> int:
    return sum(e.size for e in entries if e.bucket == bucket)


def build_report(entries: list[MediaEntry], top_n: int) -> dict:
    ordered = sorted(entries, key=lambda e: e.size, reverse=True)
    return {
        "counts": {
            "media_files": len(entries),
            "images": sum(1 for e in entries if e.bucket == "image"),
            "audio": sum(1 for e in entries if e.bucket == "audio"),
            "video": sum(1 for e in entries if e.bucket == "video"),
        },
        "sizes": {
            "total_bytes": sum(e.size for e in entries),
            "image_bytes": sum_bucket(entries, "image"),
            "audio_bytes": sum_bucket(entries, "audio"),
            "video_bytes": sum_bucket(entries, "video"),
            "largest_file_bytes": ordered[0].size if ordered else 0,
            "top_n_total_bytes": sum(e.size for e in ordered[:top_n]),
        },
        "largest_files": [{"path": e.path, "size_bytes": e.size} for e in ordered[:top_n]],
    }


def check_limits(report: dict, budget: dict, apk_size: int | None) -> list[str]:
    failures: list[str] = []
    sizes = report["sizes"]

    limits = {
        "max_total_bytes": sizes["total_bytes"],
        "max_image_bytes": sizes["image_bytes"],
        "max_audio_bytes": sizes["audio_bytes"],
        "max_video_bytes": sizes["video_bytes"],
        "max_largest_file_bytes": sizes["largest_file_bytes"],
        "max_top_n_total_bytes": sizes["top_n_total_bytes"],
    }

    for budget_key, actual in limits.items():
        max_allowed = budget.get(budget_key)
        if max_allowed is not None and actual > max_allowed:
            failures.append(f"{budget_key} exceeded: {actual} > {max_allowed}")

    if apk_size is not None and budget.get("max_apk_bytes") is not None and apk_size > budget["max_apk_bytes"]:
        failures.append(f"max_apk_bytes exceeded: {apk_size} > {budget['max_apk_bytes']}")

    return failures


def write_summary(report: dict, budget_path: str, failures: list[str], summary_path: Path, apk_size: int | None) -> None:
    lines: list[str] = []
    lines.append("## Resource budget report")
    lines.append("")
    lines.append(f"- Budget file: `{budget_path}`")
    if apk_size is not None:
        lines.append(f"- APK size bytes: `{apk_size}`")
    lines.append(f"- Media files: `{report['counts']['media_files']}`")
    lines.append(f"- Image bytes: `{report['sizes']['image_bytes']}`")
    lines.append(f"- Audio bytes: `{report['sizes']['audio_bytes']}`")
    lines.append(f"- Video bytes: `{report['sizes']['video_bytes']}`")
    lines.append(f"- Total media bytes: `{report['sizes']['total_bytes']}`")
    lines.append("")
    lines.append("### Top files")
    for file in report["largest_files"]:
        lines.append(f"- `{file['path']}` ({file['size_bytes']} bytes)")
    lines.append("")
    if failures:
        lines.append("### ❌ Budget failures")
        for failure in failures:
            lines.append(f"- {failure}")
    else:
        lines.append("### ✅ Budgets passed")

    summary_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["source", "apk"], required=True)
    parser.add_argument("--source-root")
    parser.add_argument("--apk-path")
    parser.add_argument("--budget-file", required=True)
    parser.add_argument("--report-file", required=True)
    parser.add_argument("--summary-file", required=True)
    args = parser.parse_args()

    budget_path = Path(args.budget_file)

    try:
        budget = json.loads(budget_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValueError(f"Invalid JSON in budget file '{str(budget_path)}': {exc}") from exc

    if not isinstance(budget, dict):
        raise ValueError(f"Budget file '{str(budget_path)}' must contain a JSON object at the root.")

    try:
        top_n = int(budget.get("top_n", 15))
    except (TypeError, ValueError) as exc:
        raise ValueError(f"Budget file '{str(budget_path)}' contains invalid 'top_n': {budget.get('top_n')} (must be an integer).") from exc

    apk_size: int | None = None
    if args.mode == "source":
        if not args.source_root:
            raise ValueError("--source-root is required for source mode")
        entries = collect_from_dir(Path(args.source_root))
    else:
        if not args.apk_path:
            raise ValueError("--apk-path is required for apk mode")
        apk_path = Path(args.apk_path)
        apk_size = apk_path.stat().st_size
        entries = collect_from_apk(apk_path)

    report = build_report(entries, top_n=top_n)
    failures = check_limits(report, budget=budget, apk_size=apk_size)

    report_path = Path(args.report_file)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    summary_path = Path(args.summary_file)
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    write_summary(report, str(budget_path), failures, summary_path, apk_size)

    if failures:
        for f in failures:
            print(f"::error::{f}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
