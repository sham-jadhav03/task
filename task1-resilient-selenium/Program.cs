using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using ResilientForm;

// ---------- configuration: CLI args > environment variables > defaults ----------
string Arg(string name, string env, string def)
{
    var a = Environment.GetCommandLineArgs();
    for (int i = 1; i < a.Length - 1; i++) if (a[i] == "--" + name) return a[i + 1];
    return Environment.GetEnvironmentVariable(env) ?? def;
}
var url       = Arg("url", "FORM_URL", "https://app.cloudqa.io/home/AutomationPracticeForm");
var firstName = Arg("first-name", "FIRST_NAME", "Ghansham");
var gender    = Arg("gender", "GENDER", "Male");
var state     = Arg("state", "STATE_VALUE", "");        // empty = pick first real option
var headless  = Environment.GetCommandLineArgs().Contains("--headless");

string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
var genderSynonyms = Norm(gender) switch
{
    "male" or "m" or "man" => new[] { "male", "m", "man" },
    "female" or "f" or "woman" => new[] { "female", "f", "woman" },
    _ => new[] { Norm(gender) }
};

var opts = new ChromeOptions();
if (headless) opts.AddArgument("--headless=new");
opts.AddArgument("--window-size=1400,1000");
using IWebDriver driver = new ChromeDriver(opts);   // Selenium Manager downloads the driver automatically
var js = (IJavaScriptExecutor)driver;
var results = new List<(string name, bool ok, string detail)>();

void Check(string name, Func<string> body)
{
    try { var d = body(); results.Add((name, true, d)); }
    catch (Exception e) { results.Add((name, false, e.Message.Split('\n')[0])); }
}

try
{
    Console.WriteLine($"Opening {url}");
    driver.Navigate().GoToUrl(url);

    // ---------------- 1. First Name (text box) ----------------
    Check("First Name", () =>
    {
        var el = FieldLocator.Find(driver, "text",
            pos: new[] { "first name", "given name", "forename", "fname" },
            neg: new[] { "last name", "surname", "family name", "full name", "middle name", "user name", "company" },
            wanted: Array.Empty<string>());
        js.ExecuteScript("arguments[0].scrollIntoView({block:'center'})", el);
        el.Clear();
        el.SendKeys(firstName);
        var actual = el.GetAttribute("value");
        return actual == firstName ? $"typed '{actual}'" : throw new Exception($"expected '{firstName}', found '{actual}'");
    });

    // ---------------- 2. Gender (radio button) ----------------
    Check("Gender", () =>
    {
        var el = FieldLocator.Find(driver, "radio",
            pos: new[] { "gender", "sex" }, neg: Array.Empty<string>(), wanted: genderSynonyms);
        js.ExecuteScript("arguments[0].scrollIntoView({block:'center'})", el);
        try { el.Click(); }
        catch (WebDriverException)    // custom-styled radios: the real <input> is hidden, click its label / force it
        {
            js.ExecuteScript(@"const e=arguments[0]; const l=(e.labels&&e.labels[0])||e.closest('label');
                               (l||e).click(); if(!e.checked){e.checked=true;
                               e.dispatchEvent(new Event('input',{bubbles:true}));
                               e.dispatchEvent(new Event('change',{bubbles:true}));}", el);
        }
        return el.Selected ? $"selected '{gender}'" : throw new Exception("radio is not selected after click");
    });

    // ---------------- 3. State (dropdown) ----------------
    Check("State", () =>
    {
        var el = FieldLocator.Find(driver, "select",
            pos: new[] { "state", "province", "region" },
            neg: new[] { "country", "city", "zip", "postal", "district", "gender", "nationality" },
            wanted: state == "" ? Array.Empty<string>() : new[] { state });
        js.ExecuteScript("arguments[0].scrollIntoView({block:'center'})", el);

        var options = el.FindElements(By.TagName("option")).Where(o => o.Enabled).ToList();
        bool Placeholder(IWebElement o) { var t = Norm(o.Text); return t == "" || t.StartsWith("select") || t.StartsWith("choose") || t.StartsWith("please"); }
        IWebElement? pick = state == ""
            ? options.FirstOrDefault(o => !Placeholder(o))
            : options.FirstOrDefault(o => Norm(o.Text) == Norm(state)) ?? options.FirstOrDefault(o => Norm(o.Text).Contains(Norm(state)));
        if (pick == null) throw new Exception($"option '{state}' not found. Available: {string.Join(", ", options.Select(o => o.Text).Take(15))}");

        var all = el.FindElements(By.TagName("option")).ToList();
        js.ExecuteScript(@"const s=arguments[0]; s.selectedIndex=arguments[1];
                           s.dispatchEvent(new Event('input',{bubbles:true}));
                           s.dispatchEvent(new Event('change',{bubbles:true}));", el, all.IndexOf(pick));
        var chosen = (string)js.ExecuteScript("return arguments[0].options[arguments[0].selectedIndex].text", el);
        return Norm(chosen) == Norm(pick.Text) ? $"selected '{chosen.Trim()}'" : throw new Exception($"expected '{pick.Text}', found '{chosen}'");
    });

    Directory.CreateDirectory("out");
    ((ITakesScreenshot)driver).GetScreenshot().SaveAsFile(Path.Combine("out", "final.png"));
}
finally { driver.Quit(); }

Console.WriteLine("\n===== RESULT =====");
foreach (var r in results) Console.WriteLine($"{(r.ok ? "PASS" : "FAIL")}  {r.name,-11} {r.detail}");
var allOk = results.Count == 3 && results.All(r => r.ok);
Console.WriteLine(allOk ? "\nOVERALL: PASS" : "\nOVERALL: FAIL");
return allOk ? 0 : 1;
