using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("espn", client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 NBA-Player-Lab/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});

var app = builder.Build();

var dataDirectory = Path.Combine(app.Environment.ContentRootPath, "App_Data");
var dataFile = Path.Combine(dataDirectory, "nba-players.json");
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};

var playerApi = app.MapGroup("/api/players");

playerApi.MapGet("/", () => TypedResults.Ok(ReadPlayers()));

playerApi.MapPut("/", async (IReadOnlyList<PlayerProfile> players) =>
{
    if (players.Count == 0)
    {
        return Results.BadRequest(new { message = "At least one player is required." });
    }

    var normalized = players.Select(NormalizePlayer).ToList();
    await SavePlayers(normalized);

    return Results.Ok(normalized);
});

playerApi.MapPost("/sync-nba", async (IHttpClientFactory httpClientFactory) =>
{
    try
    {
        var players = await ImportEspnPlayers(httpClientFactory.CreateClient("espn"));
        if (players.Count == 0)
        {
            return Results.Problem("ESPN returned no active NBA roster players.", statusCode: 502);
        }

        await SavePlayers(players);
        return Results.Ok(new SyncResult(players.Count, "ESPN NBA 2025-26 rosters and regular-season statistics"));
    }
    catch (Exception exception)
    {
        return Results.Problem($"NBA sync failed: {exception.Message}", statusCode: 502);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

List<PlayerProfile> ReadPlayers()
{
    if (!File.Exists(dataFile))
    {
        return SeedPlayers();
    }

    using var stream = File.OpenRead(dataFile);
    var players = JsonSerializer.Deserialize<List<PlayerProfile>>(stream, jsonOptions) ?? [];

    return players.Count == 0
        ? SeedPlayers()
        : players.Select(NormalizePlayer).ToList();
}

async Task SavePlayers(IReadOnlyList<PlayerProfile> players)
{
    Directory.CreateDirectory(dataDirectory);

    await using var stream = File.Create(dataFile);
    await JsonSerializer.SerializeAsync(stream, players, jsonOptions);
}

async Task<List<PlayerProfile>> ImportEspnPlayers(HttpClient httpClient)
{
    var stats = await LoadSeasonStats(httpClient);
    using var teamDocument = await GetJson(httpClient, "https://site.api.espn.com/apis/site/v2/sports/basketball/nba/teams");
    var teams = teamDocument.RootElement
        .GetProperty("sports")[0]
        .GetProperty("leagues")[0]
        .GetProperty("teams");

    var players = new Dictionary<string, PlayerProfile>(StringComparer.OrdinalIgnoreCase);

    foreach (var teamEntry in teams.EnumerateArray())
    {
        var team = teamEntry.GetProperty("team");
        var teamName = GetString(team, "displayName", GetString(team, "name", "NBA Team"));
        var teamAbbreviation = GetString(team, "abbreviation", "");
        var rosterKey = teamAbbreviation.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(rosterKey))
        {
            continue;
        }

        using var rosterDocument = await GetJson(
            httpClient,
            $"https://site.api.espn.com/apis/site/v2/sports/basketball/nba/teams/{rosterKey}/roster");

        if (!rosterDocument.RootElement.TryGetProperty("athletes", out var athletes))
        {
            continue;
        }

        foreach (var athlete in athletes.EnumerateArray())
        {
            if (GetString(athlete.GetProperty("status"), "type", "active") != "active")
            {
                continue;
            }

            var espnId = GetString(athlete, "id", "");
            if (string.IsNullOrWhiteSpace(espnId) || players.ContainsKey(espnId))
            {
                continue;
            }

            stats.TryGetValue(espnId, out var statLine);
            var positionCode = GetString(athlete.GetProperty("position"), "abbreviation", "");
            var position = TranslatePosition(positionCode);
            var heightInches = GetDouble(athlete, "height", DefaultHeightInches(position));
            var height = Clamp((int)Math.Round(heightInches * 2.54), 150, 240);
            var weight = Clamp((int)Math.Round(GetDouble(athlete, "weight", 215) * 0.45359237), 60, 180);
            var wingspan = Clamp(height + PositionReachBonus(position), 150, 260);
            var reach = Clamp((int)Math.Round(height * 1.31 + PositionReachBonus(position) * 1.5), 190, 330);
            var vertical = EstimateVertical(position, statLine);
            var attributes = BuildAttributes(position, height, weight, statLine, GetInt(athlete.GetProperty("experience"), "years", 0));

            players[espnId] = NormalizePlayer(new PlayerProfile(
                $"espn-{espnId}",
                GetString(athlete, "displayName", "Unknown Player"),
                position,
                BuildStyle(position, statLine),
                GetString(athlete.GetProperty("headshot"), "href", ""),
                teamName,
                teamAbbreviation,
                GetString(athlete, "jersey", ""),
                espnId,
                height,
                weight,
                wingspan,
                reach,
                vertical,
                BuildNotes(teamAbbreviation, position, statLine),
                statLine,
                attributes));
        }
    }

    return players.Values
        .OrderBy(player => player.TeamAbbreviation)
        .ThenBy(player => PositionOrder(player.Position))
        .ThenBy(player => player.Name)
        .ToList();
}

async Task<Dictionary<string, PlayerStats>> LoadSeasonStats(HttpClient httpClient)
{
    const string url = "https://site.web.api.espn.com/apis/common/v3/sports/basketball/nba/statistics/byathlete?region=us&lang=en&contentorigin=espn&isqualified=false&limit=1000&season=2026&seasontype=2&sort=offensive.avgPoints:desc";
    using var document = await GetJson(httpClient, url);
    var result = new Dictionary<string, PlayerStats>(StringComparer.OrdinalIgnoreCase);

    if (!document.RootElement.TryGetProperty("athletes", out var athletes))
    {
        return result;
    }

    foreach (var entry in athletes.EnumerateArray())
    {
        if (!entry.TryGetProperty("athlete", out var athlete))
        {
            continue;
        }

        var id = GetString(athlete, "id", "");
        if (string.IsNullOrWhiteSpace(id) || !entry.TryGetProperty("categories", out var categories))
        {
            continue;
        }

        var general = FindCategory(categories, "general");
        var offensive = FindCategory(categories, "offensive");
        var defensive = FindCategory(categories, "defensive");

        result[id] = new PlayerStats(
            Points: GetValue(offensive, 0),
            Rebounds: GetValue(general, 11),
            Assists: GetValue(offensive, 10),
            Steals: GetValue(defensive, 0),
            Blocks: GetValue(defensive, 1),
            ThreeMade: GetValue(offensive, 4),
            ThreePct: GetValue(offensive, 6),
            Minutes: GetValue(general, 1),
            Games: GetValue(general, 0));
    }

    return result;
}

async Task<JsonDocument> GetJson(HttpClient httpClient, string url)
{
    await using var stream = await httpClient.GetStreamAsync(url);
    return await JsonDocument.ParseAsync(stream);
}

PlayerProfile NormalizePlayer(PlayerProfile player)
{
    var id = Required(player.Id, Guid.NewGuid().ToString("N"));
    var attributes = player.Attributes ?? new PlayerAttributes(70, 70, 70, 70, 70, 70);
    var stats = player.Stats ?? new PlayerStats(0, 0, 0, 0, 0, 0, 0, 0, 0);

    return player with
    {
        Id = id,
        Name = Required(player.Name, "未命名球员"),
        Position = Required(player.Position, "后卫"),
        Style = Required(player.Style, "待评估"),
        PhotoUrl = player.PhotoUrl?.Trim() ?? "",
        TeamName = Required(player.TeamName, "自由球员"),
        TeamAbbreviation = Required(player.TeamAbbreviation, "FA"),
        Jersey = player.Jersey?.Trim() ?? "",
        EspnId = player.EspnId?.Trim() ?? "",
        Height = Clamp(player.Height, 150, 240),
        Weight = Clamp(player.Weight, 60, 180),
        Wingspan = Clamp(player.Wingspan, 150, 260),
        Reach = Clamp(player.Reach, 190, 330),
        Vertical = Clamp(player.Vertical, 30, 130),
        Notes = player.Notes?.Trim() ?? "",
        Stats = stats,
        Attributes = new PlayerAttributes(
            Clamp(attributes.Dribble, 0, 100),
            Clamp(attributes.Shooting, 0, 100),
            Clamp(attributes.Iq, 0, 100),
            Clamp(attributes.Personality, 0, 100),
            Clamp(attributes.Body, 0, 100),
            Clamp(attributes.Mental, 0, 100))
    };
}

static JsonElement? FindCategory(JsonElement categories, string name)
{
    foreach (var category in categories.EnumerateArray())
    {
        if (GetString(category, "name", "") == name)
        {
            return category;
        }
    }

    return null;
}

static double GetValue(JsonElement? category, int index)
{
    if (category is null ||
        !category.Value.TryGetProperty("values", out var values) ||
        values.ValueKind != JsonValueKind.Array ||
        values.GetArrayLength() <= index)
    {
        return 0;
    }

    return values[index].GetDouble();
}

static PlayerAttributes BuildAttributes(string position, int height, int weight, PlayerStats? stats, int experience)
{
    stats ??= new PlayerStats(0, 0, 0, 0, 0, 0, 0, 0, 0);
    var production = stats.Points + stats.Rebounds * 1.15 + stats.Assists * 1.35 + stats.Steals * 2 + stats.Blocks * 2;
    var role = ClampDouble(54 + stats.Minutes * 1.05 + production * 0.56, 48, 99);
    var sizeScore = position switch
    {
        "中锋" => (height - 198) * 0.9 + (weight - 95) * 0.35,
        "大前锋" => (height - 195) * 0.8 + (weight - 90) * 0.3,
        "小前锋" => (height - 190) * 0.75 + (weight - 84) * 0.28,
        _ => (height - 182) * 0.65 + (weight - 78) * 0.2
    };

    var dribbleBase = position is "控球后卫" or "得分后卫" ? 64 : position == "小前锋" ? 59 : 52;
    var shootBase = position is "控球后卫" or "得分后卫" ? 60 : position == "中锋" ? 50 : 56;

    return new PlayerAttributes(
        Dribble: Score(dribbleBase + stats.Assists * 3.8 + stats.Points * 0.7 + role * 0.12),
        Shooting: Score(shootBase + stats.Points * 0.95 + stats.ThreeMade * 5.8 + stats.ThreePct * 0.16),
        Iq: Score(58 + stats.Assists * 4.1 + stats.Minutes * 0.72 + experience * 1.6),
        Personality: Score(68 + stats.Games * 0.16 + stats.Minutes * 0.35 + experience * 1.2),
        Body: Score(56 + sizeScore + stats.Rebounds * 2.1 + stats.Blocks * 4.2),
        Mental: Score(62 + stats.Games * 0.18 + stats.Minutes * 0.48 + production * 0.42));
}

static string BuildStyle(string position, PlayerStats? stats)
{
    stats ??= new PlayerStats(0, 0, 0, 0, 0, 0, 0, 0, 0);

    if (stats.Points >= 25)
    {
        return "核心得分 / 持球攻坚";
    }

    if (stats.Assists >= 6)
    {
        return "组织发动 / 挡拆处理";
    }

    if (stats.Rebounds >= 8 || stats.Blocks >= 1.2)
    {
        return "篮板护框 / 内线终结";
    }

    if (stats.ThreeMade >= 2)
    {
        return "外线投射 / 空间牵制";
    }

    return position switch
    {
        "控球后卫" => "控运组织 / 外线轮转",
        "得分后卫" => "侧翼得分 / 防守轮转",
        "小前锋" => "锋线摇摆 / 攻防转换",
        "大前锋" => "前场终结 / 篮板协防",
        "中锋" => "禁区终结 / 掩护护框",
        _ => "轮换角色 / 待评估"
    };
}

static string BuildNotes(string team, string position, PlayerStats? stats)
{
    stats ??= new PlayerStats(0, 0, 0, 0, 0, 0, 0, 0, 0);

    if (stats.Games == 0)
    {
        return $"{team} {position}，暂无 2025-26 常规赛统计，评分主要来自身材、位置和角色模板。";
    }

    return $"{team} {position}，2025-26 常规赛场均 {stats.Points:0.0} 分、{stats.Rebounds:0.0} 篮板、{stats.Assists:0.0} 助攻，六边形评分由表现、位置和体测估算生成。";
}

static int EstimateVertical(string position, PlayerStats? stats)
{
    stats ??= new PlayerStats(0, 0, 0, 0, 0, 0, 0, 0, 0);
    var baseValue = position switch
    {
        "控球后卫" => 86,
        "得分后卫" => 89,
        "小前锋" => 88,
        "大前锋" => 83,
        "中锋" => 78,
        _ => 82
    };

    return Clamp((int)Math.Round(baseValue + stats.Points * 0.18 + stats.Blocks * 2.2 + stats.Steals * 1.2), 30, 130);
}

static string TranslatePosition(string abbreviation) => abbreviation.ToUpperInvariant() switch
{
    "PG" => "控球后卫",
    "SG" => "得分后卫",
    "G" => "后卫",
    "SF" => "小前锋",
    "PF" => "大前锋",
    "F" => "前锋",
    "C" => "中锋",
    _ => "未标注"
};

static int PositionReachBonus(string position) => position switch
{
    "控球后卫" or "后卫" => 5,
    "得分后卫" => 6,
    "小前锋" or "前锋" => 8,
    "大前锋" => 10,
    "中锋" => 12,
    _ => 7
};

static int PositionOrder(string position) => position switch
{
    "控球后卫" => 1,
    "得分后卫" => 2,
    "后卫" => 3,
    "小前锋" => 4,
    "前锋" => 5,
    "大前锋" => 6,
    "中锋" => 7,
    _ => 8
};

static double DefaultHeightInches(string position) => position switch
{
    "控球后卫" or "后卫" => 75,
    "得分后卫" => 77,
    "小前锋" or "前锋" => 80,
    "大前锋" => 82,
    "中锋" => 84,
    _ => 79
};

static int Score(double value) => Clamp((int)Math.Round(value), 40, 99);

static double ClampDouble(double value, double min, double max) => Math.Min(Math.Max(value, min), max);

static string Required(string? value, string fallback) =>
    string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);

static string GetString(JsonElement element, string property, string fallback)
{
    return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? fallback
        : fallback;
}

static double GetDouble(JsonElement element, string property, double fallback)
{
    return element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number)
        ? number
        : fallback;
}

static int GetInt(JsonElement element, string property, int fallback)
{
    return element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
        ? number
        : fallback;
}

static List<PlayerProfile> SeedPlayers() =>
[
    new(
        "espn-3975",
        "Stephen Curry",
        "控球后卫",
        "外线投射 / 空间牵制",
        "assets/headshots/stephen-curry.png",
        "Golden State Warriors",
        "GS",
        "30",
        "3975",
        188,
        84,
        192,
        244,
        91,
        "示例数据。点击“同步NBA”可导入当前赛季全联盟名单。",
        new PlayerStats(24.5, 4.4, 6.0, 1.1, 0.4, 4.1, 40.0, 32.0, 60),
        new PlayerAttributes(93, 99, 96, 90, 74, 95)),
    new(
        "espn-1966",
        "LeBron James",
        "小前锋",
        "持球组织 / 终结压迫",
        "assets/headshots/lebron-james.png",
        "Los Angeles Lakers",
        "LAL",
        "23",
        "1966",
        206,
        113,
        214,
        269,
        102,
        "示例数据。点击“同步NBA”可导入当前赛季全联盟名单。",
        new PlayerStats(24.4, 7.8, 8.2, 1.0, 0.6, 2.0, 37.6, 34.0, 60),
        new PlayerAttributes(88, 84, 98, 92, 97, 96)),
    new(
        "espn-3112335",
        "Nikola Jokic",
        "中锋",
        "高位策应 / 低位支点",
        "assets/headshots/nikola-jokic.png",
        "Denver Nuggets",
        "DEN",
        "15",
        "3112335",
        211,
        129,
        221,
        282,
        71,
        "示例数据。点击“同步NBA”可导入当前赛季全联盟名单。",
        new PlayerStats(29.6, 12.7, 10.2, 1.8, 0.7, 2.0, 41.7, 36.0, 65),
        new PlayerAttributes(79, 89, 99, 88, 91, 94)),
    new(
        "espn-4594268",
        "Anthony Edwards",
        "得分后卫",
        "爆发突破 / 强投终结",
        "assets/headshots/anthony-edwards.png",
        "Minnesota Timberwolves",
        "MIN",
        "5",
        "4594268",
        193,
        102,
        206,
        260,
        105,
        "示例数据。点击“同步NBA”可导入当前赛季全联盟名单。",
        new PlayerStats(27.6, 5.7, 4.5, 1.2, 0.6, 3.4, 39.5, 36.3, 64),
        new PlayerAttributes(90, 87, 84, 93, 94, 92))
];

public sealed record PlayerProfile(
    string Id,
    string Name,
    string Position,
    string Style,
    string PhotoUrl,
    string TeamName,
    string TeamAbbreviation,
    string Jersey,
    string EspnId,
    int Height,
    int Weight,
    int Wingspan,
    int Reach,
    int Vertical,
    string Notes,
    PlayerStats? Stats,
    PlayerAttributes? Attributes);

public sealed record PlayerStats(
    double Points,
    double Rebounds,
    double Assists,
    double Steals,
    double Blocks,
    double ThreeMade,
    double ThreePct,
    double Minutes,
    double Games);

public sealed record PlayerAttributes(
    int Dribble,
    int Shooting,
    int Iq,
    int Personality,
    int Body,
    int Mental);

public sealed record SyncResult(int Count, string Source);
