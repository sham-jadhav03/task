#!/usr/bin/env python3
"""
Plain-English test runner: an LLM drives a real browser (Playwright) step by step.

    python nl_test_runner.py "Log in as a customer and verify the dashboard is shown" --url https://phptravels.com/demo

Nothing here is specific to any website: each step the tool shows the model a compact snapshot of the
visible, interactive elements (numbered), the model returns ONE JSON action, the tool executes it, repeat.
The model finishes with a verdict + evidence; the tool checks that evidence really is on the page.
"""
import argparse, json, os, re, sys, time
from datetime import datetime
from pathlib import Path

import anthropic
from playwright.sync_api import sync_playwright

MODEL = os.environ.get("LLM_MODEL", "claude-sonnet-5-5")

SNAPSHOT_JS = r"""
() => {
  document.querySelectorAll('[data-agent-id]').forEach(e => e.removeAttribute('data-agent-id'));
  const sel = 'a[href],button,input,select,textarea,summary,[role=button],[role=link],[role=tab],[role=menuitem],[role=checkbox],[role=radio],[onclick]';
  const vis = e => { const r = e.getBoundingClientRect(), cs = getComputedStyle(e);
                     return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none'; };
  const clip = (s, n) => (s || '').replace(/\s+/g, ' ').trim().slice(0, n);
  const items = []; let id = 0;
  for (const e of document.querySelectorAll(sel)) {
    if (!vis(e)) continue;
    if (++id > 150) break;
    e.setAttribute('data-agent-id', id);
    const tag = e.tagName.toLowerCase(), type = e.getAttribute('type') || '';
    const label = (e.labels && e.labels[0] && e.labels[0].innerText) || e.getAttribute('aria-label') || '';
    let value = '';
    if (tag === 'select') value = e.options[e.selectedIndex] ? e.options[e.selectedIndex].text : '';
    else if (tag === 'input' && type !== 'password') value = e.value;
    items.push({ id, tag, type, text: clip(e.innerText, 60), label: clip(label, 40),
      placeholder: e.getAttribute('placeholder') || '', name: e.getAttribute('name') || '',
      value: clip(value, 30), href: clip(e.getAttribute('href'), 60),
      checked: !!e.checked, disabled: !!e.disabled,
      options: tag === 'select' ? [...e.options].slice(0, 25).map(o => clip(o.text, 25)) : undefined });
  }
  return { url: location.href, title: document.title, items, text: clip(document.body.innerText, 2500) };
}
"""

SYSTEM = """You are a QA automation agent operating a real web browser to execute a test case written in plain English.
Each turn you get: the test case, the history of actions so far, and a snapshot of the current page
(URL, title, visible text, numbered interactive elements). Reply with EXACTLY ONE JSON object and nothing else.

Actions:
{"action":"goto","url":"https://..."}
{"action":"click","id":N}
{"action":"fill","id":N,"value":"text"}
{"action":"select","id":N,"value":"option text"}
{"action":"check","id":N}
{"action":"press","key":"Enter"}
{"action":"scroll"}
{"action":"wait","seconds":2}
{"action":"finish","verdict":"pass"|"fail","reason":"short explanation","evidence":"text copied EXACTLY from the page text/URL/title that proves the verdict"}
Add a short "thought" field to every action.

Rules:
- Use only element ids from the CURRENT snapshot. Ids change after every page change.
- Use credentials/data given in the test case. If the test case asks for a login but gives none, use demo credentials
  shown on the page itself. Never invent data that is not in the test case or on the page.
- Do the steps in the test case, then verify the stated expectation using what is actually visible. Do not finish
  with "pass" unless the page shows evidence of the expected outcome. "evidence" must be an exact substring of the
  page text, URL or title.
- If the expected outcome is that something should NOT happen / an error should appear, check that instead.
- If an element you need does not exist, or you are stuck after reasonable attempts, finish with "fail" and say why.
- Dismiss cookie banners/popups if they block the page. Prefer the simplest path to complete the test."""


def fmt_snapshot(s):
    lines = []
    for i in s["items"]:
        bits = [f'[{i["id"]}] {i["tag"]}' + (f' type={i["type"]}' if i["type"] else "")]
        for k in ("label", "text", "placeholder", "name", "value", "href"):
            if i[k]:
                bits.append(f'{k}="{i[k]}"')
        if i["checked"]: bits.append("checked")
        if i["disabled"]: bits.append("disabled")
        if i.get("options"): bits.append("options=" + "|".join(i["options"]))
        lines.append(" ".join(bits))
    return f'URL: {s["url"]}\nTITLE: {s["title"]}\nVISIBLE TEXT: {s["text"]}\nELEMENTS:\n' + "\n".join(lines)


def ask_llm(client, test_case, history, snap_text):
    prompt = (f"TEST CASE:\n{test_case}\n\nHISTORY:\n" + ("\n".join(history) or "(none yet)")
              + f"\n\nCURRENT PAGE:\n{snap_text}\n\nNext action (JSON only):")
    for attempt in range(2):
        resp = client.messages.create(model=MODEL, max_tokens=500, system=SYSTEM,
                                      messages=[{"role": "user", "content": prompt}])
        raw = "".join(b.text for b in resp.content if b.type == "text")
        m = re.search(r"\{.*\}", raw, re.S)
        if m:
            try:
                return json.loads(m.group(0))
            except json.JSONDecodeError:
                pass
        prompt += "\n\nYour last reply was not valid JSON. Reply with one JSON object only."
    raise RuntimeError("Model did not return valid JSON")


def settle(page):
    for state, t in (("domcontentloaded", 5000), ("networkidle", 3000)):
        try: page.wait_for_load_state(state, timeout=t)
        except Exception: pass
    page.wait_for_timeout(500)


def execute(page, act, items):
    a = act["action"]
    if a == "goto":
        page.goto(act["url"], wait_until="domcontentloaded", timeout=30000)
    elif a in ("click", "fill", "select", "check"):
        loc = page.locator(f'[data-agent-id="{act["id"]}"]').first
        if a == "click": loc.click(timeout=7000)
        elif a == "check": loc.check(timeout=7000)
        elif a == "fill": loc.fill(str(act["value"]), timeout=7000)
        else:
            try: loc.select_option(label=str(act["value"]), timeout=5000)
            except Exception: loc.select_option(value=str(act["value"]), timeout=5000)
    elif a == "press": page.keyboard.press(act["key"])
    elif a == "scroll": page.mouse.wheel(0, 700)
    elif a == "wait": page.wait_for_timeout(min(float(act.get("seconds", 1)), 5) * 1000)
    else: raise ValueError(f"unknown action {a}")
    settle(page)


def describe(act, items):
    item = next((i for i in items if i["id"] == act.get("id")), None)
    d = act["action"]
    if item:
        d += f' #{item["id"]} ({item["tag"]} "{item["label"] or item["text"] or item["name"] or item["placeholder"]}")'
    if "value" in act:
        d += ' = "***"' if item and item["type"] == "password" else f' = "{act["value"]}"'
    if "url" in act: d += f' {act["url"]}'
    if "key" in act: d += f' {act["key"]}'
    return d


def run(test_case, url, headed, max_steps, outdir):
    client = anthropic.Anthropic()
    outdir.mkdir(parents=True, exist_ok=True)
    history, steps = [], []
    verdict = {"verdict": "fail", "reason": "Step limit reached without a verdict", "evidence": ""}
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=not headed)
        ctx = browser.new_context(viewport={"width": 1366, "height": 900})
        page = ctx.new_page()
        if url:
            page.goto(url, wait_until="domcontentloaded", timeout=30000); settle(page)
        for n in range(1, max_steps + 1):
            page = ctx.pages[-1]                       # follow new tabs/popups
            snap = page.evaluate(SNAPSHOT_JS)
            act = ask_llm(client, test_case, history, fmt_snapshot(snap))
            if act.get("action") == "finish":
                v = str(act.get("verdict", "fail")).lower()
                ev = str(act.get("evidence", ""))
                haystack = " ".join([snap["url"], snap["title"], snap["text"]]).lower()
                if v == "pass" and (not ev or ev.lower() not in haystack):
                    v, act["reason"] = "fail", f'Model claimed pass but evidence "{ev}" was not found on the page'
                verdict = {"verdict": v, "reason": act.get("reason", ""), "evidence": ev}
                print(f"  step {n}: finish -> {v.upper()}")
                break
            desc = describe(act, snap["items"])
            try:
                execute(page, act, snap["items"])
                result = "ok"
            except Exception as e:
                result = "ERROR: " + str(e).splitlines()[0][:150]
            print(f"  step {n}: {desc} -> {result}   # {act.get('thought', '')}")
            history.append(f"{n}. {desc} -> {result}")
            steps.append({"step": n, "action": desc, "result": result, "thought": act.get("thought", "")})
        try: ctx.pages[-1].screenshot(path=str(outdir / "final.png"), full_page=False)
        except Exception: pass
        browser.close()
    report = {"test_case": test_case, "url": url, **verdict, "steps": steps}
    (outdir / "report.json").write_text(json.dumps(report, indent=2))
    return report


def main():
    ap = argparse.ArgumentParser(description="Run a plain-English test case in a real browser using an LLM.")
    ap.add_argument("test_case", nargs="?", help="Test case text (or use --file)")
    ap.add_argument("--file", help="Text file with one test case")
    ap.add_argument("--url", help="Start URL (default: first URL found in the test case)")
    ap.add_argument("--headed", action="store_true", help="Show the browser window")
    ap.add_argument("--max-steps", type=int, default=25)
    a = ap.parse_args()
    tc = Path(a.file).read_text().strip() if a.file else a.test_case
    if not tc: ap.error("provide a test case or --file")
    url = a.url or (re.search(r"https?://\S+", tc) or [None])[0]
    outdir = Path("runs") / datetime.now().strftime("%Y%m%d-%H%M%S")
    print(f"TEST: {tc}\nURL : {url}\n")
    r = run(tc, url, a.headed, a.max_steps, outdir)
    print(f"\n===== {r['verdict'].upper()} =====\nReason  : {r['reason']}\nEvidence: {r['evidence']}\nReport  : {outdir}/report.json")
    sys.exit(0 if r["verdict"] == "pass" else 1)


if __name__ == "__main__":
    main()
