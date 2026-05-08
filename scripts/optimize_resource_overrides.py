#!/usr/bin/env python3
import argparse
import fnmatch
import json
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Optional, Tuple

SUPPORTED_INPUT_EXTS = {".png", ".jpg", ".jpeg", ".wav", ".mp3", ".mp4"}


@dataclass
class ConversionResult:
    source: str
    output: str
    source_bytes: int
    output_bytes: int
    saved_bytes: int
    replaced: bool
    strategy: str


def run_ffmpeg(args: list[str], output_log_level: str = "error") -> None:
    subprocess.run(["ffmpeg", "-y", "-loglevel", output_log_level, *args], check=True, capture_output=True, text=True)


def should_include(path: Path, include_globs: list[str], exclude_globs: list[str], root: Path) -> bool:
    relative = path.relative_to(root).as_posix()
    included = any(fnmatch.fnmatch(relative, pattern) for pattern in include_globs)
    excluded = any(fnmatch.fnmatch(relative, pattern) for pattern in exclude_globs)
    return included and not excluded


def ffprobe_has_alpha_channel(source: Path) -> bool:
    try:
        result = subprocess.run(
            [
                "ffprobe",
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=pix_fmt",
                "-of",
                "default=noprint_wrappers=1:nokey=1",
                str(source),
            ],
            check=True,
            capture_output=True,
            text=True,
        )
        pix_fmt = (result.stdout or "").strip().lower()
        return "a" in pix_fmt
    except (subprocess.CalledProcessError, FileNotFoundError):
        return True


def measure_image_ssim(source: Path, candidate: Path) -> Optional[float]:
    result = subprocess.run(
        [
            "ffmpeg",
            "-i",
            str(source),
            "-i",
            str(candidate),
            "-lavfi",
            "ssim",
            "-f",
            "null",
            "-",
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    combined = f"{result.stdout}\n{result.stderr}"
    match = re.search(r"All:(\d+(?:\.\d+)?)", combined)
    if not match:
        return None
    return float(match.group(1))


def matches_any_glob(relative_path: str, patterns: list[str]) -> bool:
    return any(fnmatch.fnmatch(relative_path, pattern) for pattern in patterns)


def convert_image_to_webp(source: Path, output: Path, config: dict, relative_path: str) -> str:
    ext = source.suffix.lower()
    temp_candidates: list[tuple[Path, str]] = []
    has_alpha = ext == ".png" and ffprobe_has_alpha_channel(source)

    if ext == ".png" and bool(config.get("png_webp_try_lossless", True)):
        lossless_path = source.parent / f".{output.name}.lossless.tmp"
        run_ffmpeg(
            [
                "-i",
                str(source),
                "-c:v",
                "libwebp",
                "-lossless",
                "1",
                "-compression_level",
                str(config.get("png_webp_lossless_compression_level", 6)),
                "-f",
                "webp",
                str(lossless_path),
            ]
        )
        temp_candidates.append((lossless_path, "png-lossless-webp"))

    lossy_quality = (
        config.get("png_webp_alpha_lossy_quality", 95) if has_alpha else config.get("png_webp_lossy_quality", 92)
    ) if ext == ".png" else config.get("jpeg_webp_quality", 88)
    lossy_method = config.get("png_webp_lossy_method", 6) if ext == ".png" else config.get("jpeg_webp_method", 6)
    lossy_path = source.parent / f".{output.name}.lossy.tmp"
    run_ffmpeg(
        [
            "-i",
            str(source),
            "-c:v",
            "libwebp",
            "-q:v",
            str(lossy_quality),
            "-compression_level",
            str(lossy_method),
            "-f",
            "webp",
            str(lossy_path),
        ]
    )
    lossy_strategy = f"{ext.lstrip('.')}-lossy-webp-q{lossy_quality}"
    temp_candidates.append((lossy_path, lossy_strategy))

    minimum_ssim = float(config.get("image_lossy_min_ssim", 0.995))
    compare_ssim = bool(config.get("measure_image_ssim", True))
    best_candidate: Optional[Tuple[Path, str]] = None

    for candidate_path, strategy in temp_candidates:
        if strategy == lossy_strategy and compare_ssim:
            ssim = measure_image_ssim(source, candidate_path)
            if ssim is not None and ssim < minimum_ssim:
                candidate_path.unlink(missing_ok=True)
                continue
        if best_candidate is None or candidate_path.stat().st_size < best_candidate[0].stat().st_size:
            best_candidate = (candidate_path, strategy)

    if best_candidate is None:
        for candidate_path, _ in temp_candidates:
            candidate_path.unlink(missing_ok=True)
        raise RuntimeError(
            f"No image candidate met quality threshold for {relative_path}. "
            f"Consider lowering image_lossy_min_ssim in config."
        )

    best_path, best_strategy = best_candidate
    shutil.move(str(best_path), str(output))
    for candidate_path, _ in temp_candidates:
        if candidate_path != best_path:
            candidate_path.unlink(missing_ok=True)
    return best_strategy


def convert_audio_to_ogg(source: Path, output: Path, config: dict, relative_path: str) -> str:
    audio_codec = str(config.get("audio_codec", "libopus"))
    target_sample_rate = int(config.get("audio_target_sample_rate_hz", 48000))
    target_channels = int(config.get("audio_target_channels", 2))
    force_mono_globs = [str(p) for p in config.get("audio_force_mono_globs", [])]
    channel_count = 1 if matches_any_glob(relative_path, force_mono_globs) else target_channels

    base_args = ["-i", str(source), "-ar", str(target_sample_rate), "-ac", str(channel_count)]
    if audio_codec == "libopus":
        bitrate = str(config.get("audio_opus_bitrate", config.get("audio_ogg_bitrate", "96k")))
        opus_application = str(config.get("audio_opus_application", "audio"))
        frame_duration = str(config.get("audio_opus_frame_duration_ms", 20))
        run_ffmpeg(
            [
                *base_args,
                "-c:a",
                "libopus",
                "-b:a",
                bitrate,
                "-vbr",
                "on",
                "-application",
                opus_application,
                "-frame_duration",
                frame_duration,
                "-f",
                "ogg",  # explicit muxer — ffmpeg cannot infer Ogg from .tmp extension
                str(output),
            ]
        )
        return f"ogg-opus-{bitrate}-{target_sample_rate}hz-{channel_count}ch"

    if audio_codec == "libvorbis":
        quality = str(config.get("audio_ogg_quality", 4))
        run_ffmpeg([*base_args, "-c:a", "libvorbis", "-q:a", quality, "-f", "ogg", str(output)])
        return f"ogg-vorbis-q{quality}-{target_sample_rate}hz-{channel_count}ch"

    raise ValueError(f"Unsupported audio codec: {audio_codec}")


def convert_video_to_webm(source: Path, output: Path, config: dict) -> str:
    crf = str(config.get("video_webm_crf", 34))
    audio_bitrate = str(config.get("video_webm_audio_bitrate", "96k"))
    run_ffmpeg(
        [
            "-i",
            str(source),
            "-c:v",
            "libvpx-vp9",
            "-b:v",
            "0",
            "-crf",
            crf,
            "-deadline",
            str(config.get("video_webm_deadline", "good")),
            "-cpu-used",
            str(config.get("video_webm_cpu_used", 4)),
            "-row-mt",
            "1",
            "-tile-columns",
            str(config.get("video_webm_tile_columns", 2)),
            "-c:a",
            "libopus",
            "-b:a",
            audio_bitrate,
            "-f",
            "webm",  # explicit muxer — ffmpeg cannot infer WebM from .tmp extension
            str(output),
        ]
    )
    return f"webm-vp9-crf{crf}-opus-{audio_bitrate}"


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
        relative_path = source.relative_to(root).as_posix()

        try:
            if ext in {".png", ".jpg", ".jpeg"}:
                strategy = convert_image_to_webp(source, temp_output, config=config, relative_path=relative_path)
            elif ext in {".wav", ".mp3"}:
                strategy = convert_audio_to_ogg(source, temp_output, config=config, relative_path=relative_path)
            elif ext == ".mp4":
                strategy = convert_video_to_webm(source, temp_output, config=config)
            else:
                raise ValueError(f"Unsupported source extension: {ext}")
        except (subprocess.CalledProcessError, RuntimeError, ValueError) as exc:
            if temp_output.exists():
                temp_output.unlink()
            details = str(exc)
            if isinstance(exc, subprocess.CalledProcessError):
                details = ((exc.stderr or "").strip() or details).replace("\n", " ")
            if len(details) > 200:
                details = f"{details[:200]}..."
            suffix = f": {details}" if details else ""
            skipped.append(f"{source.relative_to(root).as_posix()} (conversion failed{suffix})")
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
                strategy=strategy,
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
            f"(saved {item['saved_bytes']} bytes; {item['source_bytes']} -> {item['output_bytes']}; "
            f"strategy `{item.get('strategy', 'n/a')}`)"
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
