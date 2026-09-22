using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Cyberklar.AdLogin;

public sealed class AdLoginClient : IDisposable
{
    private const string LoginUrl = "https://cyberklar.ventures.dk/Login/ADlogin";
    private const string LektionerUrl = "https://cyberklar.ventures.dk/Lektioner";
    private const string FailureMarker = "Vi kunne ikke logge dig ind";
    private const string TestOpgaveMarker = "begynd testopgave";
    private const string NextButtonId = "NaesteImageButton";
    private const string AfslutButtonId = "AfslutButton";
    private const string FinishedPanelId = "MainContent_NormalNestedContent_FinishedPanel";

    private readonly CookieContainer _cookieContainer = new();
    private readonly HttpClient _httpClient;

    public AdLoginClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookieContainer,
            UseCookies = true,
            AllowAutoRedirect = true,
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Cyberklar.AdLogin/1.0");
    }

    public CookieContainer Cookies => _cookieContainer;

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var getResponse = await _httpClient.GetAsync(LoginUrl, cancellationToken);
        getResponse.EnsureSuccessStatusCode();
        var loginPageHtml = await getResponse.Content.ReadAsStringAsync(cancellationToken);

        var formFields = ExtractFormFields(loginPageHtml, username, password);

        using var content = new FormUrlEncodedContent(formFields);
        var postResponse = await _httpClient.PostAsync(LoginUrl, content, cancellationToken);
        var postHtml = await postResponse.Content.ReadAsStringAsync(cancellationToken);
        var finalUrl = postResponse.RequestMessage?.RequestUri?.ToString();

        if (postHtml.Contains(FailureMarker, StringComparison.OrdinalIgnoreCase))
        {
            return new LoginResult(false, "Login mislykkedes: forkert brugernavn eller kodeord.", postResponse.StatusCode, finalUrl);
        }

        // Successfult login bør ende på /Lektioner. Hvis POST-svaret ikke allerede
        // landede der (fx pga. en klient-side redirect HttpClient ikke følger),
        // bekræftes adgangen med et eksplicit kald, der genbruger session-cookien.
        if (finalUrl is null || !finalUrl.Contains("/Lektioner", StringComparison.OrdinalIgnoreCase))
        {
            var lektionerResponse = await _httpClient.GetAsync(LektionerUrl, cancellationToken);
            finalUrl = lektionerResponse.RequestMessage?.RequestUri?.ToString();

            if (!lektionerResponse.IsSuccessStatusCode ||
                finalUrl is null ||
                !finalUrl.Contains("/Lektioner", StringComparison.OrdinalIgnoreCase))
            {
                return new LoginResult(false, "Login lykkedes ikke: kunne ikke tilgå /Lektioner efter login.", lektionerResponse.StatusCode, finalUrl);
            }
        }

        return new LoginResult(true, "Login lykkedes.", postResponse.StatusCode, finalUrl);
    }

    public Task<LessonResult> OpenFirstUnfinishedLessonAsync(CancellationToken cancellationToken = default)
        => OpenLessonCoreAsync(_ => true, "Ingen lektions-links fundet i 'NotFinishedPanel'.", cancellationToken);

    public Task<LessonResult> OpenLessonAsync(string lessonName, CancellationToken cancellationToken = default)
        => OpenLessonCoreAsync(
            name => string.Equals(name, lessonName, StringComparison.Ordinal),
            $"Kunne ikke finde lektionen '{lessonName}' i 'NotFinishedPanel'.",
            cancellationToken);

    private async Task<LessonResult> OpenLessonCoreAsync(Func<string, bool> lessonNamePredicate, string notFoundMessage, CancellationToken cancellationToken)
    {
        var pageResponse = await _httpClient.GetAsync(LektionerUrl, cancellationToken);
        pageResponse.EnsureSuccessStatusCode();
        var html = await pageResponse.Content.ReadAsStringAsync(cancellationToken);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var form = doc.DocumentNode.SelectSingleNode("//form")
            ?? throw new InvalidOperationException("Kunne ikke finde formularen på /Lektioner.");

        var panel = doc.DocumentNode.SelectSingleNode("//*[@id='MainContent_NormalNestedContent_NotFinishedPanel']")
            ?? throw new InvalidOperationException("Kunne ikke finde 'NotFinishedPanel' på /Lektioner-siden.");

        var firstLink = (panel.SelectNodes(".//input[@type='image']") ?? Enumerable.Empty<HtmlNode>())
            .FirstOrDefault(input => lessonNamePredicate(ExtractLessonName(input)))
            ?? throw new InvalidOperationException(notFoundMessage);

        var linkName = firstLink.GetAttributeValue("name", null)
            ?? throw new InvalidOperationException("Lektions-linket mangler et 'name'-attribut.");
        var lessonName = ExtractLessonName(firstLink);

        var fields = new Dictionary<string, string>();
        foreach (var input in form.SelectNodes(".//input") ?? Enumerable.Empty<HtmlNode>())
        {
            var name = input.GetAttributeValue("name", null);
            if (string.IsNullOrEmpty(name) || input.GetAttributeValue("type", string.Empty) == "image")
            {
                // Kun det billed-link, brugeren "klikker", skal sendes med — ligesom en browser gør.
                continue;
            }

            fields[name] = HtmlEntity.DeEntitize(input.GetAttributeValue("value", string.Empty)) ?? string.Empty;
        }

        // <input type="image"> sender klik-koordinater som "navn.x"/"navn.y" i stedet for "navn=værdi".
        fields[$"{linkName}.x"] = "1";
        fields[$"{linkName}.y"] = "1";

        using var content = new FormUrlEncodedContent(fields);
        var postResponse = await _httpClient.PostAsync(LektionerUrl, content, cancellationToken);
        postResponse.EnsureSuccessStatusCode();
        var resultHtml = await postResponse.Content.ReadAsStringAsync(cancellationToken);

        var resultDoc = new HtmlDocument();
        resultDoc.LoadHtml(resultHtml);
        var title = resultDoc.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim();

        return new LessonResult(lessonName, postResponse.RequestMessage?.RequestUri?.ToString(), title, resultHtml);
    }

    public async Task<TestOpgaveResult> AdvanceToTestOpgaveAsync(string currentUrl, string currentHtml, int maxSteps = 30, CancellationToken cancellationToken = default)
    {
        for (var step = 0; step < maxSteps; step++)
        {
            if (currentHtml.Contains(TestOpgaveMarker, StringComparison.OrdinalIgnoreCase))
            {
                return new TestOpgaveResult(true, step, currentUrl, currentHtml);
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(currentHtml);

            _ = doc.GetElementbyId(NextButtonId)
                ?? throw new InvalidOperationException($"Kunne ikke finde '{NextButtonId}' — kan ikke fortsætte til testopgaven.");

            var form = doc.DocumentNode.SelectSingleNode("//form")
                ?? throw new InvalidOperationException("Kunne ikke finde formularen på lektionssiden.");

            // Efterligner ForsoegSubmit('naeste')/ToDoAndTrySubmit fra sidens JavaScript.
            (currentHtml, currentUrl) = await SubmitGoToValueAsync(currentUrl, form, "naeste", cancellationToken);
        }

        return new TestOpgaveResult(false, maxSteps, currentUrl, currentHtml);
    }

    public async Task<FinishResult> ConfirmAfslutAsync(string currentUrl, string currentHtml, CancellationToken cancellationToken = default)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(currentHtml);

        var afslutButton = doc.GetElementbyId(AfslutButtonId)
            ?? throw new InvalidOperationException($"Kunne ikke finde '{AfslutButtonId}' på siden.");

        var form = doc.DocumentNode.SelectSingleNode("//form")
            ?? throw new InvalidOperationException("Kunne ikke finde formularen på siden.");

        // AfslutButton viser typisk en bekræftelses-dialog (JS confirm/modal), inden den kalder
        // ForsoegSubmit(<goToValue>). Da vi ikke kører JavaScript, "bekræfter" vi ved at sende
        // det samme postback direkte — det er netop hvad et klik på "OK" i dialogen ville udløse.
        var onclick = afslutButton.GetAttributeValue("onclick", string.Empty);
        var goToValueMatch = Regex.Match(onclick, @"ForsoegSubmit\('([^']+)'\)");
        var goToValue = goToValueMatch.Success ? goToValueMatch.Groups[1].Value : "afslut";

        var (resultHtml, finalUrl) = await SubmitGoToValueAsync(currentUrl, form, goToValue, cancellationToken);

        return new FinishResult(finalUrl, resultHtml);
    }

    public async Task<QuizResult> AnswerQuizAsync(string currentUrl, string currentHtml, int[][] answerKey, CancellationToken cancellationToken = default)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(currentHtml);

        var pageIndices = GetQuizPageIndices(doc);

        if (pageIndices.Count != answerKey.Length)
        {
            throw new InvalidOperationException(
                $"Quizzen har {pageIndices.Count} opgave(r), men facitlisten har {answerKey.Length}.");
        }

        var form = doc.DocumentNode.SelectSingleNode("//form")
            ?? throw new InvalidOperationException("Kunne ikke finde quiz-formularen.");

        for (var i = 0; i < pageIndices.Count; i++)
        {
            SetQuizAnswers(form, answerKey[i], i + 1);

            // Sidste opgave afsluttes ved at "trykke" AfslutButton (AfslutModal sætter GoToValue='afslut'
            // og bekræfter dialogen), øvrige opgaver navigeres videre via opgave-fanebladenes ForsoegSubmit(N).
            var goToValue = i < pageIndices.Count - 1 ? pageIndices[i + 1] : "afslut";
            (currentHtml, currentUrl) = await SubmitGoToValueAsync(currentUrl, form, goToValue, cancellationToken);

            if (i < pageIndices.Count - 1)
            {
                doc = new HtmlDocument();
                doc.LoadHtml(currentHtml);
                form = doc.DocumentNode.SelectSingleNode("//form")
                    ?? throw new InvalidOperationException($"Kunne ikke finde quiz-formularen på opgave {i + 2}.");
            }
        }

        return new QuizResult(pageIndices.Count, currentUrl, currentHtml);
    }

    /// <summary>
    /// Klikker sig igennem quizzen uden at afgive nogen svar, blot for at nå frem til resultatrapporten,
    /// hvis "Rigtigt svar"-sektion afslører facitlisten (se <see cref="ParseCorrectAnswers"/>).
    /// </summary>
    public async Task<QuizResult> ProbeQuizForAnswersAsync(string currentUrl, string currentHtml, CancellationToken cancellationToken = default)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(currentHtml);

        var opgaveCount = GetQuizPageIndices(doc).Count;
        var emptyAnswerKey = Enumerable.Range(0, opgaveCount).Select(_ => Array.Empty<int>()).ToArray();

        return await AnswerQuizAsync(currentUrl, currentHtml, emptyAnswerKey, cancellationToken);
    }

    /// <summary>
    /// Udleder facitlisten fra resultatrapporten (ProeveforloebsrapportCyberKlar.aspx): for hver opgave
    /// listes alle svarmuligheder i "Rigtigt svar"-sektionen med et &lt;img&gt;, hvor alt="korrekt" markerer
    /// de rigtige svar (og alt="frame" de forkerte) — uafhængigt af, hvad der reelt blev afgivet.
    /// </summary>
    public static int[][] ParseCorrectAnswers(string reportHtml)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(reportHtml);

        var opgavePanels = doc.DocumentNode.SelectNodes("//div[starts-with(@id,'collapseOpgave')]")
            ?? throw new InvalidOperationException("Kunne ikke finde opgave-sektionerne på resultatrapporten.");

        var answerKey = new List<int[]>();

        foreach (var panel in opgavePanels)
        {
            var correctHeader = panel.SelectSingleNode(".//tr[td/strong[contains(text(),'Rigtigt svar')]]")
                ?? throw new InvalidOperationException("Kunne ikke finde 'Rigtigt svar'-sektionen for en opgave på resultatrapporten.");

            var correctIndices = new List<int>();
            var optionIndex = 0;

            foreach (var row in correctHeader.SelectNodes("following-sibling::tr") ?? Enumerable.Empty<HtmlNode>())
            {
                if (row.SelectSingleNode(".//strong[contains(text(),'Dit svar')]") is not null)
                {
                    break;
                }

                var img = row.SelectSingleNode(".//img[@alt]");
                if (img is null)
                {
                    continue;
                }

                if (string.Equals(img.GetAttributeValue("alt", string.Empty), "korrekt", StringComparison.OrdinalIgnoreCase))
                {
                    correctIndices.Add(optionIndex);
                }

                optionIndex++;
            }

            answerKey.Add([.. correctIndices]);
        }

        return [.. answerKey];
    }

    private static List<string> GetQuizPageIndices(HtmlDocument doc)
    {
        var pageLinks = doc.DocumentNode.SelectNodes("//a[contains(concat(' ', normalize-space(@class), ' '), ' gotolink ')]")
            ?? throw new InvalidOperationException("Kunne ikke finde opgave-navigationen i quizzen.");

        return pageLinks
            .Select(a => Regex.Match(a.GetAttributeValue("onclick", string.Empty), @"ForsoegSubmit\((\d+)\)"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    private static string ExtractLessonName(HtmlNode lessonInput)
    {
        var name = lessonInput.GetAttributeValue("name", string.Empty);
        return name[(name.LastIndexOf('$') + 1)..];
    }

    /// <summary>
    /// Tæller de resterende (ikke gennemførte) lektioner i "Mine lektioner"-panelet
    /// (&lt;div id="collapseMine"&gt;) på /Lektioner.
    /// </summary>
    public async Task<int> CountRemainingLessonsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(LektionerUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var panel = doc.DocumentNode.SelectSingleNode("//*[@id='collapseMine']");
        if (panel is null)
        {
            return 0;
        }

        return (panel.SelectNodes(".//input[@type='image']") ?? Enumerable.Empty<HtmlNode>()).Count();
    }

    public async Task<bool> IsLessonCompletedAsync(string lessonName, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(LektionerUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var panel = doc.DocumentNode.SelectSingleNode($"//*[@id='{FinishedPanelId}']");
        if (panel is null)
        {
            return false;
        }

        return (panel.SelectNodes(".//input[@type='image']") ?? Enumerable.Empty<HtmlNode>())
            .Any(input => input.GetAttributeValue("name", string.Empty).EndsWith(lessonName, StringComparison.Ordinal));
    }

    private static void SetQuizAnswers(HtmlNode form, int[] correctIndices, int opgaveNummer)
    {
        var answerInputs = form.SelectNodes(".//input[starts-with(@name,'svar')]")
            ?? throw new InvalidOperationException($"Fandt ingen svarmuligheder på opgave {opgaveNummer}.");

        foreach (var index in correctIndices)
        {
            if (index < 0 || index >= answerInputs.Count)
            {
                throw new InvalidOperationException(
                    $"Facitlistens indeks {index} er uden for antal svarmuligheder ({answerInputs.Count}) på opgave {opgaveNummer}.");
            }
        }

        // Efterligner Svar(N) fra sidens JavaScript, som sætter det tilhørende skjulte 'svarN'-felt.
        foreach (var input in answerInputs)
        {
            var name = input.GetAttributeValue("name", string.Empty);
            var index = int.Parse(name["svar".Length..]);
            input.SetAttributeValue("value", correctIndices.Contains(index) ? "true" : "false");
        }
    }

    private async Task<(string Html, string Url)> SubmitGoToValueAsync(string currentUrl, HtmlNode form, string goToValue, CancellationToken cancellationToken)
    {
        var actionUrl = new Uri(new Uri(currentUrl), form.GetAttributeValue("action", string.Empty)).ToString();

        var fields = new Dictionary<string, string>();
        foreach (var input in form.SelectNodes(".//input") ?? Enumerable.Empty<HtmlNode>())
        {
            // <input type="button"> sendes aldrig med af en browser ved et almindeligt form-submit.
            if (string.Equals(input.GetAttributeValue("type", string.Empty), "button", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = input.GetAttributeValue("name", null);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            fields[name] = HtmlEntity.DeEntitize(input.GetAttributeValue("value", string.Empty)) ?? string.Empty;
        }

        fields["GoToValue"] = goToValue;
        fields["sluttid"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        using var content = new FormUrlEncodedContent(fields);
        var response = await _httpClient.PostAsync(actionUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var url = response.RequestMessage?.RequestUri?.ToString() ?? actionUrl;
        return (html, url);
    }

    private static Dictionary<string, string> ExtractFormFields(string html, string username, string password)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var form = doc.DocumentNode.SelectSingleNode("//form")
            ?? throw new InvalidOperationException("Kunne ikke finde login-formularen på siden.");

        var fields = new Dictionary<string, string>();

        foreach (var input in form.SelectNodes(".//input") ?? Enumerable.Empty<HtmlNode>())
        {
            var name = input.GetAttributeValue("name", null);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            fields[name] = HtmlEntity.DeEntitize(input.GetAttributeValue("value", string.Empty)) ?? string.Empty;
        }

        var userNameField = fields.Keys.FirstOrDefault(k => k.EndsWith("txtUserName", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Kunne ikke finde brugernavn-feltet i login-formularen.");
        var passwordField = fields.Keys.FirstOrDefault(k => k.EndsWith("txtADPassword", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Kunne ikke finde kodeord-feltet i login-formularen.");

        fields[userNameField] = username;
        fields[passwordField] = password;

        return fields;
    }

    public void Dispose() => _httpClient.Dispose();
}
