// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#pragma once

extern "C" {
// Install a process-wide native crash handler.  `logPath` is the absolute
// path of the file the dump will be appended to (typically the app's
// external files directory).  Pass nullptr to log only to logcat.
// Idempotent: subsequent calls after the first are no-ops.
void nInstallCrashHandler(const char* logPath);
}
