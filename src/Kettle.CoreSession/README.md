# CoreSession

Start `Kettle.exe` normally: CoreSession is the only game-session backend. `--core-session` remains accepted for existing shortcuts.

`Kettle.CoreSession.exe` is native C++/Win32. One CoreSession process owns ConPTY and starts SteamCMD directly, without a second CoreSession or TerminalHost process. The broker is created with `CREATE_NO_WINDOW`. CoreSession owns one English native running window (artwork, status, and a single fixed `Stop` button). Stop is disabled outside an actively monitored game; it never changes into a retry or reopen action. Kettle is closed throughout normal preparation, game execution and final synchronization. Kettle reopens only for conflicts, errors or completion.

After a successful handoff, the library process exits without disposing the Steam session. CoreSession suppresses frontend relaunch while native work is active. When attention is needed it launches Kettle with `--core-session-resume <session-directory>`; this frontend reads state through the control pipe without acquiring the raw SteamCMD channel. After resolving a conflict, PARK confirms whether Kettle can exit again without disabling subsequent relaunch. CoreSession delegates preparation to CloudHost and waits for SteamCMD to confirm the helper has exited before sending `app_launch` once. It monitors SteamCMD game-process events; after Stop it waits for all reported game PIDs to disappear before requesting final synchronization. It does not poll `apps_running`. A launch-failed response returns to Kettle without running the finish/upload phase. Only idle/completed states can attach the raw channel and reopen the library.

Authenticated sessions register credential-free `data/sessions/<token>/connection.json`. CoreSession watches the frontend process handle and relaunches it after unexpected termination (at most three attempts per rolling minute). Explicit close uses DETACH/CLOSE and disables automatic relaunch. After the restart limit, manual recovery uses `Kettle.exe --core-session-resume <session-directory>`. This also covers library crashes after login. The frontend is watched from the first pipe connection, including login. If it exits before authenticated registration, CoreSession terminates its own pre-game SteamCMD job and exits; it does not attach a replacement UI as authenticated. Login itself is not automatically resumed.

The separate managed TerminalHost project and its build targets have been removed. Startup update checks and the legacy managed terminal use CoreSession's `--terminal <executable> [arguments...]` compatibility entry point to repair inherited console handles. This mode does not create a broker or another CoreSession. Broker mode starts SteamCMD directly; one CoreSession remains during gameplay. The old CLI aliases are removed. Existing `data/assistant` journals are retained for saved-data compatibility; native resume uses `--core-session-resume`. Close an older running session before updating binaries.

STATUS reports phase, busy state, job directory, message and conflict ticket. UTF-8 text fields are hex encoded to prevent newline/delimiter injection. CHOOSE requires the current ticket; stale or duplicate answers are rejected. CloudHost has already exited when CoreSession waits for the choice. Killing Kettle does not dismiss the conflict or select a default. Reopening obtains the same pending conflict and the full existing dialog. STOP and RETRY never submit raw commands from the frontend.

## CloudHost

`--sync <job-directory> prepare|finish none|local|cloud <ticket-or-dash> <request-id>` executes the existing `CloudSyncService` / AutoCloud implementation inside CloudHost. Steam calls use `NativeCloudRpc` in the same short-lived x86 AOT process. The original per-RPC mode and managed frontend game lifecycle are removed. CloudHost references Kettle.Shared (contracts and common utilities), not Kettle.Core. All rule parsing, scanning, transfer, conflict resolution and backup decisions live in CloudHost. `--maintain-backups <app-directory>` and `--backup <source> <destination>` also execute there, so the frontend does not perform save operations.

Worker failures carry structured error codes and literal details in JSON. Kettle localizes these for the conflict/recovery UI; CloudHost has no language resources. The native window retains a diagnostic fallback message.

The native parent configures `DOTNET_GCSegmentSize=8000000` (hex, 128 MiB) for CloudHost alone, and a default 64 MiB no-GC region with 32 MiB SOH / 32 MiB LOH allocation budgets. `KETTLE_CLOUD_NOGC_MB` can override the total budget (32–1536 MiB). The SOH request is a ceiling, capped at half of the total budget so older helpers requesting 64 MiB SOH cannot leave a zero LOH budget. The reservation must succeed before synchronization. Collection counts and latency mode are checked across synchronization and RPC boundaries. The process exits without calling `EndNoGCRegion`. This is a bounded no-GC region, not removal of the .NET GC; exhausting the budget can resume collection, and is reported as an unconfirmed operation. File size is not the cumulative allocation size.

## Persistence and failure rules

- `data/assistant/<run-id>/job.json` is credential-free input.
- `cloud-state.json` preserves the preparation/snapshot and confirmed sync completion.
- `launch-issued` is persisted before the one-shot launch command. It prevents replacing a launch snapshot.
- `game-ended` is persisted only after verified start and exit. CloudHost refuses finishing without it.
- `native-state` tracks progress. Startup detects unfinished runs for the same account/client; confirmed exits can resume sync, while unknown exits block automatic launch/upload.
- `result.json` and the native-friendly `result.ini` include the invocation ID. A matching success result **and** exit code zero are required.
- Conflict choices require the original ticket; the existing synchronization implementation revalidates both versions.
- A Steam command timeout poisons the native command channel. It does not resend a launch or interpret timeout as exit. This initial implementation deliberately requires investigation/recovery in that case.
- A moved installation path on recovery is rejected rather than reusing another selected game's path.

No password, login code, or raw Steam transcript is written by the assistant. Native stage tracing is compiled out in Release. Unknown-exit recovery is conservative; it does not guess that an interrupted game ended. The assistant cannot protect against termination of the whole Winlator container.

## Validation

`Build-Release.ps1` publishes Kettle, CoreSession and CloudHost in Release, removes generated symbols from the output and checks the native executable. CoreSession's standalone `build.ps1 -Configuration Release` writes to `bin/Release` and omits debug tracing. Run `clang-format -i main.cpp` in this directory to apply the checked-in formatting rules.

`build.ps1` uses the installed MSVC C++ toolchain and statically links its C++ runtime. `--self-test` tests the native parser without Steam. `tests/Kettle.CoreSessionChecks/run.ps1` builds an isolated fake SteamCMD, verifies frontend exit, native run monitoring, no-cloud prepare/finish in the published CloudHost, return/reattachment, and failure guards. It does not exercise live Steam cloud HTTP/API behavior or establish Wine/Box64 compatibility.

Save payloads are staged on disk and copied/hashed with pooled 64 KiB buffers. Uploads read locked staged files by range; downloads use a disk-backed wire file for ZIP seeking and verify the extracted payload before local replacement. Metadata, HTTP/TLS and native libraries still allocate memory; the no-GC budget is not a total process-memory limit.
