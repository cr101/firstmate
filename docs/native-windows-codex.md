# Experimental native Windows Codex launcher

This explicit opt-in launcher is a restricted experimental candidate, not an installed runtime backend or a production-ready integration.
Ordinary startup never selects it, and it does not install hooks, alter saved or global Codex settings, pull images, or select a default backend.
[`verification/runtime-backends.md`](verification/runtime-backends.md#experimental-native-windows-ownership-candidate) provides the verification refresh entry points; no current-build output is recorded there yet.

## Setup

The launcher requires Windows PowerShell 5.1, native Node and Codex, Git Bash, and Docker at their standard installation paths.
It also requires an existing local Docker image containing jq and GNU timeout; the launcher does not install or pull it.
The PowerShell help for `bin/fm-native-codex.ps1` owns the exact flag mechanics.

Build without launching, then verify an empty temporary operational home:

```powershell
.\bin\fm-native-codex.ps1 -BuildOnly
.\bin\fm-native-codex.ps1 -Experimental -VerifyOnly `
  -OperationalHome "$env:LOCALAPPDATA\Temp\firstmate-native-example" -JqImage <existing-local-image>
```

Omit `-VerifyOnly` for an interactive model session.
Rebuild the native provider after its source stamp changes and after all native sessions have stopped.
Use `/interrupt` to interrupt the current model turn and `/quit` to end the session.

## Safety boundary and limits

Only empty-fleet homes beneath the current user's Windows temporary directory are accepted.
Existing fleet metadata, projects, registrations, Relay configuration, process-event sources, non-temporary homes, and existing reparse-point ancestors are refused.
The app-server thread is ephemeral, read-only, network-disabled, and approval-never; Apps, plugins, and configured MCP servers are disabled for this host and their effective catalogs are checked before readiness.
Only controller-selected startup, notification check, and acknowledgement scripts receive registered native operation authority.
Those operations and their descendants are owned by the native session: deferred startup may outlive the digest shell but `/quit` cancels it without terminating independently owned workers, and unfinished startup may run again after restart.
Interrupted or ambiguous acknowledgements remain preserved for evidence-based reconciliation and are never replayed or rolled back automatically.

Production use remains disabled.
Populated-fleet shutdown, forced app-server descendant cleanup, adversarial Windows path and process races, remaining numeric identity readers, other harnesses, installation, and packaging are not supported or claimed.
Ordinary unelevated Codex shell execution of Git Bash remains a known limitation; the narrow authenticated host operations are not a general sandbox fix.
