# Approach and limitations

## Task 1 (C# / Selenium)
**Approach:** identify fields the way a person does. A JS scorer runs in the page and ranks every candidate input by
label text, nearby text, aria-label and name/id/placeholder hints, with negative keywords to reject look-alikes
(Last Name, Country, Female). Config (URL, values) comes from args/env. Values are read back to verify.

**Limitations**
- Heuristic: if a label is renamed to something unrelated ("Given handle") or only an image/icon, it can fail or mismatch.
- Only native `<select>`, `<input type=radio>` and text inputs. Custom div-based dropdowns (React-select etc.) and shadow DOM are not handled.
- Keyword lists are English only.
- iframes are searched but only one level deep.
- I could not run it against the live page from my authoring environment (no .NET SDK/network), so run it once yourself before recording.

## Task 2 (Python / Playwright / Claude)
**Approach:** LLM-in-the-loop agent. Compact numbered snapshot of the page each step -> one JSON action -> execute -> repeat.
Verdicts need evidence that is verified against the page text.

**Limitations**
- Non-deterministic and slower than scripted tests; each step costs an API call.
- Snapshot has no iframes, shadow DOM, canvas or CAPTCHAs/2FA. Very long pages are truncated (150 elements, 2500 chars of text).
- "Pass" means the evidence text exists on the page; it can't judge visual layout, and a vague test case gives a vague verdict.
- Sends page content (including typed non-password values) to the model provider - don't point it at sensitive systems.
- Sites with bot protection may block headless Chromium.
