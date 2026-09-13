# MapleTime MS2 UI audit and delivery record

## Scope and method

The owner approved the public MS2 portal, missing-page recovery, and the secure
registration journey, followed by a PR, merge and deployment. Game features,
public ingress, player data, client distribution and MS1 infrastructure are not
part of the redesign.

Baseline: `addbbcb54d174ef316494ca3110fed9c6f15b1f1`.
Impeccable Assessment A was independent design judgment by agent
`6b407891-83a7-4ec2-ad79-413b9e18532f`; Assessment B was independent
detector/browser evidence by agent `92b23560-3e4d-4809-bd57-4d88ee450b73`.
The parent read A before B. Their observations are synthesized here rather than
added together as if overlapping findings were separate defects.

The original baseline checkout was unchanged. Browser inspection used isolated
Microsoft Edge contexts, an ephemeral loopback server, and read-only production
registration GETs. No production account was created or modified. Baseline
account-error and success findings were source-based, not runtime acceptance.

This is an existing-world refinement, not a replacement-brand concept exercise.
No catalogue roll, generated comp, image-generation asset pass, or comp-fidelity
score is claimed. The approved MS2 artwork and the user's MS1-family direction
are the visual authority.

## MS1 reference and process updates

Session `6f8a4636-a86c-4fa0-b8c1-d7e85c553b5b` was checked when scoping, before
implementation, and as new artifacts appeared. Its product record, design plan,
source tokens, rendered baseline, and newly written `website/DESIGN.md` were
read without changing the MS1 worktree.

Relevant guidance carried over: one clear player journey, honest account
outcomes, field-linked recovery, 44px control targets, 320px/200%-text stress
cases, preserved artwork, a bounded fresh finish review, and source-derived
token documentation. The later MS1 document makes an important distinction:
resolving a reviewer's fix list is not whole-site WCAG, physical-device,
screen-reader or authenticated-operation certification. Apply the same limit
to this record.

MS2 keeps its cube, 3D class panorama and creature crops. Its tiny navigation
stays visible on mobile; it does not inherit MS1's larger menu, live status,
wiki, email-recovery backend or installer download. Georgia/system UI are
zero-download equivalents of the shared editorial/interface hierarchy.

## Baseline design judgment

**Design specificity:** authentic MS2 art did most of the identity work. The
masked navy hero, cyan/yellow actions, peer icon cards and generic account form
did not yet read as the same MapleTime companion family.

| Heuristic | Score / 4 | Baseline issue |
|---|---:|---|
| System status | 3 | Honest pilot disclosure; account transitions unexercised |
| Real-world match | 2 | Downloads implied a client that was not published |
| Control and freedom | 3 | Mobile menu remained over its destination |
| Consistency | 2 | Registration lost MapleTime and pilot context |
| Error prevention | 3 | Existing format, length and confirmation safeguards |
| Recognition | 3 | Prerequisites spread across separate surfaces |
| Efficiency | 3 | Useful direct anchors and native controls |
| Minimalist design | 2 | Repeated card hierarchy for a sequential task |
| Error recovery | 2 | Summary not linked to invalid fields |
| Help | 3 | Exact connection recipe, but eligibility surfaced late |
| **Total** | **26/40** | **Acceptable; targeted improvement required** |

All ten heuristics apply to the combined portal/setup/account journey.
Assessment A identified three P1 and two P2 priorities, with no P0.
Cognitive load was moderate: real setup prerequisites are unavoidable; repeated
choices and carrying account context between surfaces are not.

## Baseline technical audit

**Implementation integrity:** scoped pass with isolated recovery defects.

| Dimension | Score / 4 | Baseline evidence |
|---|---:|---|
| Accessibility | 3 | Sampled contrast/labels passed; server-field recovery absent |
| Performance | 3 | No application JS/fonts; logo alone was 126,770 bytes |
| Responsive design | 3 | No measured overflow; short secondary targets/menu overlap |
| Theming | 3 | Working light/dark themes, independently maintained palettes |
| Implementation integrity | 3 | Honest restrictions; inconsistent throttle/recovery UI |
| **Total** | **15/20** | **Good; address weak dimensions** |

Assessment B identified five P2 findings, not five additional unique defects.
Fourteen browser cases covered 1440px, 390px, 320px with 200% root text,
light/dark, no-JavaScript, keyboard and synthesized touch.
No measured text-contrast or document-overflow failure occurred in that sample.
This is not a blanket WCAG conformance finding.

The baseline detector emitted two `dark-glow` warnings at `index.html:0`,
both resolving to the same low-opacity, downward menu shadow in
`styles.css:48`. The values were real; the luminous-glow interpretation was a
contextual false positive. They were not suppressed as global exceptions.
CSP blocked script-overlay injection as intended; no user-visible overlay was
claimed and the policy was not weakened.

## Finding-to-change trace

| Finding | Priority | Change and acceptance condition |
|---|---|---|
| A1: download expectations | P1 design | One start action; Client setup wording; own-client/approved-network constraints at arrival; no invented download |
| A2: account context | P1 design | Shared MapleTime MS2 shell, separate-account explanation, visible setup return |
| A3 / B-04: field recovery | P1 design / P2 technical | Field-linked summary, real invalid state, retained username, empty passwords and re-entry guidance |
| A4: family and hierarchy | P2 | Shared orange/neutral tokens, open panorama, ordered setup, quieter update rows |
| A5 / B-02: menu overlap | P2 | Remove the overlay; four wrapping navigation links remain in document flow |
| B-01: short action targets | P2 | At least 44px for discrete controls; inline-prose links retain their semantic exception |
| B-03: logo transfer | P2 | Pixel-identical WebP, 75,896 bytes: 40.1% smaller, original PNG retained as fallback |
| B-05: throttle recovery | P2 | Preserve HTTP 429/limiter and offer a safe GET path in the account shell |
| Missing-page recovery | Scoped addition | A useful local 404 page with real setup links and HTTP 404 preserved |
| Integration correction | Coupled regression | Keep configured Login host/port when the optional setup URL is absent; retain another-registration as a subordinate link, not the primary action |

## Impeccable capability coverage

The full toolkit was considered. Commands with conflicting goals are applied
where they serve this product, not run ceremonially to undo one another.

| Capability | Application |
|---|---|
| Init | Confirmed product scope, audience, restrictions and durable `PRODUCT.md` |
| Shape / new work | Confirmed journey and recorded a development-only direction contract |
| Critique | Isolated design and technical assessments before synthesis |
| Audit | Accessibility, performance, responsiveness, theming and implementation integrity |
| Bolder | Give the authentic MS2 panorama its full, unmasked role |
| Quieter / distill | Remove peer-card clutter, duplicate hero actions and mobile overlay |
| Clarify | Precise setup/download labels, account context and corrective messages |
| Onboard | Account, compatible client, then Mushroom connection; success resumes setup |
| Colorize | Shared maple-orange action/focus roles and explicit dark tonal surfaces |
| Typeset | Editorial heading hierarchy; readable body/helper sizes; no font download |
| Layout | Ordered task structure, grouped requirements and compact update rows |
| Adapt | Narrow/intermediate/wide layouts, text expansion, zoom and input modes |
| Animate | Short control feedback only; instant reduced-motion states, no hidden entrances |
| Delight | Authentic MS2 imagery and a considered account-to-game handoff, not confetti |
| Harden | Validation/recovery states, missing routes, no-JS and failure resilience |
| Optimize | Measured logo reduction, responsive imagery, dimensions and lazy updates |
| Extract | One theme source shared by static pages and compiled Razor, reusable controls |
| Polish | Bounded complete-journey capture, one correction batch, then fresh review |
| Document | Final `DESIGN.md` and schema-v2 sidecar derived from the implementation |
| Live | Not activated: an element-variant loop is unnecessary; static CSP remains strict |
| Overdrive | Not activated: no measured compute problem or justified shader/particle system; no new product behavior or motion dependency |
| Visualize / asset producer | No generated comp needed; existing approved art is authoritative. Only a pixel-identical encoding derivative is added |
| Native / plugin administration | Not applicable to this web surface; no native app, pin/hook, or unrelated plugin-drift changes |

## Verification and evidence boundaries

The repeatable static contracts are `scripts/test_pilot_portal.ps1` and
`scripts/test_website_ui.py`. They cover stable anchors, semantics, local assets,
the registration/download contract, CSP, recovery routes, image hashes, explicit
upload/probe lists and a 32-KiB authored HTML/CSS / 576-KiB complete static budget.
The complete budget includes the retained PNG fallback; modern-browser initial
body transfer is separately bounded at 320 KiB.

`scripts/check_website_browser.py` consumes compiled, isolated Razor fixtures,
serves the actual static files with their CSP, and records viewport/state,
contrast, overflow, discrete-target, image and request evidence. It covers
keyboard/native touch, no-JS, reduced motion, forced colors, RTL layout stress,
missing images and longer interface text. Fixture rendering does not create
real accounts or certify database persistence.

Retained local evidence is deliberately excluded from Git, Docker and the
static upload under `.impeccable/review/`. It can be reproduced from the checked-in
harnesses. Do not treat a screenshot, detector count, fixture outcome or a
successful website response as native-gameplay readiness.

## Verified implementation

The Release fixture build passes with no warnings or errors and **646
assertions** across 16 compiled Razor states. Nine static UI regressions,
the portal publication contracts, scoped C# formatting, application composition
and compiled startup URL-rejection checks also pass.

The final accepted browser evidence is in
`.impeccable/review/evidence-recapture/`: **264 cases in Edge and 264 in WebKit**,
with no reported overflow, sampled contrast, discrete-target, missing-description,
image-selection, page-error or unexpected-request failures. The matrix includes
normal and hover states, both themes, 1440/768/390px, 320px at 200% root text,
native keyboard/touch disclosure, no JavaScript, reduced motion, forced colors,
RTL layout stress, longer text, Unicode and missing images.

An important evidence correction was made before the independent finish review:
the test host's initial state left password-sentinel ModelState entries
unvalidated, which produced an empty alert that the production initial GET does
not render. The fixture now marks those entries valid and explicitly asserts
the absence of an initial alert. Its guide URL also now matches the real
`#getting-started` configuration instead of a nonexistent fixture anchor.
The earlier initial-state captures are not acceptance evidence. All current
screenshots and both browser reports were replaced after this correction;
this was an evidence repair, not another cosmetic redesign cycle.

The final integrated Impeccable scan covers the two static pages, their CSS and
all 16 compiled Razor fixtures; `detector-final.json` contains **zero findings**.
There is no production overlay and no claim that a clean detector certifies UX.
The original six raster assets remain byte-identical. The added logo's decoded
RGBA pixels are identical to the PNG, and neither image has an ICC profile.

Current screenshots cover the homepage at desktop/mobile in both themes, the
initial/error/success account layouts at desktop/mobile in both themes, all other
account states on mobile, and missing-page recovery. The contact sheet is an
index, not a substitute for detailed inspection of the originals. Browser
emulation, synthetic touch and isolated fixtures do not certify physical
devices, assistive technology, real registration, TLS, gameplay or deployment.

## Finish record

The independent Impeccable finish reviewer
`bf281f0d-66bb-4b42-a31f-a18f52df5636` returned **`ship`**, with no material
fixes identified within its stated coverage. It found the type, MS2 imagery,
light/dark ground, task hierarchy, truthfulness, first viewport and recovery
handoff consistent with the pinned refinement contract.

The reviewer individually inspected all four portal originals and four account
originals (initial mobile light, errors mobile dark, success desktop light and
no-guide success). The other 22 required captures received contact-sheet
integrity review, not full-resolution typography review. Its verdict supports
the supplied portal/recovery/account UI and sampled directly coupled response
handling, not deployment, real TLS, account persistence, physical devices,
screen readers, gameplay or blanket WCAG conformance.

The five-section review is retained in
`.impeccable/review/finish-review.json`. The documentation handoff below completes
the final documentation obligation; publication remains a separate delivery step.

## Final documentation handoff

The completed implementation, not an earlier plan or generated comp, is now
recorded in root `DESIGN.md` and schema-v2 `.impeccable/design.json`. The root
file uses the canonical eight-section format and frontmatter: 32 color entries
(16 light/dark pairs), ten observed type roles, three reused radii, eight spacing
steps and 15 component recipes. Dark-prefixed keys are value snapshots of the
same theme variables, not additional CSS variables. No normative scale was
invented.

The sidecar extends rather than duplicates those primitives. Its ten isolated
`ds-` previews include the two button variants, cube/identity tag, wrapping
navigation, field, linked error summary, confirmed-result paragraph, native
disclosure, factual status container and discrete text link. They use the
existing root variables and actual SVG paths. They have no submitting form,
account/API action, remote dependency or claim to create a real account.
Authored states remain distinct from native browser behavior and from the
defined-but-unrendered disabled-button style. Tonal strips are explicitly
preview-only metadata, not additional palette tokens or contrast guarantees.

The latest MS1 route-brief pattern was checked read-only in its session worktree:
`website/DESIGN.md` and `website/.impeccable/surfaces/` homepage/account briefs.
MS2 now separates route strategy without importing MS1 features or changing the
confirmed product:

| Documentation file | Handoff |
|---|---|
| `DESIGN.md` | Source authority, tokens, eight canonical sections, native surfaces, responsive rules, provenance and bounded acceptance |
| `.impeccable/design.json` | Schema-v2 extensions, ten component excerpts and verbatim system narrative |
| `.impeccable/surfaces/website-index-html.md` | Original approved homepage direction retained; account/404/shared-theme target matches removed |
| `.impeccable/surfaces/maple2-server-web-views-account-register-cshtml.md` | Code-derived Operate brief for registration, results, notices and coupled response handling |
| `.impeccable/surfaces/website-404-html.md` | Code-derived route-recovery brief, preserving real destinations and HTTP 404 |
| `CLAUDE.md` | Matching-brief guidance, shared-theme authority and the shared stylesheet's recovery-specific caveat |
| `UI-AUDIT.md` | This final documentation record; original evidence and review scope preserved |

The homepage's conflicting target matches were removed before the other records
were added. Each primary/related target now has one matching brief; shared tokens
have global authority, not homepage composition authority. New briefs use the
existing Impeccable v1 `version`, `slug`, `primary_target`, `related_targets`
schema. They describe completed route behavior, not retrospective human
approvals, seeds, QUALITY BAR choices or comps.

**Documentation/source checks.** Read-back and ripgrep checks covered the shared
theme, portal stylesheet, both static pages and response override; embedded-theme
helper/csproj; shared account layout, registration/notices and the four coupled
response/protection/result/filter sources. All 43 frontmatter token references
resolve to recorded primitives; component properties stay within the format's
eight-property allowance. All ten sidecar `refersTo` values resolve to component
recipes; color/type metadata keys match the frontmatter. Checks confirmed 32
eight-stop preview strips, 20 correctly escaped HTML/CSS JSON string fields,
canonical section order, matching narrative and non-overlapping brief headers.
CSS variable references were compared with the actual root declarations. These
were static source/serialization checks with the available file/rg tools, not an
executed JSON/YAML parser or helper result: this handoff had no command-execution
tool with which to invoke the Impeccable `surface-brief` helper or a schema
validator. No package installation, detector rerun, doctor/context repair or
additional visual QA was performed.

The parent subsequently executed JSON/YAML parsing and source-consistency
validation: all 32 colors and eight spacing steps matched the actual theme;
component property/reference checks and all ten preview CSS-variable references
passed; all three briefs had valid, existing, non-overlapping targets and local
documentation links. The real Impeccable helper also resolved the account and
404 targets to their respective briefs. No dependency installation was needed.

The artwork record and all seven pinned raster hashes were cross-checked against
`deploy/azure/README.md#website-artwork` and `scripts/test_pilot_portal.ps1`.
Provenance is linked in DESIGN and carried in the sidecar; no image metadata or
artwork bytes were touched. The PNG/WebP equality, byte sizes and absent ICC
profiles remain the accepted implementation validation, not a new image test.
The owner's 2026-09-12 website-use permission is not AGPL permission to
redistribute Nexon artwork or the client.

Only current `evidence-recapture/browser-chromium.json` and
`browser-webkit.json` were used for browser-report checks: each contains 264 cases
and empty failure, browser-error and failed-request arrays. The retained final
detector remains `[]`; it was read, not rerun. The earlier unvalidated sentinel
ModelState/incorrect-guide fixture evidence remains invalidated, not production
failure or acceptance evidence. This documentation pass did not inspect another
set of images or enlarge the reviewer's coverage: eight originals were directly
inspected by that reviewer (four portal, four account); the other 22 required
captures received contact-sheet integrity review only.

**Not canonized or repaired.** No new source defect was identified by this
documentation sampling, and no craft-floor refusal was promoted into a future
component rule. The corrected route-authority overlap was documentation drift,
not a UI redesign. Native browser surfaces, absent optional components and the
explicit Georgia/local-font family translation are not invented product gaps.
The existing `ship` verdict remains limited to supplied portal/recovery/account
UI and sampled coupled response handling. No UI, Web code, tests, configuration,
PRODUCT truth, MS1 files, artwork or release files changed here; no commit,
deployment, actual TLS, real account/database operation, physical-device,
screen-reader, gameplay or blanket-WCAG certification is implied. Deployment of
this completed pass has not been delivered to the user by this handoff.
