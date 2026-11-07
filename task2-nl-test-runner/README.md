# Task 2 – Plain-English → browser test (Python + Playwright + Claude)

```bash
pip install -r requirements.txt && playwright install chromium
export ANTHROPIC_API_KEY=sk-ant-...        # optional: export LLM_MODEL=<model name>

python nl_test_runner.py "Log in as a customer and verify the dashboard is shown" --url https://phptravels.com/demo --headed
python nl_test_runner.py "Search for hotels in Dubai and verify results are listed" --url https://phptravels.com/demo
python nl_test_runner.py --file my_test.txt --url https://any-other-site.com
```
Output: step log, `PASS`/`FAIL` with reason + evidence, `runs/<time>/report.json`, `runs/<time>/final.png`.
Exit code `0` = pass, `1` = fail.

## How it works
1. Open the start URL in Chromium.
2. Snapshot the page: URL, title, visible text and every visible interactive element, numbered.
3. Send test case + history + snapshot to the LLM, which returns one JSON action (click/fill/select/goto/press/...).
4. Execute it with Playwright, wait for the page to settle, repeat (max 25 steps).
5. The model ends with `finish` + verdict + **evidence**. A `pass` is accepted only if the evidence string is
   actually present in the page text/URL/title, so the model can't just claim success.

No site-specific selectors or URLs are hard-coded. See NOTES.md for limitations.
