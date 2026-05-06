# Prompt Score Hub

Prompt Score Hub is a small ASP.NET Core web app for collecting, scoring, and sharing useful conversations with large language models.

## Features

- Password-protected admin submission area
- Math captcha on login
- Cookie authentication with secure flags
- Request rate limiting for login and submission endpoints
- Security headers and a strict content security policy
- Local scoring rubric with score breakdown
- SQLite storage
- Public shared library with search, tag filtering, pagination, reporting, and statistics
- Duplicate conversation detection by content hash
- Admin deletion for published submissions

## Requirements

- .NET 10 SDK

## Run Locally

```powershell
dotnet restore .\ConsoleApp7\ConsoleApp7.csproj
dotnet run --project .\ConsoleApp7\ConsoleApp7.csproj
```

Open the URL printed by `dotnet run`.

In development, if no password is configured, the temporary login password is:

```text
change-me-now
```

## Security Configuration

For real deployments, do not rely on the development password. Set one of these values:

```powershell
$env:PROMPT_SHARE_PASSWORD="your-long-random-password"
dotnet run --project .\ConsoleApp7\ConsoleApp7.csproj
```

For better secret handling, store a SHA-256 password hash instead:

```powershell
$bytes = [System.Text.Encoding]::UTF8.GetBytes("your-long-random-password")
$hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLower()
$env:PROMPT_SHARE_PASSWORD_HASH=$hash
```

Production mode requires `PROMPT_SHARE_PASSWORD_HASH`, `PROMPT_SHARE_PASSWORD`, `AppPasswordHash`, or `AppPassword`.

## Configuration

The following configuration keys are supported:

- `DatabasePath`: SQLite database path. Defaults to `ConsoleApp7/App_Data/prompt-share.db`.
- `PassingScore`: Minimum score for public sharing. Defaults to `75`.
- `MaxConversationLength`: Maximum accepted conversation length. Defaults to `12000`.
- `AppPassword`: Plaintext password, useful only for local development.
- `AppPasswordHash`: SHA-256 hash of the password.

## Notes

This project uses a local heuristic scorer. It does not call an external AI API by default, which keeps the app simple and private. You can replace `ScoreConversation` in `Program.cs` with a model-backed scorer later.

## License

MIT
