"""Photographs the plugin's settings page (and the per-user Media segments setting) on the demo server."""
import os
import sys
from playwright.sync_api import sync_playwright

URL = os.environ.get("URL", "http://localhost:8096")
USER_ID = os.environ.get("JF_USER_ID", "")
OUT = sys.argv[1] if len(sys.argv) > 1 else "docs/images"
os.makedirs(OUT, exist_ok=True)


def union_box(page, selectors):
    """Bounding box covering every element matched by the selectors (page coordinates)."""
    return page.evaluate(
        """(sels) => {
            const rects = sels.flatMap(s => [...document.querySelectorAll(s)])
                .filter(e => e.offsetParent !== null)
                .map(e => e.getBoundingClientRect());
            const x = Math.min(...rects.map(r => r.left)) - 16, y = Math.min(...rects.map(r => r.top)) - 16;
            const r = Math.max(...rects.map(r => r.right)) + 16, b = Math.max(...rects.map(r => r.bottom)) + 16;
            return { x: Math.max(0, x + scrollX), y: Math.max(0, y + scrollY), width: r - x, height: b - y };
        }""",
        selectors,
    )


with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 1280, "height": 900}, device_scale_factor=1)

    page.goto(f"{URL}/web/#/login")
    page.wait_for_timeout(4000)
    if page.locator(".btnManual").first.is_visible():
        page.locator(".btnManual").first.click()
    page.fill("#txtManualName", "admin")
    page.fill("#txtManualPassword", "demo-password")
    page.locator(".manualLoginForm button[type=submit]").click()
    page.wait_for_timeout(5000)

    page.goto(f"{URL}/web/#/configurationpage?name=Comskip")
    page.wait_for_selector("#ComskipConfigPage #IncludeLiveTvRecordings", state="attached", timeout=30000)
    page.wait_for_function("document.querySelector('#comskipStatus') && !document.querySelector('#comskipStatus').textContent.includes('Loading')", timeout=30000)
    page.wait_for_timeout(1500)

    page.screenshot(path=f"{OUT}/settings-full.png", full_page=True)
    page.screenshot(path=f"{OUT}/settings-status.png",
                    clip=union_box(page, ["#ComskipConfigPage h2.sectionTitle", "#comskipStatus", "#libraryList", "#AutoAnalyzeNewItems"]))
    page.screenshot(path=f"{OUT}/settings-processing.png",
                    clip=union_box(page, ["#ComskipPath", "#FfmpegPath", "#Sanitize", "#HardwareDecode", "#SettleSeconds", "#WriteEdlSidecar"]))
    page.screenshot(path=f"{OUT}/settings-remove-commercials.png",
                    clip=union_box(page, ["#RemoveCommercials", "#CutMaxRemovedPercent", "#CutMinRemainingMinutes"]))

    # The per-user setting every viewer must change (shown set to "Ask to skip"; not saved).
    page.goto(f"{URL}/web/#/mypreferencesplayback?userId={USER_ID}")
    page.wait_for_selector("#segmentTypeAction__Commercial", state="attached", timeout=30000)
    page.select_option("#segmentTypeAction__Commercial", "AskToSkip")
    page.wait_for_timeout(800)
    page.locator("#segmentTypeAction__Commercial").scroll_into_view_if_needed()
    page.screenshot(path=f"{OUT}/user-playback-commercial.png",
                    clip=union_box(page, [".segmentTypeAction"]))

    browser.close()
    print("saved", sorted(os.listdir(OUT)))
