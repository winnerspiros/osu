// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#pragma once

#include <stdint.h>

extern "C" {

// Start the native pthread-based liveness watchdog.
//
// This watchdog exists because the managed C# HangWatchdog cannot observe the
// failure mode where one Mono thread sits in a long native call while the
// runtime is mid stop-the-world GC: every other managed thread (including the
// managed watchdog's monitor Thread) is suspended via SIGRTMIN+N → sigsuspend
// and cannot run.  A pure pthread-only loop that never enters managed code,
// never allocates, and never touches the Mono runtime is the only thing that
// can produce a diagnostic dump under that condition.
//
// Parameters:
//   logPath       – absolute path to append the dump to (typically
//                   `<internal-files-dir>/native_crash.log`).  May be NULL or
//                   empty; in that case the watchdog still runs but only emits
//                   to logcat.
//   hangSeconds   – number of seconds without a heartbeat before a dump is
//                   triggered.  Reasonable values are 5–15.
//
// Idempotent: calling more than once is a no-op after the first successful
// start.  Never throws; failures are reported to logcat and otherwise ignored.
__attribute__((visibility("default")))
void osu_native_watchdog_start(const char* logPath, int32_t hangSeconds);

// Bump the heartbeat timestamp.  Called from the managed Update-thread
// heartbeat tick (see HangWatchdog.Heartbeat.tick()).  Async-signal-safe:
// it performs only one __atomic_store_n on a 64-bit slot.
__attribute__((visibility("default")))
void osu_native_watchdog_heartbeat();

}
