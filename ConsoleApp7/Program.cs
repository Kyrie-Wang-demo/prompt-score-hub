using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = 5;
        limiter.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("submit", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = 10;
        limiter.QueueLimit = 0;
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/";
        options.Cookie.Name = "PromptShare.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
});
builder.Services.AddDataProtection();

var app = builder.Build();
var settings = AppSettings.Load(app.Configuration, app.Environment);

InitializeDatabase(settings.DatabasePath);

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.Use(async (context, next) =>
{
    context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
    context.Response.Headers.TryAdd("X-Frame-Options", "DENY");
    context.Response.Headers.TryAdd("Referrer-Policy", "no-referrer");
    context.Response.Headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    context.Response.Headers.TryAdd(
        "Content-Security-Policy",
        "default-src 'self'; style-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'");
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/captcha", (IDataProtectionProvider protectionProvider) =>
{
    var left = RandomNumberGenerator.GetInt32(2, 13);
    var right = RandomNumberGenerator.GetInt32(2, 13);
    var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    var protector = protectionProvider.CreateProtector("captcha-v2");
    var token = protector.Protect($"{left + right}|{DateTimeOffset.UtcNow:O}|{nonce}");

    return Results.Ok(new CaptchaResponse($"{left} + {right} = ?", token));
});

app.MapPost("/api/login", async (
    LoginRequest request,
    HttpContext httpContext,
    IDataProtectionProvider protectionProvider) =>
{
    if (!IsCaptchaValid(request.CaptchaAnswer, request.CaptchaToken, protectionProvider))
    {
        return Results.BadRequest(new ApiMessage("Invalid or expired captcha."));
    }

    if (!IsUsernameValid(request.Username) || string.IsNullOrWhiteSpace(request.Password))
    {
        await Task.Delay(RandomNumberGenerator.GetInt32(120, 280));
        return Results.BadRequest(new ApiMessage("Incorrect username or password."));
    }

    using var connection = OpenConnection(settings.DatabasePath);
    var user = FindUser(connection, request.Username!);
    if (user is null || !VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt))
    {
        await Task.Delay(RandomNumberGenerator.GetInt32(120, 280));
        return Results.BadRequest(new ApiMessage("Incorrect username or password."));
    }

    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Role)
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(new ClaimsPrincipal(identity));

    return Results.Ok(new ApiMessage("Signed in."));
}).RequireRateLimiting("login");

app.MapPost("/api/register", async (
    RegisterRequest request,
    HttpContext httpContext,
    IDataProtectionProvider protectionProvider) =>
{
    if (!IsCaptchaValid(request.CaptchaAnswer, request.CaptchaToken, protectionProvider))
    {
        return Results.BadRequest(new ApiMessage("Invalid or expired captcha."));
    }

    if (!IsUsernameValid(request.Username))
    {
        return Results.BadRequest(new ApiMessage("Username must be 3-24 characters and use only letters, numbers, underscore, or dash."));
    }

    if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8 || request.Password.Length > 128)
    {
        return Results.BadRequest(new ApiMessage("Password must be 8-128 characters."));
    }

    using var connection = OpenConnection(settings.DatabasePath);
    if (FindUser(connection, request.Username!) is not null)
    {
        return Results.Conflict(new ApiMessage("Username is already taken."));
    }

    var role = CountUsers(connection) == 0 ? "admin" : "user";
    var user = CreateUser(connection, request.Username!, request.Password, role);
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Role)
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(new ClaimsPrincipal(identity));

    return Results.Ok(new RegisterResult("Account created.", user.Username, user.Role));
}).RequireRateLimiting("login");

app.MapPost("/api/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync();
    return Results.Ok(new ApiMessage("Signed out."));
});

app.MapGet("/api/me", (ClaimsPrincipal user) =>
{
    var authenticated = user.Identity?.IsAuthenticated == true;
    return Results.Ok(new
    {
        authenticated,
        username = authenticated ? user.Identity?.Name : null,
        role = authenticated ? user.FindFirstValue(ClaimTypes.Role) : null,
        canDelete = user.IsInRole("admin")
    });
});

app.MapGet("/api/stats", () =>
{
    using var connection = OpenConnection(settings.DatabasePath);
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT
            COUNT(*),
            COALESCE(ROUND(AVG(Score), 1), 0),
            COALESCE(MAX(Score), 0),
            COALESCE(SUM(ReportCount), 0)
        FROM Submissions
        """;

    using var reader = command.ExecuteReader();
    reader.Read();

    return Results.Ok(new StatsDto(
        reader.GetInt32(0),
        reader.GetDouble(1),
        reader.GetInt32(2),
        reader.GetInt32(3),
        settings.PassingScore));
});

app.MapGet("/api/submissions", (string? q, string? tag, int? minScore, int? page, int? pageSize) =>
{
    var safePage = Math.Max(page ?? 1, 1);
    var safePageSize = Math.Clamp(pageSize ?? 20, 1, 50);
    var offset = (safePage - 1) * safePageSize;
    var filters = new List<string>();

    using var connection = OpenConnection(settings.DatabasePath);
    using var command = connection.CreateCommand();

    if (!string.IsNullOrWhiteSpace(q))
    {
        filters.Add("(Title LIKE $query OR Conversation LIKE $query OR Feedback LIKE $query)");
        command.Parameters.AddWithValue("$query", $"%{q.Trim()}%");
    }

    if (!string.IsNullOrWhiteSpace(tag))
    {
        filters.Add("Tags LIKE $tag");
        command.Parameters.AddWithValue("$tag", $"%{tag.Trim()}%");
    }

    if (minScore is not null)
    {
        filters.Add("Score >= $minScore");
        command.Parameters.AddWithValue("$minScore", Math.Clamp(minScore.Value, 0, 100));
    }

    var whereClause = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";
    command.CommandText = $"""
        SELECT Id, Title, Author, Tags, Conversation, Score, Feedback, CreatedAt, ReportCount
        FROM Submissions
        {whereClause}
        ORDER BY CreatedAt DESC
        LIMIT $limit OFFSET $offset
        """;
    command.Parameters.AddWithValue("$limit", safePageSize);
    command.Parameters.AddWithValue("$offset", offset);

    using var reader = command.ExecuteReader();
    var submissions = new List<SubmissionDto>();
    while (reader.Read())
    {
        submissions.Add(new SubmissionDto(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8)));
    }

    return Results.Ok(new PagedResponse<SubmissionDto>(submissions, safePage, safePageSize));
});

app.MapPost("/api/score", (SubmissionRequest request) =>
{
    var validation = ValidateSubmission(request, settings);
    if (validation is not null)
    {
        return Results.BadRequest(validation);
    }

    var result = ScoreConversation(request.Conversation);
    return Results.Ok(new ScorePreview(result.Score, result.Feedback, result.Breakdown));
}).RequireAuthorization().RequireRateLimiting("submit");

app.MapPost("/api/submissions", (SubmissionRequest request, ClaimsPrincipal user) =>
{
    var validation = ValidateSubmission(request, settings);
    if (validation is not null)
    {
        return Results.BadRequest(validation);
    }

    var result = ScoreConversation(request.Conversation);
    if (result.Score < settings.PassingScore)
    {
        return Results.Ok(new SubmissionResult(false, result.Score, result.Feedback, result.Breakdown, "Score is below the sharing threshold."));
    }

    var fingerprint = Fingerprint(request.Conversation);
    using var connection = OpenConnection(settings.DatabasePath);

    if (SubmissionExists(connection, fingerprint))
    {
        return Results.Conflict(new ApiMessage("This conversation is already shared."));
    }

    using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO Submissions (Title, Author, Tags, Conversation, ContentHash, Score, Feedback, CreatedAt, ReportCount)
        VALUES ($title, $author, $tags, $conversation, $contentHash, $score, $feedback, $createdAt, 0)
        """;
    command.Parameters.AddWithValue("$title", request.Title.Trim());
    command.Parameters.AddWithValue("$author", string.IsNullOrWhiteSpace(request.Author)
        ? user.Identity?.Name ?? ""
        : CleanOptional(request.Author, 40));
    command.Parameters.AddWithValue("$tags", CleanTags(request.Tags));
    command.Parameters.AddWithValue("$conversation", request.Conversation.Trim());
    command.Parameters.AddWithValue("$contentHash", fingerprint);
    command.Parameters.AddWithValue("$score", result.Score);
    command.Parameters.AddWithValue("$feedback", result.Feedback);
    command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
    command.ExecuteNonQuery();

    return Results.Ok(new SubmissionResult(true, result.Score, result.Feedback, result.Breakdown, "Saved to the shared library."));
}).RequireAuthorization().RequireRateLimiting("submit");

app.MapPost("/api/submissions/{id:int}/report", (int id) =>
{
    using var connection = OpenConnection(settings.DatabasePath);
    using var command = connection.CreateCommand();
    command.CommandText = "UPDATE Submissions SET ReportCount = ReportCount + 1 WHERE Id = $id";
    command.Parameters.AddWithValue("$id", id);
    var changed = command.ExecuteNonQuery();

    return changed == 0
        ? Results.NotFound(new ApiMessage("Submission not found."))
        : Results.Ok(new ApiMessage("Report received."));
}).RequireRateLimiting("submit");

app.MapDelete("/api/submissions/{id:int}", (int id) =>
{
    using var connection = OpenConnection(settings.DatabasePath);
    using var command = connection.CreateCommand();
    command.CommandText = "DELETE FROM Submissions WHERE Id = $id";
    command.Parameters.AddWithValue("$id", id);
    var changed = command.ExecuteNonQuery();

    return changed == 0
        ? Results.NotFound(new ApiMessage("Submission not found."))
        : Results.Ok(new ApiMessage("Deleted."));
}).RequireAuthorization("AdminOnly");

app.MapFallbackToFile("index.html");

app.Run();

static ApiMessage? ValidateSubmission(SubmissionRequest request, AppSettings settings)
{
    if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 80)
    {
        return new ApiMessage("Title is required and must be 80 characters or fewer.");
    }

    if (string.IsNullOrWhiteSpace(request.Conversation) ||
        request.Conversation.Length < 30 ||
        request.Conversation.Length > settings.MaxConversationLength)
    {
        return new ApiMessage($"Conversation must be between 30 and {settings.MaxConversationLength} characters.");
    }

    if (request.Author?.Length > 40)
    {
        return new ApiMessage("Author must be 40 characters or fewer.");
    }

    if (request.Tags?.Length > 120)
    {
        return new ApiMessage("Tags must be 120 characters or fewer.");
    }

    return null;
}

static void InitializeDatabase(string databasePath)
{
    var directory = Path.GetDirectoryName(databasePath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    using var connection = OpenConnection(databasePath);
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS Submissions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Title TEXT NOT NULL,
            Author TEXT NOT NULL DEFAULT '',
            Tags TEXT NOT NULL DEFAULT '',
            Conversation TEXT NOT NULL,
            ContentHash TEXT NOT NULL DEFAULT '',
            Score INTEGER NOT NULL,
            Feedback TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            ReportCount INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Submissions_ContentHash ON Submissions(ContentHash);

        CREATE TABLE IF NOT EXISTS Users (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Username TEXT NOT NULL UNIQUE,
            PasswordHash TEXT NOT NULL,
            PasswordSalt TEXT NOT NULL,
            Role TEXT NOT NULL DEFAULT 'user',
            CreatedAt TEXT NOT NULL
        );
        """;
    command.ExecuteNonQuery();

    EnsureColumn(connection, "Author", "TEXT NOT NULL DEFAULT ''");
    EnsureColumn(connection, "Tags", "TEXT NOT NULL DEFAULT ''");
    EnsureColumn(connection, "ContentHash", "TEXT NOT NULL DEFAULT ''");
    EnsureColumn(connection, "ReportCount", "INTEGER NOT NULL DEFAULT 0");
}

static void EnsureColumn(SqliteConnection connection, string name, string definition)
{
    using var exists = connection.CreateCommand();
    exists.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Submissions') WHERE name = $name";
    exists.Parameters.AddWithValue("$name", name);
    var hasColumn = Convert.ToInt32(exists.ExecuteScalar()) > 0;
    if (hasColumn)
    {
        return;
    }

    using var alter = connection.CreateCommand();
    alter.CommandText = $"ALTER TABLE Submissions ADD COLUMN {name} {definition}";
    alter.ExecuteNonQuery();
}

static SqliteConnection OpenConnection(string databasePath)
{
    var connection = new SqliteConnection($"Data Source={databasePath}");
    connection.Open();
    using var pragma = connection.CreateCommand();
    pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
    pragma.ExecuteNonQuery();
    return connection;
}

static bool IsCaptchaValid(string? answer, string? token, IDataProtectionProvider protectionProvider)
{
    if (!int.TryParse(answer?.Trim(), out var submittedAnswer) || string.IsNullOrWhiteSpace(token))
    {
        return false;
    }

    try
    {
        var protector = protectionProvider.CreateProtector("captcha-v2");
        var payload = protector.Unprotect(token).Split('|');
        var expectedAnswer = int.Parse(payload[0]);
        var createdAt = DateTimeOffset.Parse(payload[1]);

        return submittedAnswer == expectedAnswer &&
            DateTimeOffset.UtcNow - createdAt < TimeSpan.FromMinutes(5);
    }
    catch
    {
        return false;
    }
}

static bool SubmissionExists(SqliteConnection connection, string fingerprint)
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT 1 FROM Submissions WHERE ContentHash = $contentHash LIMIT 1";
    command.Parameters.AddWithValue("$contentHash", fingerprint);
    return command.ExecuteScalar() is not null;
}

static int CountUsers(SqliteConnection connection)
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM Users";
    return Convert.ToInt32(command.ExecuteScalar());
}

static UserAccount? FindUser(SqliteConnection connection, string username)
{
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT Id, Username, PasswordHash, PasswordSalt, Role
        FROM Users
        WHERE lower(Username) = lower($username)
        LIMIT 1
        """;
    command.Parameters.AddWithValue("$username", username.Trim());

    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
        return null;
    }

    return new UserAccount(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4));
}

static UserAccount CreateUser(SqliteConnection connection, string username, string password, string role)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = HashPassword(password, salt);

    using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO Users (Username, PasswordHash, PasswordSalt, Role, CreatedAt)
        VALUES ($username, $passwordHash, $passwordSalt, $role, $createdAt)
        RETURNING Id
        """;
    command.Parameters.AddWithValue("$username", username.Trim());
    command.Parameters.AddWithValue("$passwordHash", Convert.ToBase64String(hash));
    command.Parameters.AddWithValue("$passwordSalt", Convert.ToBase64String(salt));
    command.Parameters.AddWithValue("$role", role);
    command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
    var id = Convert.ToInt32(command.ExecuteScalar());

    return new UserAccount(id, username.Trim(), Convert.ToBase64String(hash), Convert.ToBase64String(salt), role);
}

static bool VerifyPassword(string password, string storedHash, string storedSalt)
{
    var salt = Convert.FromBase64String(storedSalt);
    var expectedHash = Convert.FromBase64String(storedHash);
    var candidateHash = HashPassword(password, salt);
    return CryptographicOperations.FixedTimeEquals(candidateHash, expectedHash);
}

static byte[] HashPassword(string password, byte[] salt) =>
    Rfc2898DeriveBytes.Pbkdf2(
        password,
        salt,
        100_000,
        HashAlgorithmName.SHA256,
        32);

static bool IsUsernameValid(string? username)
{
    if (string.IsNullOrWhiteSpace(username))
    {
        return false;
    }

    var trimmed = username.Trim();
    return trimmed.Length is >= 3 and <= 24 &&
        trimmed.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');
}

static string Fingerprint(string value)
{
    var normalized = string.Join(' ', value.Trim().Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
}

static string CleanOptional(string? value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "";
    }

    var trimmed = value.Trim();
    return trimmed[..Math.Min(trimmed.Length, maxLength)];
}

static string CleanTags(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "";
    }

    var tags = value
        .Split(',', '#')
        .Select(tag => tag.Trim())
        .Where(tag => tag.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(8);

    return string.Join(", ", tags);
}

static ScoreResult ScoreConversation(string conversation)
{
    var score = 35;
    var feedback = new List<string>();
    var breakdown = new List<ScoreBreakdown>();

    AddScore(conversation.Length >= 300, 15, "Length", "Enough detail for review.", "Add more context and detail.");
    AddScore(HasAny(conversation, "user", "question", "request"), 10, "User turn", "Includes the user request.", "Keep the original user request.");
    AddScore(HasAny(conversation, "assistant", "model", "answer"), 10, "Assistant turn", "Includes the model answer.", "Keep the model answer.");
    AddScore(HasAny(conversation, "context", "goal", "requirement", "constraint"), 10, "Context", "Includes context, goals, or constraints.", "Add context, goals, or constraints.");
    AddScore(HasAny(conversation, "example", "steps", "code", "summary"), 10, "Reuse value", "Includes examples, steps, code, or a summary.", "Add examples, steps, code, or a summary.");
    AddScore(conversation.Split('\n').Length >= 4, 10, "Structure", "Readable line structure.", "Split the conversation into readable turns.");
    AddScore(!HasAny(conversation, "password is", "credit card", "id card", "phone number"), 10, "Privacy", "No obvious sensitive keywords.", "Remove passwords, payment data, IDs, or phone numbers.");

    score = Math.Clamp(score, 0, 100);
    var feedbackText = feedback.Count == 0 ? "Complete, clear, and useful for sharing." : string.Join(" ", feedback);

    return new ScoreResult(score, feedbackText, breakdown);

    void AddScore(bool passed, int points, string name, string positive, string negative)
    {
        if (passed)
        {
            score += points;
        }
        else
        {
            feedback.Add(negative);
        }

        breakdown.Add(new ScoreBreakdown(name, passed ? points : 0, points, passed ? positive : negative));
    }
}

static bool HasAny(string text, params string[] words) =>
    words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

sealed class AppSettings
{
    private AppSettings(string databasePath, int passingScore, int maxConversationLength)
    {
        DatabasePath = databasePath;
        PassingScore = passingScore;
        MaxConversationLength = maxConversationLength;
    }

    public string DatabasePath { get; }
    public int PassingScore { get; }
    public int MaxConversationLength { get; }

    public static AppSettings Load(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var databasePath = configuration["DatabasePath"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "prompt-share.db");
        var passingScore = configuration.GetValue("PassingScore", 75);
        var maxConversationLength = configuration.GetValue("MaxConversationLength", 12000);
        return new AppSettings(databasePath, passingScore, maxConversationLength);
    }
}

record CaptchaResponse(string Question, string Token);
record LoginRequest(string? Username, string? Password, string? CaptchaAnswer, string? CaptchaToken);
record RegisterRequest(string? Username, string? Password, string? CaptchaAnswer, string? CaptchaToken);
record SubmissionRequest(string Title, string? Author, string? Tags, string Conversation);
record ApiMessage(string Message);
record RegisterResult(string Message, string Username, string Role);
record UserAccount(int Id, string Username, string PasswordHash, string PasswordSalt, string Role);
record ScoreBreakdown(string Name, int Points, int MaxPoints, string Note);
record ScoreResult(int Score, string Feedback, IReadOnlyList<ScoreBreakdown> Breakdown);
record ScorePreview(int Score, string Feedback, IReadOnlyList<ScoreBreakdown> Breakdown);
record SubmissionResult(bool Saved, int Score, string Feedback, IReadOnlyList<ScoreBreakdown> Breakdown, string Message);
record SubmissionDto(int Id, string Title, string Author, string Tags, string Conversation, int Score, string Feedback, string CreatedAt, int ReportCount);
record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize);
record StatsDto(int Total, double AverageScore, int HighestScore, int ReportCount, int PassingScore);
