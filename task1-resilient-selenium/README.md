# Task 1 – Page-change-resilient Selenium tests (C#)

Automates **First Name** (text box), **Gender** (radio) and **State** (dropdown) on
`https://app.cloudqa.io/home/AutomationPracticeForm` without fixed XPath/CSS selectors.

## Setup
Requires .NET 8 SDK and Google Chrome (Selenium Manager downloads the matching driver).

```bash
dotnet run -- --url "https://app.cloudqa.io/home/AutomationPracticeForm"
dotnet run -- --url "file:///path/to/modified-page.html" --first-name Ghansham --gender Female --state Maharashtra --headless
```

| Option | Env var | Default |
|---|---|---|
| `--url` | `FORM_URL` | the CloudQA practice form |
| `--first-name` | `FIRST_NAME` | Ghansham |
| `--gender` | `GENDER` | Male |
| `--state` | `STATE_VALUE` | *(first real option)* |
| `--headless` | – | off |

Exit code is `0` if all three checks pass, `1` otherwise. A screenshot is saved to `out/final.png`.

## How it stays stable when the page changes
`FieldLocator.cs` scores every candidate element in the browser and picks the best one:
1. **Label text** (`<label for>`, wrapping `<label>`, `aria-label`, `aria-labelledby`) – weight 3
2. **Nearby text** (text/cell/span just before the field, or right after a radio) – weight 2
3. **Hints** (placeholder, name, id, title, data-testid, autocomplete; camelCase split) – weight 1
4. **Negative keywords** subtract heavily so look-alikes lose (`Last Name` vs `First Name`, `Country`/`City` vs `State`).
5. Radios: the option label must equal the wanted value (`male` ≠ `female`), plus bonus if it sits in a "Gender/Sex" group.
Field order, ids, names, classes and DOM nesting are never used as the primary signal. It polls for late-rendered
fields, searches iframes, and falls back to clicking the label for custom-styled radios.
After each action the value is read back and verified.

## Limitations
See NOTES.md.
