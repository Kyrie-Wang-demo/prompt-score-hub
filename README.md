# Prompt Score Hub

Prompt Score Hub is a small ASP.NET Core web app for collecting, scoring, and sharing useful conversations with large language models.

## Features

- User registration and login
- First registered user automatically becomes admin
- Registered users can score and submit conversations
- Admin users can delete published submissions
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

The first registered account becomes the admin account. Later registered accounts are regular users.

## Account Roles

- Visitors can browse, search, filter, and report public submissions.
- Registered users can preview scores and publish high-scoring conversations.
- Admin users can do everything regular users can do, plus delete submissions.

Passwords are stored with PBKDF2-SHA256 hashes and per-user salts.

## Configuration

The following configuration keys are supported:

- `DatabasePath`: SQLite database path. Defaults to `ConsoleApp7/App_Data/prompt-share.db`.
- `PassingScore`: Minimum score for public sharing. Defaults to `75`.
- `MaxConversationLength`: Maximum accepted conversation length. Defaults to `12000`.

## Notes

This project uses a local heuristic scorer. It does not call an external AI API by default, which keeps the app simple and private. You can replace `ScoreConversation` in `Program.cs` with a model-backed scorer later.

## License

MIT
