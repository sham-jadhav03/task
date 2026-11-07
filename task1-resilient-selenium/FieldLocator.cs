using OpenQA.Selenium;

namespace ResilientForm;

/// <summary>
/// Finds form fields by what a human would use to recognise them (label text, nearby text,
/// aria-label, placeholder, name/id hints) instead of a fixed XPath/CSS selector.
/// Every candidate element is scored; the highest score wins. Wrong-but-similar fields
/// (e.g. "Last Name" for "First Name", "Country" for "State") get negative scores.
/// </summary>
public static class FieldLocator
{
    // arguments: [0]=kind (text|radio|select), [1]=positive keywords, [2]=negative keywords, [3]=wanted values
    private const string Js = """
const kind = arguments[0], pos = arguments[1], neg = arguments[2], wanted = arguments[3];
const norm = s => (s || '').replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
const textOf = n => n ? norm(n.textContent) : '';
const isVisible = el => { const r = el.getBoundingClientRect(), cs = getComputedStyle(el);
  return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none'; };
const match = (text, k) => {
  if (!text) return 0;
  if (text === k) return 2;
  if ((' ' + text + ' ').includes(' ' + k + ' ')) return 1.5;
  if (text.replace(/ /g, '').includes(k.replace(/ /g, ''))) return 1;
  return 0; };
const hasField = n => n.querySelector && n.querySelector('input,select,textarea,button');

function labelTexts(el) {
  const out = [];
  if (el.labels) for (const l of el.labels) out.push(textOf(l));
  const al = el.getAttribute('aria-label'); if (al) out.push(norm(al));
  const lb = el.getAttribute('aria-labelledby');
  if (lb) lb.split(/\s+/).forEach(id => { const n = document.getElementById(id); if (n) out.push(textOf(n)); });
  return out.filter(Boolean);
}
function hintTexts(el) {
  const out = [];
  for (const a of ['placeholder','name','id','title','data-testid','data-test','autocomplete']) {
    const v = el.getAttribute(a); if (v) out.push(norm(v)); }
  return out;
}
function nearbyText(el) {           // text sitting just before the field (cell, span, div, bare text)
  let n = el;
  for (let depth = 0; n && depth < 3; depth++, n = n.parentElement) {
    let t = n.previousSibling;
    while (t && t.nodeType === 3 && !t.textContent.trim()) t = t.previousSibling;
    if (t && t.nodeType === 3) return norm(t.textContent);
    let p = n.previousElementSibling;
    while (p && !textOf(p)) p = p.previousElementSibling;
    if (p && !hasField(p) && textOf(p).length < 60) return textOf(p);
  }
  return '';
}
function nextText(el) {             // <input type=radio> Male
  let t = el.nextSibling;
  while (t && t.nodeType === 3 && !t.textContent.trim()) t = t.nextSibling;
  if (t && t.nodeType === 3) return norm(t.textContent);
  const e = el.nextElementSibling;
  if (e && !hasField(e) && textOf(e).length < 30) return textOf(e);
  return '';
}
function scoreText(el) {
  let s = 0;
  const groups = [[labelTexts(el), 3], [[nearbyText(el)], 2], [hintTexts(el), 1]];
  for (const [texts, w] of groups) for (const t of texts) {
    for (const k of pos) s += match(t, k) * w * 2;
    for (const k of neg) s -= match(t, k) * w * 6;
  }
  return s;
}

let best = null, bestScore = 0;
const consider = (el, s) => { if (s > bestScore) { best = el; bestScore = s; } };

if (kind === 'text') {
  document.querySelectorAll('input:not([type]),input[type=text],input[type=search],textarea')
    .forEach(el => { if (isVisible(el) && !el.disabled && !el.readOnly) consider(el, scoreText(el)); });
} else if (kind === 'select') {
  document.querySelectorAll('select').forEach(el => {
    if (!isVisible(el) || el.disabled) return;
    let s = scoreText(el);
    const opts = [...el.options].map(o => norm(o.text));
    if (wanted.length && wanted.some(w => opts.includes(norm(w)))) s += 2;
    consider(el, s); });
} else if (kind === 'radio') {
  const wantedN = wanted.map(norm);
  document.querySelectorAll('input[type=radio]').forEach(el => {
    const texts = labelTexts(el).concat([nextText(el)]);
    let m = texts.some(t => wantedN.includes(t)) || (el.value && wantedN.includes(norm(el.value)));
    if (!m) return;
    let s = 10;
    for (const h of hintTexts(el)) for (const k of pos) if (match(h, k)) s += 5;
    let anc = el;
    for (let i = 0; i < 5 && anc; i++) {            // is it inside a "Gender" group?
      anc = anc.parentElement;
      if (anc && textOf(anc).length < 200 && pos.some(k => match(textOf(anc), k))) { s += 5; break; }
    }
    consider(el, s); });
}
return best;
""";

    private static IWebElement? RunOnce(IWebDriver d, string kind, string[] pos, string[] neg, string[] wanted) =>
        ((IJavaScriptExecutor)d).ExecuteScript(Js, kind, pos, neg, wanted) as IWebElement;

    /// <summary>Polls (page may render late), checks the main document, then every iframe.</summary>
    public static IWebElement Find(IWebDriver d, string kind, string[] pos, string[] neg, string[] wanted, int timeoutSec = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(timeoutSec);
        do
        {
            d.SwitchTo().DefaultContent();
            var el = RunOnce(d, kind, pos, neg, wanted);
            if (el != null) return el;

            var frames = d.FindElements(By.CssSelector("iframe,frame"));
            for (int i = 0; i < frames.Count; i++)
            {
                try
                {
                    d.SwitchTo().DefaultContent();
                    d.SwitchTo().Frame(i);
                    el = RunOnce(d, kind, pos, neg, wanted);
                    if (el != null) return el;       // driver stays inside this frame
                }
                catch (WebDriverException) { /* frame vanished, keep going */ }
            }
            Thread.Sleep(500);
        } while (DateTime.UtcNow < end);

        d.SwitchTo().DefaultContent();
        throw new NoSuchElementException($"No confident match for field kind '{kind}' ({string.Join("/", pos)}).");
    }
}
