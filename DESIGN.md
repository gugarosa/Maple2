---
name: MapleTime MS2
description: "The implemented MapleTime companion system, with authentic MS2 identity and focused player controls."
colors:
  page: "#ffffff"
  surface: "#ffffff"
  soft: "#f8f9fa"
  inset: "#f1f3f5"
  ink: "#1a1d21"
  muted: "#5c6370"
  line: "#d1d5db"
  accent: "#c2410c"
  accent-strong: "#9a3412"
  accent-ink: "#ffffff"
  accent-soft: "#fff7ed"
  input-border: "#77818f"
  error: "#a12626"
  error-bg: "#fff2f2"
  success: "#166534"
  success-bg: "#f0fdf4"
  dark-page: "#0d1117"
  dark-surface: "#161b22"
  dark-soft: "#1c2128"
  dark-inset: "#242a33"
  dark-ink: "#e6edf3"
  dark-muted: "#9ba6b2"
  dark-line: "#39424e"
  dark-accent: "#fb923c"
  dark-accent-strong: "#fdba74"
  dark-accent-ink: "#0d1117"
  dark-accent-soft: "#322318"
  dark-input-border: "#8391a2"
  dark-error: "#ffb4b4"
  dark-error-bg: "#40292d"
  dark-success: "#9ae5b2"
  dark-success-bg: "#183b30"
typography:
  display:
    fontFamily: 'Georgia, "Times New Roman", serif'
    fontSize: "clamp(2.5rem, 4.7vw, 4.5rem)"
    fontWeight: 400
    lineHeight: 1.12
    letterSpacing: "-0.035em"
  account-display:
    fontFamily: 'Georgia, "Times New Roman", serif'
    fontSize: "clamp(2rem, 1.4rem + 2vw, 3rem)"
    fontWeight: 400
    lineHeight: 1.12
    letterSpacing: "-0.035em"
  headline:
    fontFamily: 'Georgia, "Times New Roman", serif'
    fontSize: "clamp(1.75rem, 2.5vw, 2.25rem)"
    fontWeight: 400
    lineHeight: 1.25
    letterSpacing: "-0.035em"
  title:
    fontFamily: '"Segoe UI", system-ui, -apple-system, sans-serif'
    fontSize: "1.125rem"
    fontWeight: 650
    lineHeight: 1.4
  body:
    fontFamily: '"Segoe UI", system-ui, -apple-system, sans-serif'
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.65
  label:
    fontFamily: '"Segoe UI", system-ui, -apple-system, sans-serif'
    fontSize: "1rem"
    fontWeight: 600
    lineHeight: 1.65
  note:
    fontFamily: '"Segoe UI", system-ui, -apple-system, sans-serif'
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: 1.65
  button:
    fontFamily: '"Segoe UI", system-ui, -apple-system, sans-serif'
    fontSize: "1rem"
    fontWeight: 600
    lineHeight: 1.4
  brand:
    fontFamily: '"Segoe UI", system-ui, -apple-system, sans-serif'
    fontSize: "1.125rem"
    fontWeight: 700
    lineHeight: 1.4
  data:
    fontFamily: '"Cascadia Code", "SFMono-Regular", Consolas, monospace'
    fontSize: "0.95em"
    fontWeight: 400
    lineHeight: 1.6
rounded:
  radius-sm: "8px"
  radius: "10px"
  radius-lg: "14px"
spacing:
  space-1: "0.25rem"
  space-2: "0.5rem"
  space-3: "0.75rem"
  space-4: "1rem"
  space-5: "1.5rem"
  space-6: "2rem"
  space-7: "3rem"
  space-8: "4rem"
components:
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.accent-ink}"
    typography: "{typography.button}"
    rounded: "{rounded.radius-sm}"
    padding: "0.75rem 1.5rem"
  button-primary-hover:
    backgroundColor: "{colors.accent-strong}"
    textColor: "{colors.accent-ink}"
  button-secondary:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    typography: "{typography.button}"
    rounded: "{rounded.radius-sm}"
    padding: "0.75rem 1.5rem"
  button-secondary-hover:
    backgroundColor: "{colors.accent-soft}"
    textColor: "{colors.accent}"
  button-disabled:
    backgroundColor: "{colors.inset}"
    textColor: "{colors.muted}"
  text-link:
    textColor: "{colors.accent}"
    typography: "{typography.body}"
    padding: "0.5rem 0"
  brand:
    textColor: "{colors.ink}"
    typography: "{typography.brand}"
  navigation:
    textColor: "{colors.muted}"
    typography: "{typography.note}"
    padding: "0.5rem 0"
  account-field:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    rounded: "{rounded.radius-sm}"
    padding: "0.75rem"
    width: "100%"
  account-feedback:
    backgroundColor: "{colors.error-bg}"
    textColor: "{colors.error}"
    typography: "{typography.body}"
    rounded: "{rounded.radius}"
    padding: "1rem 1.5rem"
  account-success:
    backgroundColor: "{colors.success-bg}"
    textColor: "{colors.success}"
    typography: "{typography.body}"
    rounded: "{rounded.radius}"
    padding: "1rem 1.5rem"
  connection-disclosure:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    rounded: "{rounded.radius}"
  status-container:
    backgroundColor: "{colors.soft}"
    textColor: "{colors.ink}"
    rounded: "{rounded.radius-lg}"
    padding: "2rem"
  update-row:
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    padding: "1.5rem 0.75rem"
  update-row-hover:
    backgroundColor: "{colors.soft}"
    textColor: "{colors.ink}"
---

# Design System: MapleTime MS2

## Overview

**Creative North Star: "A familiar MapleTime companion"**

This names the confirmed existing-world refinement, not a new metaphor selection
or generated concept. Maple-orange controls, near-neutral ground, editorial
serif headings and compact rounded forms provide the MS1-family resemblance.
The original cube and authentic MS2 scenery, logo and creatures establish the
second game's identity; the interface does not repaint them to match its accent.

Space, reading measure and visible state do the organizing. Dashed rules separate
content; tonal containers support disclosures, feedback and bounded information.
Density follows the task rather than a universal landing-page composition.
Native controls remain useful without JavaScript, and both system color schemes
receive explicit colors instead of automatic inversion.

**Key Characteristics:**
- Maple-orange interaction on explicit light and dark neutral grounds.
- Georgia display, local interface and code stacks; no downloaded fonts.
- Authentic MS2 identity kept separate from reusable interface color tokens.
- Flat, rounded controls with native behavior and visible recovery states.

**Source authority.** `website/theme.css` is the one shared token and base-control
source. `Maple2.Server.Web/Maple2.Server.Web.csproj` embeds that exact file as
`Maple2.Server.Web.PlayerTheme.css`; `Helpers/PlayerTheme.cs` reads it into
`Views/Shared/_AccountLayout.cshtml`. There is no separately maintained account
palette. `website/styles.css` owns portal/recovery layout; the account layout
owns its smaller frame and form styling. Markup and response helpers determine
the states actually available. This document records that completed source
candidate, not proof of publication.

Frontmatter is the portable token record. Keys match CSS variable names without
the leading `--`, except typography/component roles derived from used selectors
and the documented `dark-` value snapshots. The schema-v2
[sidecar](.impeccable/design.json) carries extensions and ten isolated component
excerpts, not a second primitive-token authority. `PRODUCT.md` owns product
truth; [surface briefs](.impeccable/surfaces/) own route strategy. The homepage's
panorama composition is not a global account or recovery layout rule. No seed,
QUALITY BAR, catalogue roll, generated comp or retroactive approval is claimed.

**Artwork provenance.** Keep the original `website/mark.svg` and six incumbent
rasters unchanged. The seventh raster, `website/ms2-logo.webp`, is a lossless
delivery derivative of the retained PNG fallback/installer source: all decoded
RGBA pixels match, and neither logo has an ICC profile. Official source links,
source hashes, crop coordinates, conversion settings and the owner's
2026-09-12 website-use permission live in
[Website artwork](deploy/azure/README.md#website-artwork); all seven shipped
raster hashes are pinned in `scripts/test_pilot_portal.ps1`. The sidecar links
this provenance without adding image metadata. Nexon artwork/client rights do
not follow from the server's AGPL license; preserve attribution and distribution
limits.

**Acceptance boundary.** The retained finish verdict is `ship` for the supplied
portal, recovery/account UI and sampled directly coupled response handling.
The reviewer inspected eight originals (four portal, four account); the other
22 required captures received contact-sheet integrity review only.
`UI-AUDIT.md` records the current `evidence-recapture` reports, the invalidated
old fixture evidence and the exact verification scope. Neither this record nor
that verdict certifies deployment, actual TLS, physical devices, screen readers,
real account/database persistence, gameplay or blanket WCAG conformance.

## Colors

A single maple-orange action family sits on cool near-neutrals; state colors
explain outcomes without becoming additional brand accents.

### Primary

- **Maple orange:** `accent` identifies links, primary actions, focus, caret and
  selected text ground. `accent-strong` is the hover partner, not a second CTA
  category. `accent-ink` supplies the paired foreground.
- **Soft orange:** `accent-soft` is the secondary button's hover ground.
- Frontmatter `dark-*` keys are snapshots of the same variables inside
  `prefers-color-scheme: dark`, not CSS variables named `--dark-*`. Components
  resolve the live variables. No manual theme switch or persisted preference
  exists.

### Neutral

- **Page / surface:** `page` is the document ground; `surface` is the field,
  disclosure and secondary-control ground. They coincide in light mode and
  separate in dark mode.
- **Soft / inset:** `soft` organizes status, footer and row hover; `inset`
  supports the MS2 brand label and the defined disabled button state.
- **Ink / muted:** `ink` carries primary copy; `muted` carries secondary copy,
  descriptions and helper text without reducing it to decorative microtype.
- **Line / input border:** `line` separates content and containers;
  `input-border` provides the stronger input/secondary-button boundary and the
  authored scrollbar thumb.

### State feedback

- **Error / error ground:** `error` and `error-bg` distinguish field correction
  and the form summary. Text, field associations and borders also carry state.
- **Success / success ground:** `success` and `success-bg` distinguish the
  server-confirmed account result. They are not availability or health colors.

**The Semantic Orange Rule.** Use the current theme's accent pair for interaction;
state colors describe outcomes, and the native cube/artwork retain their own colors.

Sidecar tonal strips are preview metadata only: the surface strip uses the
existing light/dark grounds, and other strips use relative-OKLCH display
interpolation. Neither introduces a CSS token, an approved palette step or a
contrast-safe foreground/background pairing.

## Typography

**Display Font:** `--font-display`: Georgia, with Times New Roman and serif
fallbacks. **Body Font:** `--font-body`: Segoe UI, then system-ui, Apple system
and sans-serif. **Data Font:** `--font-data`: Cascadia Code, SFMono-Regular,
Consolas and monospace.

Georgia is the confirmed local editorial voice of this MS2 refinement.
Newsreader is not loaded. Segoe/platform sans serve interface copy, not a
replacement display identity. There are no font requests or font-weight
synthesis dependencies; the base disables font synthesis.

### Hierarchy

- **Display:** the shared `display` role applies to the main heading; the
  homepage stacks an italic accent phrase within it.
- **Account display:** `account-display` is the smaller heading used across
  registration, result and notice states, not the homepage's title scale.
- **Headline:** `headline` gives sections a regular-weight serif voice.
- **Title:** `title` is the compact sans heading for steps, updates and the
  error summary; the summary explicitly resets letter spacing to normal.
- **Body / label / note:** `body` sets readable copy, `label` gives fields
  medium-weight names, and `note` covers helper/navigation/date text.
- **Button / brand:** these retain distinct medium and bold interface roles.
- **Data:** `data` uses a relative size and tabular numerals for connection and
  version strings. Dates also use tabular numerals without changing to monospace.

This is an observed role set, not a modular type scale. Paragraphs default to a
72ch maximum; headings balance wrapping, and copy/links/code can wrap anywhere.
The homepage introduction is locally (1.125rem), the account introduction
(1.0625rem); the status heading is locally (1.875rem). Homepage narrow and 404
heading overrides are recorded with their layouts, not promoted into new
global type steps.

**The Editorial Hierarchy Rule.** Keep Georgia for the large heading hierarchy
and the local sans stack for controls, labels and compact task headings.

## Layout

The shared container caps at `--content` (1280px), with
`min(calc(100% - 3rem), var(--content))`; at (30rem) and below its total gutter
becomes (2rem). The account frame is independently
`min(calc(100% - 2rem), 40rem)`. Its header/footer wrap naturally and its main
padding is `clamp(2rem, 6vw, 4rem)`; the form is a single-column grid with
`space-5` gaps. Do not derive a new spacing scale from these local measurements.

The frontmatter records the actual eight `space-*` steps. Controls use
`space-3`/`space-5` padding, related content uses the smaller steps, and larger
section gaps use `space-6` through `space-8`. Root `--control-target` is a
minimum block size (2.75rem), not a fixed height or a guarantee for every inline
link. Discrete buttons, navigation, disclosure summaries, field inputs and
summary links use it; inline prose links retain their semantic exception.
Labels and long strings must be able to wrap.

### Observed responsive behavior

| Condition | Implemented change |
|---|---|
| Above 70rem | Portal header is sticky; title/action and client areas use two columns, and setup uses three. These compositions belong to the homepage brief. |
| At or below 70rem | Navigation/column gaps tighten; the factual status container stacks its action below copy. |
| At or below 60rem | Header becomes static; four navigation links occupy a wrapping row in normal flow. Homepage title/action, setup and client columns stack; update dates move below copy and footer groups stack. The mobile panorama source is selected. The homepage heading becomes `clamp(2.5rem, 6vw, 4rem)`. |
| At or below 30rem | Container gutters and section spacing tighten; requirements labels stack; disclosure/status padding reduces and update thumbnails shrink from 4rem to 3rem while their arrow is hidden. Navigation still wraps. |
| Account, all widths | A 40rem-capped single column, full-width primary control, naturally wrapping header/footer; no separate account breakpoint or panorama. |
| Missing-page recovery | A 44rem-capped reading frame, `clamp(3rem, 12vh, 8rem)` block padding, `clamp(2.5rem, 5vw, 3.5rem)` heading and wrapping actions. |

Desktop anchor clearance is (6rem), changing to `space-4` at (60rem). The skip
link precedes navigation and reveals on focus. Logical dimensions and wrapping
support text/RTL stress; that does not claim translated routes exist.

Print removes the portal header, artwork, footer and skip link, simplifies
columns, avoids splitting grouped content, and exposes disclosure content using
the authored `::details-content` rule. This is source behavior, not a claim of
cross-browser print certification.

## Elevation & Depth

There are **no authored box or text shadows**, gradients, glass blurs or hover
lifts in this UI. Depth comes from explicit tonal surfaces, spacing, and thin
solid or dashed boundaries. The shared `--rule` is `1px dashed var(--line)`;
fields and disclosure containers use solid boundaries. The sticky header uses
`--layer-header` (10), and the fixed skip link uses `--layer-skip` (20).
These are the only authored layer tokens, not an invitation to add overlay
surfaces.

**The Tone Before Shadow Rule.** Separate authored interface surfaces with tone,
spacing and rules, not shadows; retain natural depth inside the MS2 artwork.

Native surfaces are not custom UI components: disclosure markers, required-field
validation, password/autofill UI, selection, caret and scrollbars remain browser
owned with only the source's limited theming. Do not infer dialogs, tooltips,
menus or custom validation popovers from these browser affordances.

## Shapes

Compact controls use `radius-sm`; disclosures and account feedback use `radius`;
panorama clipping and the factual status container use `radius-lg`. These are
the entire reused radius set in the frontmatter. The small MS2 label's (4px)
corners and circular creature crops (50%) are local identity/content treatments,
not a pill/chip scale.

Interface icons are actual inline SVG paths on a (24 by 24) view box, displayed
at (1.125rem), with unfilled current-color strokes (1.75), round caps and joins.
The right-arrow and outbound-arrow paths indicate destination direction;
decorative icons are hidden from accessibility APIs. The multicolor cube is
separate identity artwork, displayed at (2rem), not a monochrome UI glyph.

## Components

The sidecar contains ten `ds-`-scoped, standalone HTML/CSS previews using the
existing root variables. They are excerpts, not operational registration/API
controls: buttons have `type="button"`, field examples have no submission names
or form, and preview links use local fragments. They do not reproduce
antiforgery, routing, backend results or real account creation. The actual
templates and response helpers remain authoritative.

### Buttons and links

Compact, readable controls rather than raised game-window chrome. Primary and
secondary share `radius-sm`, a transparent/solid one-pixel boundary, `space-3`
by `space-5` padding and the minimum target. Primary hover uses
`accent-strong`; secondary hover uses `accent-soft` with accent text and border.
Active sets the border to current color. The shared stylesheet also defines a
muted, not-allowed disabled button state; no loading or disabled registration
workflow is claimed. Account primary controls span their frame.

Text links remain underlined, with (0.22em) underline offset and (1px) thickness.
Discrete text-link controls add `space-2` block padding and the minimum target;
inline prose links are not converted into buttons.

Shared focus is an accent outline (3px) offset by (4px). Control color/border
feedback uses `--duration-feedback` (140ms) with `--ease-feedback`
(`cubic-bezier(0.16, 1, 0.3, 1)`). Reduced motion changes that duration to (0s);
it does not hide content or remove hover/focus state. In forced colors,
button/brand-label boundaries use `ButtonText` and focus uses `Highlight`.

### Navigation and identity

The brand pairs the unmodified cube with a bold MapleTime label and subordinate
MS2 tag. Its hover keeps ink; it is not an interactive filter chip. Portal
navigation uses muted note-sized links, accent/underline hover and the shared
focus ring. The four links wrap rather than becoming a JavaScript menu.
There is no authored selected-route or scrollspy state. The account header
offers only the brand and configured setup-guide return.

### Inputs and feedback

Native labeled inputs have a surface ground, strong input border, `radius-sm`,
`space-3` padding, a full-width flexible box and the shared focus treatment.
The build has no custom field hover, password reveal, async availability,
loading or disabled-field design.

Invalid fields use `error` borders plus associated textual errors, not color
alone. The error summary uses `error-bg`, a solid error border, `radius` and
`space-4`/`space-5` padding. It exposes linked field errors in username,
password, confirmation order (general errors precede fields), has an alert
role and native focus entry, and explains retained username/cleared secrets.
It is not a toast. Success uses the analogous success pair with a status role
and bidirectional isolation of the confirmed username. Standalone notices
instead use the plain account heading/description and focused alert section,
not an invented colored severity-card family.

HTTP outcomes and next actions are recorded in the account surface brief.
The visual summaries must not convert a 400/403/409/429/503 response into success.
Forced colors uses `CanvasText` for input/feedback boundaries and `Highlight`
for the focused summary/notice.

### Native disclosures

A solid-line, `radius` container uses the native summary marker in accent.
The summary has a full target and medium-weight text; hover changes its text
to accent. Open state adds a divider below the summary; a targeted disclosure
gets an accent outline (2px) offset (4px). Keyboard, pointer and touch activation
are browser behavior. The connection recipe starts open and secondary help
starts closed; this is local content strategy, not a rule that all details
must begin in the same state.

### Rows and bounded information

Setup is a numbered list, not a card library. Update links are dashed-rule
rows: native creature crop, compact title/copy, date and outbound SVG. Hover
adds soft ground and underlines the accent title. Small layouts rearrange
content instead of truncating it.

The actual status container uses soft ground, `radius-lg` and `space-6` padding
(`space-5` on narrow screens); it contains factual prose and a secondary
destination, not counters, health lights or metric tiles. Raster-led update
and panorama patterns are documented here rather than counterfeited in
self-contained icon previews.

## Do's and Don'ts

### Do:

- **Do** derive shared colors and base controls from `website/theme.css`, including its embedded account use.
- **Do** use the current theme's paired foregrounds, text labels and visible focus to communicate state.
- **Do** preserve native disclosure/form behavior, wrapping controls and reduced-motion feedback.
- **Do** keep approved MS2 artwork bytes, attribution and provenance separate from UI tokens.
- **Do** read the matching surface brief before applying a route's composition or recovery strategy.

### Don't:

- **Don't** duplicate the shared theme or claim Newsreader, downloaded fonts or remote account assets are loaded.
- **Don't** add authored shadows, glass or hover lifts to this flat interface, or flatten the depth in native artwork.
- **Don't** treat the homepage panorama, ordered setup composition or factual status container as mandatory on account and recovery surfaces.
- **Don't** replace SVG interface icons with text glyphs, or turn the MS2 identity tag into an invented chip/filter system.
- **Don't** present preview excerpts, synthetic tonal strips, fixture results or the bounded ship verdict as live-account, deployment or accessibility certification.
