# Compiled account UI fixtures

From the worktree root:

```powershell
.\scripts\test_account_ui.ps1
```

The harness selects the existing user-local .NET 8 SDK, builds this standalone
project and the Web reference, and writes fixtures to
`tools\WebsitePreview\bin\fixtures`. Use `-OutputDirectory` to choose another
directory **inside this worktree**, and `-DotNet` to select another compatible
SDK. `global.json` remains authoritative.

The host loads Web's **compiled Razor items**, never its controllers or Program.
Only inert fixture controllers are registered. The host binds HTTP only to
`127.0.0.1` on an OS-assigned port and exercises Web's existing trusted-loopback
forwarded HTTPS path. A test-only handler models Secure-cookie forwarding;
requests without the HTTPS proxy header exercise the real 403 guard.
Data-protection keys are ephemeral. No certificate is installed or persisted:
this workstation's Schannel cannot use ephemeral TLS keys.
The host checks responses, writes HTML plus a status/hash manifest, then stops.

The fixture states cover initial, field errors, conflict, unavailable, success,
long/untrusted model strings, exception masking, expired forms, real limiter
rejection, absent retry metadata, blocked HTTP, and missing optional setup URL.
Antiforgery/request-size probes POST only to an inert fixture action. No database
or real account is accessed. No production POST is sent.

The initial fixture explicitly marks its sentinel ModelState entries valid and
asserts that no correction alert is displayed. Its setup URL must match the
application configuration. A page with correct geometry but the wrong state or
destination is not valid review evidence.

Saved antiforgery tokens belong only to the disposed ephemeral fixture host.
These files are visual/a11y inputs, not working registration pages. The project
is not in the solution, cannot be published, and is never referenced by Web.
Browser, physical-device, real-registration, TLS, proxy-CSP and deployment acceptance
remain separate checks.

## Browser evidence

After the compiled fixture command completes, run the checked-in browser matrix:

```powershell
python .\scripts\check_website_browser.py --account-fixtures .\tools\WebsitePreview\bin\fixtures --output .\.impeccable\review\browser --capture .\.impeccable\review --engine chromium --channel msedge
python .\scripts\check_website_browser.py --account-fixtures .\tools\WebsitePreview\bin\fixtures --output .\.impeccable\review\browser --engine webkit
```

The browser-only dependency is pinned in `requirements-browser.txt`; it is not
a website runtime dependency. If Python reports a missing Playwright package,
install it with `python -m pip install -r .\tools\WebsitePreview\requirements-browser.txt`.
If the WebKit launch reports a missing browser, use
`python -m playwright install webkit`. The Edge run uses an existing Microsoft
Edge installation; no browser security flags or production CSP changes are needed.

The helper records expected missing-image cases separately, but fails on
unexpected page/request errors, overflow, contrast, target, description or
fixture-state errors. Captures use the current compiled HTML, not reconstructed
mockups. Review full originals as well as aggregate measurements.

On Windows, run Web startup probes after builds, not concurrently against the
same output DLL. Do not run a fixture rebuild while a browser evidence pass is
reading those fixture files.
