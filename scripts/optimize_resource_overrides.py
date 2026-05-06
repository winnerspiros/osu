#!/usr/bin/env python3
import argparse
import fnmatch
import json
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

SUPPORTED_INPUT_EXTS = {".png", ".jpg", ".jpeg", ".wav", ".mp3", ".mp4"}


@dataclass
class ConversionResult:
    source: str
    output: str
    source_bytes: int
    output_bytes: int
    saved_bytes: int
    replaced: bool


def run_ffmpeg(args: list[str]) -> None:
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", *args], check=True)


def should_include(path: Path, include_globs: list[str], exclude_globs: list[str], root: Path) -> bool:
    relative = path.relative_to(root).as_posix()
    included = any(fnmatch.fnmatch(relative, pattern) for pattern in include_globs)
    excluded = any(fnmatch.fnmatch(relative, pattern) for pattern in exclude_globs)
    return included and not excluded


def convert_file(source: Path, output: Path, config: dict) -> None:
    ext = source.suffix.lower()
    if ext == ".png":
        run_ffmpeg(["-i", str(source), "-c:v", "libwebp", "-lossless", "1", "-compression_level", "6", str(output)])
    elif ext in {".jpg", ".jpeg"}:
        run_ffmpeg(["-i", str(source), "-c:v", "libwebp", "-q:v", str(config["jpeg_webp_quality"]), str(output)])
    elif ext in {".wav", ".mp3"}:
        run_ffmpeg(["-i", str(source), "-c:a", "libvorbis", "-q:a", str(config["audio_ogg_quality"]), str(output)])
    elif ext == ".mp4":
        run_ffmpeg(
            [
                "-i",
                str(source),
                "-c:v",
                "libvpx-vp9",
                "-b:v",
                "0",
                "-crf",
                str(config["video_webm_crf"]),
                "-c:a",
                "libopus",
                "-b:a",
                str(config["video_webm_audio_bitrate"]),
                str(output),
            ]
        )
    else:
        raise ValueError(f"Unsupported source extension: {ext}")


def map_output_path(source: Path) -> Path:
    ext = source.suffix.lower()
    if ext in {".png", ".jpg", ".jpeg"}:
        return source.with_suffix(".webp")
    if ext in {".wav", ".mp3"}:
        return source.with_suffix(".ogg")
    if ext == ".mp4":
        return source.with_suffix(".webm")
    raise ValueError(f"Unsupported extension: {ext}")


def optimize(root: Path, config: dict, dry_run: bool) -> dict:
    include_globs = config.get("include_globs", ["**/*"])
    exclude_globs = config.get("exclude_globs", [])
    allow_video = bool(config.get("enable_video_conversion", True))
    keep_originals = bool(config.get("keep_original_files", True))

    results: list[ConversionResult] = []
    skipped: list[str] = []

    for source in root.rglob("*"):
        if not source.is_file():
            continue
        ext = source.suffix.lower()
        if ext not in SUPPORTED_INPUT_EXTS:
            continue
        if ext == ".mp4" and not allow_video:
            skipped.append(f"{source.relative_to(root).as_posix()} (video conversion disabled)")
            continue
        if not should_include(source, include_globs=include_globs, exclude_globs=exclude_globs, root=root):
            continue

        output = map_output_path(source)
        temp_output = source.parent / f".{output.name}.tmp"

        try:
            convert_file(source, temp_output, config=config)
        except subprocess.CalledProcessError:
            if temp_output.exists():
                temp_output.unlink()
            skipped.append(f"{source.relative_to(root).as_posix()} (ffmpeg conversion failed)")
            continue

        source_size = source.stat().st_size
        output_size = temp_output.stat().st_size

        if output_size >= source_size:
            temp_output.unlink(missing_ok=True)
            skipped.append(f"{source.relative_to(root).as_posix()} (no size win)")
            continue

        if not dry_run:
            output.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(temp_output), str(output))
            replaced = False
            if not keep_originals:
                source.unlink(missing_ok=True)
                replaced = True
        else:
            temp_output.unlink(missing_ok=True)
            replaced = False

        results.append(
            ConversionResult(
                source=source.relative_to(root).as_posix(),
                output=output.relative_to(root).as_posix(),
                source_bytes=source_size,
                output_bytes=output_size,
                saved_bytes=source_size - output_size,
                replaced=replaced,
            )
        )

    total_saved = sum(r.saved_bytes for r in results)
    return {
        "converted_count": len(results),
        "saved_bytes_total": total_saved,
        "converted": [r.__dict__ for r in sorted(results, key=lambda r: r.saved_bytes, reverse=True)],
        "skipped": skipped,
        "dry_run": dry_run,
    }


def write_summary(report: dict, summary_path: Path) -> None:
    lines = [
        "## Resource override optimization report",
        "",
        f"- Converted assets: `{report['converted_count']}`",
        f"- Estimated bytes saved: `{report['saved_bytes_total']}`",
        f"- Dry run: `{report['dry_run']}`",
        "",
        "### Largest wins",
    ]
    for item in report["converted"][:20]:
        lines.append(
            f"- `{item['source']}` -> `{item['output']}` "
            f"(saved {item['saved_bytes']} bytes; {item['source_bytes']} -> {item['output_bytes']})"
        )

    if report["skipped"]:
        lines.append("")
        lines.append("### Skipped")
        for item in report["skipped"][:50]:
            lines.append(f"- {item}")

    summary_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--report-file", required=True)
    parser.add_argument("--summary-file", required=True)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--skip-if-no-ffmpeg", action="store_true")
    args = parser.parse_args()

    root = Path(args.root)
    config_path = Path(args.config)
    report_path = Path(args.report_file)
    summary_path = Path(args.summary_file)

    if shutil.which("ffmpeg") is None:
        if args.skip_if_no_ffmpeg:
            report = {
                "converted_count": 0,
                "saved_bytes_total": 0,
                "converted": [],
                "skipped": ["ffmpeg is not available on PATH (skipped by --skip-if-no-ffmpeg)."],
                "dry_run": args.dry_run,
            }
            report_path.parent.mkdir(parents=True, exist_ok=True)
            report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            write_summary(report, summary_path)
            return 0

        print("::error::ffmpeg is required but not available on PATH.")
        return 1

    if not root.exists():
        report = {
            "converted_count": 0,
            "saved_bytes_total": 0,
            "converted": [],
            "skipped": [f"Root path does not exist: {root}"],
            "dry_run": args.dry_run,
        }
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        write_summary(report, summary_path)
        return 0

    config = json.loads(config_path.read_text(encoding="utf-8"))
    report = optimize(root, config=config, dry_run=args.dry_run)

    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    write_summary(report, summary_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
