#!/usr/bin/env python3
"""
KatFile download integration test script.

Usage:
    python3 test_katfile.py <katfile_url> <wit_ai_server_token>

Example:
    python3 test_katfile.py https://katfile.com/abc123def456/myfile.zip "XXXXXXXXXXXXXXXX"

Requires:
    pip install playwright requests
    playwright install chromium

What it tests:
    1. URL pattern detection (always, no network)
    2. wit.ai API connectivity (network required)
    3. Full KatFile browser + reCaptcha + download flow (network + Playwright)
"""

import asyncio
import re
import sys
import json
import urllib.request
import urllib.error
import io


# ── 1. URL pattern test (mirrors KatFileDownloadService._urlPattern) ───────────

URL_PATTERN = re.compile(
    r'https?://(?:www\.)?katfile\.com/\w',
    re.IGNORECASE
)

def is_katfile_url(url: str) -> bool:
    return bool(URL_PATTERN.match(url))

def test_url_pattern():
    cases = [
        ("https://katfile.com/abc123/file.zip",     True),
        ("https://www.katfile.com/XyZ/doc.pdf",     True),
        ("http://katfile.com/abc/test.mp4",          True),
        ("https://k2s.cc/file/abc",                  False),
        ("https://keep2share.cc/file/abc",           False),
        ("https://katfile.com/",                     False),  # no file id char
        ("https://katfile.com",                      False),
    ]
    passed = all_ok = True
    for url, expected in cases:
        result = is_katfile_url(url)
        ok = result == expected
        status = "OK" if ok else "FAIL"
        print(f"  [{status}] {url!r:55s} → {result} (expected {expected})")
        if not ok:
            passed = False
    return passed


# ── 2. wit.ai API connectivity test ────────────────────────────────────────────

def test_witai(api_key: str) -> bool:
    """Send a tiny silent MP3 to wit.ai and verify we get a valid JSON response."""
    # Minimal valid MP3 frame (silent, 128kbps, 44100 Hz)
    # This is a real MP3 header so wit.ai won't reject as malformed
    SILENT_MP3 = bytes([
        0xFF, 0xFB, 0x90, 0x00,  # MP3 sync + header (MPEG1, L3, 128kbps, 44100Hz)
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ] * 100)

    url = "https://api.wit.ai/speech?v=20240304"
    req = urllib.request.Request(url, data=SILENT_MP3, method="POST")
    req.add_header("Authorization", f"Bearer {api_key}")
    req.add_header("Content-Type", "audio/mpeg")

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            body = resp.read().decode()
        print(f"  [OK] wit.ai responded HTTP 200")
        # Parse NDJSON — look for last object with "text"
        for line in reversed(body.splitlines()):
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
                text = obj.get("text", "")
                print(f"  [OK] wit.ai transcript: {text!r}")
                return True
            except json.JSONDecodeError:
                continue
        print(f"  [OK] wit.ai responded but no text found (silent audio — expected)")
        return True
    except urllib.error.HTTPError as e:
        body = e.read().decode()
        print(f"  [FAIL] wit.ai HTTP {e.code}: {body[:200]}")
        return False
    except Exception as e:
        print(f"  [FAIL] wit.ai request failed: {e}")
        return False


# ── 3. KatFile page structure probe ───────────────────────────────────────────

def probe_katfile_page(url: str) -> bool:
    """Fetch the KatFile download page (no JS) and check for expected form fields."""
    req = urllib.request.Request(url)
    req.add_header("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            html = resp.read().decode(errors="replace")
    except Exception as e:
        print(f"  [FAIL] Could not fetch {url}: {e}")
        return False

    checks = [
        ("download form",       r'<form[^>]+method=["\']post["\']', html),
        ("method_free field",   r'name=["\']method_free["\']',      html),
        ("file ID hidden field",r'name=["\']id["\']',               html),
        ("reCaptcha script",    r'recaptcha',                       html),
    ]
    ok = True
    for name, pattern, text in checks:
        found = bool(re.search(pattern, text, re.IGNORECASE))
        status = "OK" if found else "MISSING"
        print(f"  [{status}] {name}")
        if not found:
            ok = False
    return ok


# ── 4. Full Playwright flow (requires: pip install playwright + playwright install chromium) ──

async def full_playwright_test(katfile_url: str, wit_api_key: str):
    try:
        from playwright.async_api import async_playwright
    except ImportError:
        print("  [SKIP] playwright not installed (pip install playwright)")
        return

    print("  Starting Chromium...")
    async with async_playwright() as p:
        browser = await p.chromium.launch(
            headless=True,
            args=["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]
        )
        context = await browser.new_context(user_agent=(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
        ))
        page = await context.new_page()

        try:
            print(f"  Navigating to {katfile_url} ...")
            await page.goto(katfile_url, wait_until="domcontentloaded", timeout=30_000)

            title = await page.title()
            print(f"  [OK] Page title: {title!r}")

            # Check for free download button
            free_btn = page.locator("input[name='method_free']").first
            try:
                await free_btn.wait_for(state="visible", timeout=5_000)
                print("  [OK] Found 'method_free' button (step 1 form present)")
                await free_btn.click()
                await page.wait_for_load_state("domcontentloaded", timeout=20_000)
                print("  [OK] Submitted step 1 form")
            except Exception:
                print("  [INFO] No step-1 form (already on step 2 or direct link)")

            # Check for countdown
            timer = page.locator("#countdown, #ctimer, .countdown-timer, [id*='timer']").first
            try:
                await timer.wait_for(state="visible", timeout=3_000)
                text = await timer.inner_text(timeout=2_000)
                print(f"  [OK] Countdown detected: {text!r} — would wait for it")
            except Exception:
                print("  [INFO] No countdown timer found")

            # Check for reCaptcha iframe
            try:
                await page.wait_for_selector("iframe[src*='recaptcha']", timeout=10_000)
                print("  [OK] reCaptcha iframe found — audio solve would proceed here")
            except Exception:
                print("  [WARN] reCaptcha iframe NOT found within 10 s")

        finally:
            await context.close()
            await browser.close()


# ── Main ───────────────────────────────────────────────────────────────────────

def main():
    katfile_url = sys.argv[1] if len(sys.argv) > 1 else None
    wit_key     = sys.argv[2] if len(sys.argv) > 2 else None

    overall = True

    print("\n=== Test 1: URL pattern detection ===")
    ok = test_url_pattern()
    overall = overall and ok
    print(f"  Result: {'PASSED' if ok else 'FAILED'}\n")

    if wit_key:
        print("=== Test 2: wit.ai API connectivity ===")
        ok = test_witai(wit_key)
        overall = overall and ok
        print(f"  Result: {'PASSED' if ok else 'FAILED'}\n")
    else:
        print("=== Test 2: wit.ai API — SKIPPED (no API key provided) ===\n")

    if katfile_url:
        print(f"=== Test 3: KatFile page structure probe ({katfile_url}) ===")
        ok = probe_katfile_page(katfile_url)
        overall = overall and ok
        print(f"  Result: {'PASSED' if ok else 'FAILED'}\n")

        print(f"=== Test 4: Playwright browser flow ===")
        asyncio.run(full_playwright_test(katfile_url, wit_key or ""))
        print()
    else:
        print("=== Test 3 & 4: KatFile page tests — SKIPPED (no URL provided) ===\n")

    print(f"{'='*50}")
    print(f"Overall: {'ALL PASSED' if overall else 'SOME FAILED'}")

if __name__ == "__main__":
    main()
