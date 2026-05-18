import fs from "node:fs";
import path from "node:path";

const root = path.resolve("ConsoleApp7/wwwroot");
const dataDir = path.join(root, "data");
fs.mkdirSync(dataDir, { recursive: true });

async function getJson(url) {
  const response = await fetch(url, {
    headers: {
      "User-Agent": "Mozilla/5.0 NBA-Player-Lab/1.0",
      Accept: "application/json"
    }
  });

  if (!response.ok) {
    throw new Error(`${response.status} ${url}`);
  }

  return response.json();
}

const clamp = (value, min, max) => Math.min(Math.max(value, min), max);
const score = (value) => Math.round(clamp(value, 40, 99));
const emptyStats = () => ({
  points: 0,
  rebounds: 0,
  assists: 0,
  steals: 0,
  blocks: 0,
  threeMade: 0,
  threePct: 0,
  minutes: 0,
  games: 0
});

function positionZh(abbreviation = "") {
  return {
    PG: "控球后卫",
    SG: "得分后卫",
    G: "后卫",
    SF: "小前锋",
    PF: "大前锋",
    F: "前锋",
    C: "中锋"
  }[String(abbreviation).toUpperCase()] || "未标注";
}

function reachBonus(position) {
  return {
    控球后卫: 5,
    后卫: 5,
    得分后卫: 6,
    小前锋: 8,
    前锋: 8,
    大前锋: 10,
    中锋: 12
  }[position] ?? 7;
}

function defaultHeight(position) {
  return {
    控球后卫: 75,
    后卫: 75,
    得分后卫: 77,
    小前锋: 80,
    前锋: 80,
    大前锋: 82,
    中锋: 84
  }[position] ?? 79;
}

function category(categories, name) {
  return (categories || []).find((item) => item.name === name);
}

function valueOf(categoryItem, index) {
  return Number(categoryItem?.values?.[index] ?? 0);
}

function buildStyle(position, stats) {
  if (stats.points >= 25) return "核心得分 / 持球攻坚";
  if (stats.assists >= 6) return "组织发动 / 挡拆处理";
  if (stats.rebounds >= 8 || stats.blocks >= 1.2) return "篮板护框 / 内线终结";
  if (stats.threeMade >= 2) return "外线投射 / 空间牵制";

  return {
    控球后卫: "控运组织 / 外线轮转",
    得分后卫: "侧翼得分 / 防守轮转",
    小前锋: "锋线摇摆 / 攻防转换",
    大前锋: "前场终结 / 篮板协防",
    中锋: "禁区终结 / 掩护护框"
  }[position] || "轮换角色 / 待评估";
}

function buildAttributes(position, height, weight, stats, experience) {
  const production =
    stats.points + stats.rebounds * 1.15 + stats.assists * 1.35 + stats.steals * 2 + stats.blocks * 2;
  let sizeScore;

  if (position === "中锋") sizeScore = (height - 198) * 0.9 + (weight - 95) * 0.35;
  else if (position === "大前锋") sizeScore = (height - 195) * 0.8 + (weight - 90) * 0.3;
  else if (position === "小前锋") sizeScore = (height - 190) * 0.75 + (weight - 84) * 0.28;
  else sizeScore = (height - 182) * 0.65 + (weight - 78) * 0.2;

  const dribbleBase = ["控球后卫", "得分后卫"].includes(position) ? 64 : position === "小前锋" ? 59 : 52;
  const shootBase = ["控球后卫", "得分后卫"].includes(position) ? 60 : position === "中锋" ? 50 : 56;

  return {
    dribble: score(dribbleBase + stats.assists * 3.8 + stats.points * 0.7 + stats.minutes * 0.12),
    shooting: score(shootBase + stats.points * 0.95 + stats.threeMade * 5.8 + stats.threePct * 0.16),
    iq: score(58 + stats.assists * 4.1 + stats.minutes * 0.72 + experience * 1.6),
    personality: score(68 + stats.games * 0.16 + stats.minutes * 0.35 + experience * 1.2),
    body: score(56 + sizeScore + stats.rebounds * 2.1 + stats.blocks * 4.2),
    mental: score(62 + stats.games * 0.18 + stats.minutes * 0.48 + production * 0.42)
  };
}

function estimateVertical(position, stats) {
  const base = {
    控球后卫: 86,
    得分后卫: 89,
    小前锋: 88,
    大前锋: 83,
    中锋: 78
  }[position] ?? 82;

  return Math.round(clamp(base + stats.points * 0.18 + stats.blocks * 2.2 + stats.steals * 1.2, 30, 130));
}

const statsUrl =
  "https://site.web.api.espn.com/apis/common/v3/sports/basketball/nba/statistics/byathlete?region=us&lang=en&contentorigin=espn&isqualified=false&limit=1000&season=2026&seasontype=2&sort=offensive.avgPoints:desc";
const statsJson = await getJson(statsUrl);
const statsMap = new Map();

for (const entry of statsJson.athletes || []) {
  const id = String(entry.athlete?.id || "");
  const general = category(entry.categories, "general");
  const offensive = category(entry.categories, "offensive");
  const defensive = category(entry.categories, "defensive");

  statsMap.set(id, {
    points: valueOf(offensive, 0),
    rebounds: valueOf(general, 11),
    assists: valueOf(offensive, 10),
    steals: valueOf(defensive, 0),
    blocks: valueOf(defensive, 1),
    threeMade: valueOf(offensive, 4),
    threePct: valueOf(offensive, 6),
    minutes: valueOf(general, 1),
    games: valueOf(general, 0)
  });
}

const teamsJson = await getJson("https://site.api.espn.com/apis/site/v2/sports/basketball/nba/teams");
const teams = teamsJson.sports?.[0]?.leagues?.[0]?.teams || [];
const players = new Map();

for (const teamEntry of teams) {
  const team = teamEntry.team;
  const teamAbbreviation = team?.abbreviation;
  if (!teamAbbreviation) continue;

  let roster;
  try {
    roster = await getJson(
      `https://site.api.espn.com/apis/site/v2/sports/basketball/nba/teams/${teamAbbreviation.toLowerCase()}/roster`
    );
  } catch {
    continue;
  }

  for (const athlete of roster.athletes || []) {
    const espnId = String(athlete.id || "");
    if (!espnId || players.has(espnId) || athlete.status?.type !== "active") continue;

    const position = positionZh(athlete.position?.abbreviation);
    const stats = statsMap.get(espnId) || emptyStats();
    const heightInches = Number(athlete.height || defaultHeight(position));
    const height = Math.round(clamp(heightInches * 2.54, 150, 240));
    const weight = Math.round(clamp(Number(athlete.weight || 215) * 0.45359237, 60, 180));
    const bonus = reachBonus(position);
    const experience = Number(athlete.experience?.years || 0);
    const notes =
      stats.games > 0
        ? `${teamAbbreviation} ${position}，2025-26 常规赛场均 ${stats.points.toFixed(1)} 分、${stats.rebounds.toFixed(1)} 篮板、${stats.assists.toFixed(1)} 助攻，评分由表现、位置和体测估算生成。`
        : `${teamAbbreviation} ${position}，暂无 2025-26 常规赛统计，评分主要来自身材、位置和角色模板。`;

    players.set(espnId, {
      id: `espn-${espnId}`,
      name: athlete.displayName || "Unknown Player",
      position,
      style: buildStyle(position, stats),
      photoUrl: athlete.headshot?.href || "",
      teamName: team.displayName || team.name || "NBA Team",
      teamAbbreviation,
      jersey: athlete.jersey ? String(athlete.jersey) : "",
      espnId,
      height,
      weight,
      wingspan: Math.round(clamp(height + bonus, 150, 260)),
      reach: Math.round(clamp(height * 1.31 + bonus * 1.5, 190, 330)),
      vertical: estimateVertical(position, stats),
      notes,
      stats,
      attributes: buildAttributes(position, height, weight, stats, experience)
    });
  }
}

const list = [...players.values()].sort((a, b) =>
  `${a.teamAbbreviation}-${a.position}-${a.name}`.localeCompare(`${b.teamAbbreviation}-${b.position}-${b.name}`)
);

fs.writeFileSync(path.join(dataDir, "nba-players-data.js"), `window.NBA_PLAYERS_DATA = ${JSON.stringify(list)};\n`, "utf8");
fs.writeFileSync(path.join(dataDir, "nba-players.json"), JSON.stringify(list, null, 2), "utf8");
console.log(`Generated ${list.length} players`);
