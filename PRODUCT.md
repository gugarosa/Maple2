# MapleTime MS2 website

<!-- impeccable:product-schema 1 -->

## Platform

web

## Scope and Authority

This record covers the public portal in `website` and the registration journey
in `Maple2.Server.Web`, not a claim about complete game-server functionality.
The owner confirmed this scope and direction on 2026-09-13: follow the MS1
Impeccable process, preserve the MapleTime family resemblance, make MS2 artwork
essential, and complete a PR, merge, and deployment.

The MS1 reference is session `6f8a4636-a86c-4fa0-b8c1-d7e85c553b5b`: preserve a
familiar identity, simplify the player journey, verify truthful account states,
and document the implemented system. Its in-progress work is reference material,
not permission to change MS1 or copy its game claims.
Revisit that session's latest scope and design artifacts before implementation,
finish review, and release; carry over relevant process improvements, not
unrelated MS1 features. When cross-session history is unavailable, use its current
product/design documentation and state that evidence limit.

## Users

Invited MS2 testers need to understand access requirements, register a separate
account, configure their compatible Windows client, and connect with Mushroom.
Returning testers need direct setup instructions and honest project updates.
Other visitors must understand that the website being public does not make the
restricted game or account service publicly accessible.

## Product Purpose

MapleTime MS2 is an independent, non-commercial MapleStory 2 server project.
Its website is a small player companion: explain the private pilot and make the
account-to-client-to-connection sequence understandable without invented proof,
redundant choices, or a misleading download.

## Operating Context

- The public information site is `https://ms2.mapletime.dev`.
- Account registration is `https://play.ms2.mapletime.dev/account`, restricted to
  approved networks. The static portal never collects credentials.
- MS1 accounts and characters do not carry over.
- Testers supply a lawfully obtained, compatible NA client `20.8.1.0316` on
  Windows 10 or newer, x64. The official Mushroom Launcher is not the client.
- `installer/distribution.json` owns the launcher version and connection values.
- The installer remains on hold for antivirus review; no game-client or
  MapleTime installer download is published by the portal.
- Successful registration creates an ordinary account, not administrator access.
  Usernames use 3-24 ASCII letters, numbers, or underscores; new passwords use
  8-16 characters to fit the verified game-client field.

## Capabilities and Constraints

Preserve the pilot restrictions, account validation, antiforgery, password
clearing, rate limiting, no-store behavior, canonical links, and HTTPS boundary.
Never invent online counts, uptime, player testimonials, public availability,
client distribution permission, or complete gameplay verification.

Keep the static portal independently deployable. Do not alter MS1 resources,
DNS, budgets, ingress, player data, native binaries, or installer release gates.
Registration changes ship through the reviewed application delivery workflow;
static changes ship through the MS2-only portal deployment script.

## Brand Commitments

The name is MapleTime MS2. The owner requested a coherent relationship with MS1
and a distinct MS2 art direction, not an MS1 sprite reskin.
Preserve the original MS2 cube mark and the approved MS2 logo, world panorama,
and three creature crops. Their permission, source hashes, and conversion history
are recorded in `deploy/azure/README.md#website-artwork`.
Do not modify pinned artwork to attach metadata or treat the server license as
an artwork license. Retain Nexon attribution and independent-project disclosure.

## Product Principles

1. One clear route from account to compatible client to connection.
2. Put each fact where it changes the player's decision.
3. Preserve safeguards while simplifying presentation.
4. Distinguish an available website, a running pilot, and verified gameplay.
5. Let authentic MS2 art carry identity instead of decorative UI machinery.

## Accessibility and Inclusion

Support keyboard access, visible focus, native disclosure controls, readable
contrast, both system color schemes, reduced motion, narrow screens, and 200%
text scaling. Core functionality must not require JavaScript, external fonts,
or remote artwork. The English interface must tolerate longer text and Unicode
without pretending that unimplemented translations are available.

## Evidence on Hand

`website`, `Maple2.Server.Web/Views/Account/Register.cshtml`,
`DEVELOPMENT_STATUS.md`, `CLIENT_SETUP.md`, `installer/distribution.json`, and the
existing portal, account, application-startup, and deployment contract checks.
Browser fixtures and emulation are not physical-device, screen-reader, real
account creation, or native-gameplay acceptance.
