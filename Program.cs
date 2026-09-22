using Cyberklar.AdLogin;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

var username = configuration["Credentials:Username"];
var password = configuration["Credentials:Password"];

if (string.IsNullOrWhiteSpace(username))
{
    Console.Write("Brugernavn: ");
    username = Console.ReadLine();
}

if (string.IsNullOrWhiteSpace(password))
{
    Console.Write("Kodeord: ");
    password = ReadPassword();
}

if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
{
    Console.Error.WriteLine("Brugernavn og kodeord skal angives, enten i appsettings.json under \"Credentials\" eller ved prompt.");
    return 1;
}

using var client = new AdLoginClient();

Console.WriteLine($"Logger ind som '{username}'...");

LoginResult result;
try
{
    result = await client.LoginAsync(username, password);
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Netværksfejl under login: {ex.Message}");
    return 1;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Kunne ikke gennemføre login: {ex.Message}");
    return 1;
}

Console.WriteLine($"[{(int)result.StatusCode}] {result.Message}");
if (result.FinalUrl is not null)
{
    Console.WriteLine($"Endte på: {result.FinalUrl}");
}

if (!result.Success)
{
    return 1;
}

var sessionFilePath = Path.Combine(AppContext.BaseDirectory, "session.cookies.json");
SessionStore.Save(client.Cookies, sessionFilePath);
Console.WriteLine($"Session gemt til: {sessionFilePath}");

var lessonsProcessed = 0;

while (true)
{
    var remaining = await client.CountRemainingLessonsAsync();
    if (remaining == 0)
    {
        Console.WriteLine(lessonsProcessed == 0
            ? "Du er helt up-to-date med lektionerne — der er ingen, der mangler at blive gennemført."
            : "Ingen flere lektioner under 'Mine lektioner' — færdig.");
        return 0;
    }

    Console.WriteLine($"{remaining} lektion(er) tilbage under 'Mine lektioner'.");

    if (!await ProcessNextLessonAsync(client))
    {
        return 1;
    }

    lessonsProcessed++;
}

static async Task<bool> ProcessNextLessonAsync(AdLoginClient client)
{
    LessonResult lesson;
    try
    {
        lesson = await client.OpenFirstUnfinishedLessonAsync();
        Console.WriteLine($"Åbnede lektion: {lesson.LessonName}");
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"Kunne ikke åbne den næste lektion: {ex.Message}");
        return false;
    }

    if (lesson.Url is null)
    {
        Console.Error.WriteLine("Lektionen har ingen gyldig URL — kan ikke fortsætte til testopgaven.");
        return false;
    }

    TestOpgaveResult testOpgave;
    try
    {
        testOpgave = await client.AdvanceToTestOpgaveAsync(lesson.Url, lesson.Html);
        if (!testOpgave.Found || testOpgave.FinalUrl is null)
        {
            Console.Error.WriteLine($"Nåede ikke testopgaven inden for {testOpgave.Steps} forsøg.");
            return false;
        }

        Console.WriteLine($"Nåede testopgaven efter {testOpgave.Steps} klik på 'Næste'.");
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"Kunne ikke fortsætte gennem lektionen: {ex.Message}");
        return false;
    }

    int[][] answerKey;
    try
    {
        Console.WriteLine("Afgiver et tomt forsøg for at aflæse facitlisten på resultatrapporten...");
        var probe = await client.ProbeQuizForAnswersAsync(testOpgave.FinalUrl, testOpgave.Html);
        answerKey = AdLoginClient.ParseCorrectAnswers(probe.Html);
        Console.WriteLine($"Fandt facitliste for {answerKey.Length} opgave(r).");
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"Kunne ikke aflæse facitlisten fra resultatrapporten: {ex.Message}");
        return false;
    }

    try
    {
        lesson = await client.OpenLessonAsync(lesson.LessonName);
        if (lesson.Url is null)
        {
            Console.Error.WriteLine("Lektionen har ingen gyldig URL efter genoptagelse.");
            return false;
        }

        testOpgave = await client.AdvanceToTestOpgaveAsync(lesson.Url, lesson.Html);
        if (!testOpgave.Found || testOpgave.FinalUrl is null)
        {
            Console.Error.WriteLine($"Nåede ikke testopgaven igen inden for {testOpgave.Steps} forsøg.");
            return false;
        }

        var quiz = await client.AnswerQuizAsync(testOpgave.FinalUrl, testOpgave.Html, answerKey);
        Console.WriteLine($"Besvarede quizzens {quiz.OpgaveCount} opgave(r) korrekt og afsluttede lektionen.");
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"Kunne ikke gennemføre lektionen med de fundne svar: {ex.Message}");
        return false;
    }

    var completed = await client.IsLessonCompletedAsync(lesson.LessonName);
    Console.WriteLine(completed
        ? $"'{lesson.LessonName}' står nu som gennemført (100%)."
        : $"'{lesson.LessonName}' er stadig ikke gennemført — tjek quizsvarene.");

    return completed;
}

static string ReadPassword()
{
    var password = string.Empty;
    ConsoleKeyInfo key;
    do
    {
        key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Backspace && password.Length > 0)
        {
            password = password[..^1];
            Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            password += key.KeyChar;
            Console.Write('*');
        }
    } while (key.Key != ConsoleKey.Enter);

    Console.WriteLine();
    return password;
}
