"""Bounded, local-only browser evidence for the portal and compiled Razor fixtures."""

import argparse
import functools
import json
import threading
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import quote, unquote, urlsplit

from playwright.sync_api import sync_playwright


ROOT = Path(__file__).resolve().parents[1]
SITE = ROOT / "website"

MEASURE = r"""() => {
  const rgba = value => {
    const parts = value.match(/[\d.]+/g)?.map(Number);
    return parts && parts.length >= 3 ? [...parts.slice(0, 3), parts[3] ?? 1] : [0, 0, 0, 0];
  };
  const blend = (front, back) => front.slice(0, 3).map((v, i) => v * front[3] + back[i] * (1 - front[3]));
  const background = element => {
    const chain = [];
    for (let current = element; current; current = current.parentElement) chain.push(current);
    return chain.reverse().reduce((color, current) => blend(rgba(getComputedStyle(current).backgroundColor), color), [255, 255, 255]);
  };
  const luminance = color => color.map(value => {
    const s = value / 255;
    return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  }).reduce((sum, value, i) => sum + value * [0.2126, 0.7152, 0.0722][i], 0);
  const contrast = (front, back) => {
    const a = luminance(front), b = luminance(back);
    return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
  };
  const visible = element => {
    const rect = element.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0 && getComputedStyle(element).visibility === 'visible';
  };
  const describe = element => element.id ? '#' + element.id :
    element.tagName.toLowerCase() + (element.className && typeof element.className === 'string' ? '.' + element.className.trim().split(/\s+/).join('.') : '');
  const contrastFailures = [];
  const measuredPairs = new Set();
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let node; (node = walker.nextNode());) {
    const element = node.parentElement;
    if (!element || !node.textContent.trim() || ['STYLE', 'SCRIPT'].includes(element.tagName) || !visible(element)) continue;
    const range = document.createRange();
    range.selectNode(node);
    if (range.getBoundingClientRect().height === 0) continue;
    const style = getComputedStyle(element), back = background(element);
    const front = blend(rgba(style.color), back);
    const size = parseFloat(style.fontSize), weight = parseInt(style.fontWeight) || 400;
    const minimum = size >= 24 || (size >= 18.666 && weight >= 700) ? 3 : 4.5;
    const ratio = contrast(front, back);
    const key = [style.color, back, minimum].join('|');
    if (ratio + 0.01 < minimum && !measuredPairs.has(key)) {
      contrastFailures.push({element: describe(element), text: node.textContent.trim().slice(0, 100), ratio, minimum});
    }
    measuredPairs.add(key);
  }
  const overflow = [...document.querySelectorAll('body *')].filter(element => {
    if (!visible(element)) return false;
    const rect = element.getBoundingClientRect();
    return rect.right > document.documentElement.clientWidth + 1 || rect.left < -1;
  }).slice(0, 15).map(describe);
  const controls = [...document.querySelectorAll('a, button, input:not([type="hidden"]), summary')].filter(visible);
  const smallTargets = controls.filter(element => {
    if (element.tagName === 'A' && getComputedStyle(element).display === 'inline') return false;
    const rect = element.getBoundingClientRect();
    return rect.width < 43.5 || rect.height < 43.5;
  }).map(element => ({element: describe(element), text: element.textContent.trim().slice(0, 80), width: element.getBoundingClientRect().width, height: element.getBoundingClientRect().height}));
  const brokenDescriptions = [...document.querySelectorAll('[aria-describedby], [aria-labelledby]')].flatMap(element =>
    ['aria-describedby', 'aria-labelledby'].flatMap(name => (element.getAttribute(name) || '').split(/\s+/).filter(Boolean).filter(id => !document.getElementById(id)).map(id => ({element: describe(element), id}))));
  const unlabelledInputs = [...document.querySelectorAll('input:not([type="hidden"])')].filter(element => !element.labels?.length && !element.getAttribute('aria-label')).map(describe);
  const brokenImages = [...document.images].filter(image => image.complete && image.naturalWidth === 0).map(image => image.getAttribute('src'));
  const headingCount = document.querySelectorAll('h1').length;
  return {
    width: innerWidth, contentWidth: document.documentElement.scrollWidth,
    overflow, contrastFailures, measuredColorPairs: measuredPairs.size,
    smallTargets, brokenDescriptions, unlabelledInputs, brokenImages, headingCount,
    transitionDuration: getComputedStyle(document.querySelector('.button') || document.body).transitionDuration,
    passwordsEmpty: [...document.querySelectorAll('input[type="password"]')].every(input => input.value === ''),
    images: [...document.images].map(image => ({source: image.getAttribute('src'), selected: new URL(image.currentSrc || image.src).pathname, width: image.width, height: image.height, naturalWidth: image.naturalWidth, naturalHeight: image.naturalHeight})),
    resources: performance.getEntriesByType('resource').map(entry => ({name: new URL(entry.name).pathname, bytes: entry.decodedBodySize, duration: entry.duration})),
    documentBytes: performance.getEntriesByType('navigation')[0]?.decodedBodySize ?? 0
  };
}"""


class Handler(SimpleHTTPRequestHandler):
    def __init__(self, *args, fixtures, **kwargs):
        self.fixtures = fixtures
        super().__init__(*args, directory=str(SITE), **kwargs)

    def log_message(self, _format, *_args):
        pass

    def translate_path(self, path):
        requested = unquote(urlsplit(path).path)
        if requested.startswith("/__fixtures/"):
            candidate = (self.fixtures / requested.removeprefix("/__fixtures/")).resolve()
            if candidate.is_relative_to(self.fixtures) and candidate.suffix == ".html":
                return str(candidate)
            return str(self.fixtures / "__not_found__")
        return super().translate_path(path)

    def end_headers(self):
        if not self.path.startswith("/__fixtures/"):
            settings = json.loads((SITE / "staticwebapp.config.json").read_text(encoding="utf-8"))
            for name, value in settings["globalHeaders"].items():
                self.send_header(name, value)
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def send_error(self, code, message=None, explain=None):
        if code != 404:
            return super().send_error(code, message, explain)
        body = (SITE / "404.html").read_bytes()
        self.send_response(404)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def settle(page):
    page.evaluate("""async () => {
      await document.fonts.ready;
      await Promise.all([...document.images].map(image => {
        image.loading = 'eager';
        return image.decode().catch(() => null);
      }));
      window.scrollTo(0, 0);
    }""")


def inspect(page, case, failures, allow_broken_images=False):
    measured = page.evaluate(MEASURE)
    for key in ("overflow", "contrastFailures", "smallTargets", "brokenDescriptions", "unlabelledInputs"):
        if measured[key]:
            failures.append({"case": case, "check": key, "evidence": measured[key]})
    if measured["brokenImages"] and not allow_broken_images:
        failures.append({"case": case, "check": "brokenImages", "evidence": measured["brokenImages"]})
    if measured["headingCount"] != 1 or not measured["passwordsEmpty"]:
        failures.append({"case": case, "check": "headings-or-password-clearing", "evidence": measured})
    return {"case": case, **measured}


def capture(page, directory, name):
    if directory:
        page.screenshot(path=str(directory / name), full_page=True, animations="disabled")


def run(args):
    fixtures = args.account_fixtures.resolve()
    fixture_files = sorted(fixtures.glob("*.html"))
    if not fixture_files:
        raise RuntimeError(f"No compiled Razor HTML fixtures found in {fixtures}. Run the account UI harness first.")
    args.output.mkdir(parents=True, exist_ok=True)
    if args.capture:
        args.capture.mkdir(parents=True, exist_ok=True)
    server = ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, fixtures=fixtures))
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    base = f"http://127.0.0.1:{server.server_port}"
    results, failures, browser_errors, request_failures = [], [], [], []
    try:
        with sync_playwright() as playwright:
            launch_options = {"channel": args.channel} if args.channel else {}
            browser = getattr(playwright, args.engine).launch(**launch_options)
            try:
                for scheme in ("light", "dark"):
                    for width, height, scale in ((1440, 1000, 1), (768, 1024, 1), (390, 844, 1), (320, 900, 2)):
                        context = browser.new_context(viewport={"width": width, "height": height}, color_scheme=scheme)
                        page = context.new_page()
                        page.on("pageerror", lambda error: browser_errors.append(str(error)))
                        page.on("requestfailed", lambda request: request_failures.append({"url": request.url, "failure": request.failure}))
                        routes = [("/", None)] + [
                            (f"/__fixtures/{quote(file.name)}", file.stem) for file in fixture_files
                        ] + [("/missing/deep/page/", "missing")]
                        for route, state in routes:
                            response = page.goto(base + route, wait_until="networkidle")
                            expected = 404 if state == "missing" else 200
                            if response.status != expected:
                                failures.append({"case": route, "check": "status", "actual": response.status, "expected": expected})
                            if scale != 1:
                                page.evaluate("(size) => document.documentElement.style.fontSize = size + 'px'", 16 * scale)
                            case = f"{state or 'home'}-{scheme}-{width}-text{scale}"
                            initial = page.evaluate(MEASURE)
                            if not state:
                                transfer = initial["documentBytes"] + sum(resource["bytes"] for resource in initial["resources"])
                                if transfer > 320 * 1024:
                                    failures.append({"case": case, "check": "initial-body-budget", "bytes": transfer, "limit": 320 * 1024})
                            settle(page)
                            result = inspect(page, case, failures)
                            if state == "initial" and page.locator('[role="alert"]').count():
                                failures.append({"case": case, "check": "initial-state-must-not-show-an-error"})
                            result["initialResources"] = initial["resources"]
                            result["initialDocumentBytes"] = initial["documentBytes"]
                            if state is None:
                                selected = {image["source"]: image["selected"] for image in result["images"]}
                                expected_world = "/ms2-world-mobile.webp" if width <= 960 else "/ms2-world.webp"
                                if selected.get("/ms2-world.webp") != expected_world or selected.get("/ms2-logo.png") != "/ms2-logo.webp":
                                    failures.append({"case": case, "check": "responsive-artwork", "evidence": selected})
                            results.append(result)
                            if scale == 1 and width in (1440, 390):
                                viewport = "desktop" if width == 1440 else "mobile"
                                if state is None:
                                    name = f"{'dark-' if scheme == 'dark' else ''}{viewport}.png"
                                    capture(page, args.capture, name)
                                elif state in ("initial", "errors", "success"):
                                    capture(page, args.capture, f"account-{state}-{scheme}-{viewport}.png")
                                    if scheme == "light" and width == 390:
                                        capture(page, args.capture, f"account-{state}.png")
                                elif scheme == "light" and width == 390:
                                    capture(page, args.capture, f"account-{state}.png" if state != "missing" else "missing.png")
                            for control in page.locator(".button, .site-nav a, summary").all():
                                if not control.is_visible():
                                    continue
                                control.hover()
                                hover = page.evaluate(MEASURE)
                                if hover["contrastFailures"]:
                                    failures.append({"case": case, "check": "hover-contrast", "evidence": hover["contrastFailures"]})
                            if state is None:
                                page.locator('a[href="#getting-started"]').last.click()
                                heading = page.locator("#setup-title").bounding_box()
                                if heading["y"] < -1 or heading["y"] >= height:
                                    failures.append({"case": case, "check": "start-anchor-visibility", "evidence": heading})
                                summary = page.locator("#connection-steps > summary")
                                summary.focus()
                                was_open = page.locator("#connection-steps").evaluate("(element) => element.open")
                                page.keyboard.press("Enter")
                                is_open = page.locator("#connection-steps").evaluate("(element) => element.open")
                                if is_open == was_open:
                                    failures.append({"case": case, "check": "keyboard-disclosure"})
                        context.close()

                touch_options = {"viewport": {"width": 390, "height": 844}, "has_touch": True}
                if args.engine != "firefox":
                    touch_options["is_mobile"] = True
                touch = browser.new_context(**touch_options)
                page = touch.new_page()
                page.goto(base, wait_until="networkidle")
                summary = page.locator("#connection-steps > summary")
                summary.tap()
                if page.locator("#connection-steps").evaluate("(element) => element.open"):
                    failures.append({"case": "touch", "check": "disclosure-close"})
                summary.tap()
                if not page.locator("#connection-steps").evaluate("(element) => element.open"):
                    failures.append({"case": "touch", "check": "disclosure-open"})
                results.append(inspect(page, "synthesized-touch", failures))
                touch.close()

                for special in ("no-javascript", "reduced-motion", "forced-colors", "rtl", "missing-images", "text-expansion", "unicode"):
                    options = {"viewport": {"width": 320, "height": 900}, "color_scheme": "dark"}
                    if special == "no-javascript":
                        options["java_script_enabled"] = False
                    if special == "reduced-motion":
                        options["reduced_motion"] = "reduce"
                    if special == "forced-colors":
                        options["forced_colors"] = "active"
                    context = browser.new_context(**options)
                    if special == "missing-images":
                        context.route("**/*", lambda route: route.abort() if route.request.resource_type == "image" else route.continue_())
                    page = context.new_page()
                    for route, state in [("/", "home")] + [(f"/__fixtures/{quote(file.name)}", file.stem) for file in fixture_files]:
                        page.goto(base + route, wait_until="networkidle")
                        if special == "rtl":
                            page.evaluate("document.documentElement.dir = 'rtl'")
                        if special == "text-expansion":
                            page.evaluate("""() => {
                              for (const element of document.querySelectorAll('h1, h2, h3, summary, label, .site-nav a')) {
                                element.append(document.createTextNode(' extended interface text'));
                              }
                            }""")
                        if special == "unicode":
                            page.evaluate(r"""() => {
                              for (const element of document.querySelectorAll('h1, label, summary')) {
                                element.append(document.createTextNode(' \u4e16\u754c \ud83c\udf0d \u0645\u0631\u062d\u0628\u0627'));
                              }
                            }""")
                        settle(page)
                        measured = inspect(page, f"{state}-{special}", failures, allow_broken_images=special == "missing-images")
                        if special == "reduced-motion" and measured["transitionDuration"] not in ("0s", "0s, 0s, 0s"):
                            failures.append({"case": state, "check": "reduced-motion", "evidence": measured["transitionDuration"]})
                        results.append(measured)
                    context.close()
            finally:
                browser.close()
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
    report = {
        "engine": args.engine,
        "channel": args.channel,
        "evidence": "Local browser emulation, synthesized touch, and compiled Razor view fixtures; no production POST, physical device, or screen-reader certification.",
        "cases": results,
        "failures": failures,
        "browserErrors": browser_errors,
        "failedRequests": request_failures,
    }
    path = args.output / f"browser-{args.engine}.json"
    path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"engine": args.engine, "cases": len(results), "failures": len(failures), "browserErrors": browser_errors, "failedRequests": request_failures, "report": str(path)}, indent=2))
    if failures or browser_errors or request_failures:
        raise SystemExit(1)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--account-fixtures", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--capture", type=Path)
    parser.add_argument("--engine", choices=("chromium", "firefox", "webkit"), default="chromium")
    parser.add_argument("--channel", choices=("msedge", "chrome"))
    args = parser.parse_args()
    if args.channel and args.engine != "chromium":
        parser.error("--channel is only supported with --engine chromium")
    run(args)
