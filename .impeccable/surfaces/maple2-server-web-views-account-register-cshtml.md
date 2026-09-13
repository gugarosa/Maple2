---
version: 1
slug: "maple2-server-web-views-account-register-cshtml"
primary_target: "Maple2.Server.Web/Views/Account/Register.cshtml"
related_targets: ["Maple2.Server.Web/Views/Account/Notice.cshtml","Maple2.Server.Web/Views/Shared/_AccountLayout.cshtml","Maple2.Server.Web/Helpers/PlayerTheme.cs","Maple2.Server.Web/Helpers/RegistrationResponses.cs","Maple2.Server.Web/Helpers/AccountPageProtection.cs","Maple2.Server.Web/ViewResults/AccountNoticeResult.cs","Maple2.Server.Web/Filters/AccountPageAttribute.cs","Maple2.Server.Web/Maple2.Server.Web.csproj"]
---

# Surface brief: Registration and account notices

Visitor mode: **Operate**.

## Built strategy

An invited tester completes one account action or finds a safe recovery path.
The first viewport has the cube/MapleTime identity, a configured setup return,
one task heading and the relevant form, result or notice. The narrow,
single-column frame keeps labels, field help, correction summary and next
action together. It has no panorama, promotional first viewport or invented
status dashboard.

Initial registration explains the separate MS2 account and approved-network
limit before the required username/password/confirmation fields. Field errors
follow username, password, confirmation order; general errors precede them.
The focused alert summary links to invalid fields. Their help and textual
errors remain associated; the username is retained and both secrets are
cleared with re-entry guidance.

Confirmed success replaces the form. Client setup is primary when configured;
another registration remains subordinate and is not account recovery. Without
the optional guide URL, the result retains the configured Login host/port,
manual Mushroom guidance and the ordinary-account boundary.

| Outcome | Implemented recovery |
|---|---|
| Invalid fields, HTTP 400 | Render the correction summary and form, not a success screen. |
| Username taken, HTTP 409 | Link the conflict to Username; keep the entered name available for correction. |
| Save failure, HTTP 503 | Say registration is temporarily unavailable; do not claim an account exists. |
| Verification failure, HTTP 400 | Explain expiry/cookie verification and offer a safe GET reload, not a replayed POST. |
| Throttle, HTTP 429 | Say the last request was not processed; show a wait only when supplied, preserve Retry-After and offer GET reload. |
| HTTPS required, HTTP 403 | Direct to the configured setup guide without a credential form or reload button. Without a guide, ask the operator for the secure address. |

Standalone notices use the same task-sized heading, plain description,
focusable alert and bounded actions; they are not error-field cards or toasts.
The form and notices need no external fonts, scripts or images. The cube is
inline SVG and the shared theme is embedded from the one static source.

## Constraints and basis

Preserve field validation, antiforgery, the existing rate limiter, secret
clearing, no-store/no-cache responses, canonical/configured destinations and
truthful HTTP statuses. Native HTML constraints are not proof of server
acceptance. New accounts do not recover existing accounts or convey admin
access; [PRODUCT.md](../../PRODUCT.md) remains the product authority.

Sources are the primary/related targets above. They share tokens with the
portal through `website/theme.css`, whose global authority is
[DESIGN.md](../../DESIGN.md). This brief records the completed implementation;
it does not add a retrospective approval, seed, comp or new direction contract.
FORM remains the confirmed existing-world refinement. The retained `ship`
verdict is bounded to supplied UI and sampled coupled response handling, not
real registration/persistence, deployment, TLS, physical devices, screen
readers, gameplay or blanket WCAG conformance.
