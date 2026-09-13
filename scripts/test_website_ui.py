import json
import unittest
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urlsplit


ROOT = Path(__file__).resolve().parents[1]
SITE = ROOT / "website"


class Document(HTMLParser):
    def __init__(self, path):
        super().__init__(convert_charrefs=True)
        self.elements = []
        self.feed(path.read_text(encoding="utf-8"))

    def handle_starttag(self, tag, attrs):
        self.elements.append((tag, dict(attrs)))

    def named(self, tag):
        return [attrs for name, attrs in self.elements if name == tag]

    def with_id(self, identifier):
        return [(tag, attrs) for tag, attrs in self.elements if attrs.get("id") == identifier]


class WebsiteUiTests(unittest.TestCase):
    def setUp(self):
        self.pages = {
            "/": Document(SITE / "index.html"),
            "/index.html": Document(SITE / "index.html"),
            "/404.html": Document(SITE / "404.html"),
        }

    def test_semantics_and_heading_order(self):
        for path in ("/", "/404.html"):
            with self.subTest(path=path):
                page = self.pages[path]
                self.assertEqual(page.named("html")[0]["lang"], "en")
                self.assertEqual(len(page.named("main")), 1)
                self.assertEqual(len(page.named("h1")), 1)
                self.assertEqual(page.with_id("main-content")[0][1]["tabindex"], "-1")
                ids = [attrs["id"] for _, attrs in page.elements if "id" in attrs]
                self.assertEqual(len(ids), len(set(ids)))
                previous = 0
                for tag, _ in page.elements:
                    if tag in ("h1", "h2", "h3", "h4", "h5", "h6"):
                        level = int(tag[1])
                        self.assertLessEqual(level, previous + 1)
                        previous = level

    def test_local_navigation_and_resources_resolve_from_nested_missing_pages(self):
        for path in ("/", "/404.html"):
            for tag, attrs in self.pages[path].elements:
                for attr in ("href", "src", "srcset"):
                    value = attrs.get(attr)
                    if value is None:
                        continue
                    with self.subTest(page=path, tag=tag, value=value):
                        url = urlsplit(value)
                        if url.scheme or url.netloc:
                            self.assertEqual(url.scheme, "https")
                            continue
                        if url.path:
                            self.assertTrue(url.path.startswith("/"), value)
                            target = SITE / url.path.lstrip("/")
                            self.assertTrue(url.path == "/" or target.is_file(), value)
                        if url.fragment:
                            destination = self.pages[url.path or path]
                            self.assertEqual(len(destination.with_id(url.fragment)), 1, value)

    def test_static_pages_have_no_executable_or_credential_surfaces(self):
        for path in ("/", "/404.html"):
            page = self.pages[path]
            for tag in ("script", "style", "form", "input", "iframe"):
                self.assertEqual(page.named(tag), [], (path, tag))
            for _, attrs in page.elements:
                self.assertFalse(any(name.startswith("on") for name in attrs))
                self.assertNotIn("style", attrs)
            styles = [item["href"] for item in page.named("link") if item.get("rel") == "stylesheet"]
            self.assertEqual(styles, ["/theme.css", "/styles.css"])

    def test_artwork_is_sized_and_the_panorama_is_not_lazy(self):
        images = self.pages["/"].named("img")
        for image in images:
            self.assertIn("alt", image)
            self.assertGreater(int(image["width"]), 0)
            self.assertGreater(int(image["height"]), 0)
        panorama = next(image for image in images if image["src"] == "/ms2-world.webp")
        self.assertEqual(panorama["fetchpriority"], "high")
        self.assertNotEqual(panorama.get("loading"), "lazy")
        monsters = [image for image in images if image.get("class") == "update-monster"]
        self.assertEqual(len({image["src"] for image in monsters}), 3)
        self.assertTrue(all(image["loading"] == "lazy" for image in monsters))
        self.assertTrue(any(source.get("srcset") == "/ms2-logo.webp" and source.get("type") == "image/webp"
                            for source in self.pages["/"].named("source")))
        self.assertTrue(any(image["src"] == "/ms2-logo.png" for image in images))

    def test_connection_deep_link_targets_a_visible_native_control(self):
        target = self.pages["/"].with_id("connection-steps")
        self.assertEqual(len(target), 1)
        self.assertEqual(target[0][0], "details")
        self.assertIn("open", target[0][1])
        self.assertTrue(self.pages["/"].with_id("registration-help"))
        self.assertEqual(len(self.pages["/"].named("summary")), 2)

    def test_recovery_status_and_static_security_are_preserved(self):
        settings = json.loads((SITE / "staticwebapp.config.json").read_text(encoding="utf-8"))
        self.assertEqual(settings["responseOverrides"]["404"], {"rewrite": "/404.html", "statusCode": 404})
        csp = settings["globalHeaders"]["Content-Security-Policy"]
        for directive in ("default-src 'none'", "style-src 'self'", "img-src 'self'", "form-action 'none'"):
            self.assertIn(directive, csp)
        for forbidden in ("unsafe-inline", "unsafe-eval", "https:", "*"):
            self.assertNotIn(forbidden, csp)

    def test_recovery_page_offers_real_next_steps(self):
        links = {item["href"] for item in self.pages["/404.html"].named("a")}
        self.assertIn("/#getting-started", links)
        self.assertIn("/#downloads", links)
        self.assertIn("https://mapletime.dev", links)

    def test_static_payload_stays_within_the_measured_budget(self):
        authored = sum(path.stat().st_size for path in SITE.iterdir() if path.suffix in (".html", ".css"))
        complete = sum(path.stat().st_size for path in SITE.iterdir() if path.is_file())
        self.assertLessEqual(authored, 32 * 1024)
        self.assertLessEqual(complete, 576 * 1024)

    def test_readiness_stays_in_the_canonical_distribution_contract(self):
        distribution = json.loads((ROOT / "installer" / "distribution.json").read_text(encoding="utf-8"))
        links = self.pages["/"].named("a")
        registration = [link for link in links if link["href"] == distribution["registrationUrl"]]
        self.assertEqual(len(registration), 1)
        self.assertEqual(registration[0]["aria-describedby"], "pilot-access")
        self.assertTrue(self.pages["/"].with_id("pilot-access"))
        self.assertTrue(self.pages["/"].with_id("download-status"))


if __name__ == "__main__":
    unittest.main()
