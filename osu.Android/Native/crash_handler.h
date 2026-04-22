// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#pragma once

extern "C" {
// Install a process-wide native crash handler.  `logPath` is the absolute
// path of the file the dump will be appended to (typically the app's
// external files directory).  Pass nullptr to log only to logcat.
// Idempotent: subsequent calls after the first are no-ops.
void nInstallCrashHandler(const char* logPath);

// Re-install the signal handlers, even if a previous install already ran.
// Use this after the Mono runtime is fully up so our handler chains *on top
// of* Mono's SIGSEGV handler — otherwise Mono intercepts JIT-NRE faults
// first and re-raises via `tgkill` (which appears in tombstones as
// `si_code = SI_TKILL`), bypassing our dump entirely.  The previously-saved
// "previous handler" slot is overwritten with whatever is currently
// installed (typically Mono's), so chaining still works.
void nReinstallCrashHandler();
}
