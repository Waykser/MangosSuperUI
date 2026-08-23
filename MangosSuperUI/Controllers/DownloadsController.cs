using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;
using System.Text;
using System.IO.Compression;

namespace MangosSuperUI.Controllers;

public class DownloadsController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly IWebHostEnvironment _env;
    private readonly DbcService _dbc;

    private const int CUSTOM_RANGE_START = 900000;

    public DownloadsController(ConnectionFactory db, IWebHostEnvironment env, DbcService dbc)
    {
        _db = db;
        _env = env;
        _dbc = dbc;
    }

    public async Task<IActionResult> Index()
    {
        // Regenerate Catalog.lua into the Placer addon folder on every page visit
        await RefreshPlacerCatalog();
        // Regenerate the BotBar spell table (names/icons/ground flags the 1.12
        // client cannot look up for itself)
        await RefreshBotBarSpellData();
        return View();
    }

    // ===================== ADDON LIST =====================

    /// <summary>
    /// GET /Downloads/AddonList — Returns metadata about all addons in wwwroot/addons/.
    /// Each subfolder is an addon. If a .zip with the same name exists alongside it, it's downloadable.
    /// </summary>
    [HttpGet]
    public IActionResult AddonList()
    {
        var addonsRoot = Path.Combine(_env.WebRootPath, "addons");
        if (!Directory.Exists(addonsRoot))
            return Json(new { addons = Array.Empty<object>() });

        var addons = new List<object>();

        foreach (var dir in Directory.GetDirectories(addonsRoot))
        {
            var folderName = Path.GetFileName(dir);

            // Read addon info from the .toc file
            string title = folderName;
            string notes = "";
            string version = "";
            string author = "";

            var tocPath = Path.Combine(dir, folderName + ".toc");
            if (System.IO.File.Exists(tocPath))
            {
                foreach (var line in System.IO.File.ReadLines(tocPath))
                {
                    if (line.StartsWith("## Title:")) title = line.Substring(9).Trim();
                    else if (line.StartsWith("## Notes:")) notes = line.Substring(9).Trim();
                    else if (line.StartsWith("## Version:")) version = line.Substring(11).Trim();
                    else if (line.StartsWith("## Author:")) author = line.Substring(10).Trim();
                }
            }

            var luaFiles = Directory.GetFiles(dir, "*.lua").Length;

            // Read README.md if present
            string readme = "";
            var readmePath = Path.Combine(dir, "README.md");
            if (System.IO.File.Exists(readmePath))
                readme = System.IO.File.ReadAllText(readmePath);

            addons.Add(new
            {
                folder = folderName,
                title,
                notes,
                version,
                author,
                luaFiles,
                readme
            });
        }

        return Json(new { addons });
    }

    // ===================== DOWNLOAD ADDON ZIP =====================

    /// <summary>
    /// GET /Downloads/Addon?name=MangosSuperUI_Placer — Generates a ZIP on-the-fly from wwwroot/addons/{name}/.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Addon(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest("Addon name required");

        // Sanitize — prevent path traversal
        name = Path.GetFileName(name);

        var addonDir = Path.Combine(_env.WebRootPath, "addons", name);
        if (!Directory.Exists(addonDir))
            return NotFound($"Addon folder '{name}' not found");

        // The BotBar ships a generated spell table. Refresh it here as well as on
        // the Index page, so a direct download link never hands out the checked-in
        // placeholder — an addon whose spells all read "Spell 133" looks broken.
        if (string.Equals(name, "MSUI_BotBar", StringComparison.OrdinalIgnoreCase))
            await RefreshBotBarSpellData();

        using var memoryStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(addonDir))
            {
                var entryName = name + "/" + Path.GetFileName(file);
                archive.CreateEntryFromFile(file, entryName, System.IO.Compression.CompressionLevel.Optimal);
            }
        }

        memoryStream.Position = 0;
        return File(memoryStream.ToArray(), "application/zip", name + ".zip");
    }

    // ===================== BOTBAR SPELL DATA =====================

    // Vanilla 1.12 Lua has no way to resolve an arbitrary spell ID: GetSpellName
    // only works on indices into the PLAYER's own spellbook, so an addon holding a
    // bot's spell IDs cannot render a name, let alone an icon. The server therefore
    // sends IDs only and the addon looks the rest up in this generated table --
    // exactly how MSUI_LootBrowserData.lua and the Placer's Catalog.lua already work.
    //
    // The `g` flag is the important one: it marks a spell as ground-targeted, which
    // is what tells the addon to offer the anchor modifiers (target / self / cluster)
    // on that button instead of casting straight away.
    private async Task RefreshBotBarSpellData()
    {
        var addonDir = Path.Combine(_env.WebRootPath, "addons", "MSUI_BotBar");
        if (!Directory.Exists(addonDir)) return;

        try
        {
            var lua = await BuildBotSpellsLua();
            await System.IO.File.WriteAllTextAsync(
                Path.Combine(addonDir, "MSUI_BotSpellsData.lua"), lua, Encoding.UTF8);
        }
        catch
        {
            // Non-fatal -- the page still loads, and the addon degrades to showing
            // bare spell IDs rather than breaking outright.
        }
    }

    /// <summary>
    /// GET /Downloads/BotBarInfo -- regenerates the BotBar spell table and reports
    /// what went into it, so the Downloads page can show whether it is current.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> BotBarInfo()
    {
        var addonDir = Path.Combine(_env.WebRootPath, "addons", "MSUI_BotBar");
        var dataPath = Path.Combine(addonDir, "MSUI_BotSpellsData.lua");

        await RefreshBotBarSpellData();

        var exists = System.IO.File.Exists(dataPath);
        return Json(new
        {
            generated = exists,
            generatedAt = exists ? System.IO.File.GetLastWriteTimeUtc(dataPath) : (DateTime?)null,
            sizeBytes = exists ? new FileInfo(dataPath).Length : 0L,
            dbcLoaded = _dbc.IsLoaded,
            dbcError = _dbc.LoadError
        });
    }

    // -- BotBar spell table builder ----------------------------------

    private const uint SPELL_ATTR_PASSIVE = 0x00000040;
    private const uint SPELL_ATTR_DO_NOT_DISPLAY = 0x00000080;
    private const uint TARGET_FLAG_DEST_LOCATION = 0x00000040;

    private async Task<string> BuildBotSpellsLua()
    {
        using var conn = _db.Mangos();

        // One row per entry, at the highest build the server actually loads --
        // the same "max build" selection SpellMgr::LoadSpells uses, so the table
        // can never describe a spell variant the core does not have.
        const string sql = @"
            SELECT t1.entry, t1.name, t1.nameSubtext, t1.spellIconId, t1.targets, t1.attributes
            FROM spell_template t1
            WHERE t1.build = (SELECT MAX(t2.build) FROM spell_template t2 WHERE t2.entry = t1.entry)";

        var rows = await conn.QueryAsync<SpellRowDto>(sql);

        var sb = new StringBuilder();
        sb.AppendLine("--[[ MSUI_BotSpellsData.lua");
        sb.AppendLine();
        sb.AppendLine("     GENERATED FILE. Produced by MangosSuperUI: /Downloads");
        sb.AppendLine("     Do not hand-edit -- it is rewritten on every Downloads page visit.");
        sb.AppendLine();
        sb.AppendLine("     Why this file exists");
        sb.AppendLine("     --------------------");
        sb.AppendLine("     The server sends the addon a bot's spellbook as bare IDs. Vanilla 1.12");
        sb.AppendLine("     Lua cannot turn an arbitrary spell ID into a name or an icon, so the");
        sb.AppendLine("     lookup has to be shipped with the addon.");
        sb.AppendLine();
        sb.AppendLine("     MSUIBS_SPELLS[id] = { n = name, r = rank text, i = icon, g = ground-targeted }");
        sb.AppendLine();
        sb.AppendLine("     g = true means the spell needs a destination (Blizzard, Rain of Fire,");
        sb.AppendLine("     Volley, Hurricane, Flamestrike). The addon offers the anchor modifiers");
        sb.AppendLine("     on those buttons; every other spell casts on the resolved unit.");
        sb.AppendLine("]]");
        sb.AppendLine();
        sb.AppendLine("MSUIBS_SPELLS = {}");
        sb.AppendLine("local S = MSUIBS_SPELLS");
        sb.AppendLine();

        var written = 0;
        var ground = 0;

        foreach (var r in rows.OrderBy(r => r.entry))
        {
            // Same filter the server applies when it dumps a bot's book. Keeping the
            // two in step means the addon never receives an ID it cannot render.
            if ((r.attributes & SPELL_ATTR_PASSIVE) != 0) continue;
            if ((r.attributes & SPELL_ATTR_DO_NOT_DISPLAY) != 0) continue;

            var name = r.name;
            if (string.IsNullOrWhiteSpace(name))
            {
                // spell_template.name is empty for a fair number of rows; Spell.dbc
                // carries the real label. Same fallback ItemsController uses.
                if (_dbc.AllSpellEntries.TryGetValue((uint)r.entry, out var dbcEntry))
                    name = dbcEntry.Name;
            }
            if (string.IsNullOrWhiteSpace(name)) continue;

            var icon = "INV_Misc_QuestionMark";
            if (_dbc.SpellIcons.TryGetValue(r.spellIconId, out var iconName)
                && !string.IsNullOrWhiteSpace(iconName))
                icon = iconName;

            var isGround = (r.targets & TARGET_FLAG_DEST_LOCATION) != 0;
            if (isGround) ground++;

            sb.Append("S[").Append(r.entry).Append("]={n=\"").Append(LuaEscape(name)).Append('"');
            if (!string.IsNullOrWhiteSpace(r.nameSubtext))
                sb.Append(",r=\"").Append(LuaEscape(r.nameSubtext)).Append('"');
            sb.Append(",i=\"").Append(LuaEscape(icon)).Append('"');
            if (isGround)
                sb.Append(",g=1");
            sb.AppendLine("}");
            written++;
        }

        sb.AppendLine();
        sb.AppendLine($"-- {written} spells, {ground} ground-targeted");
        return sb.ToString();
    }

    private sealed class SpellRowDto
    {
        public int entry { get; set; }
        public string? name { get; set; }
        public string? nameSubtext { get; set; }
        public uint spellIconId { get; set; }
        public uint targets { get; set; }
        public uint attributes { get; set; }
    }

    // ===================== CATALOG REFRESH =====================

    /// <summary>
    /// Writes a fresh Catalog.lua into wwwroot/addons/MangosSuperUI_Placer/ from the DB.
    /// Called automatically when the Downloads page loads.
    /// </summary>
    private async Task RefreshPlacerCatalog()
    {
        var addonDir = Path.Combine(_env.WebRootPath, "addons", "MangosSuperUI_Placer");
        if (!Directory.Exists(addonDir)) return;

        try
        {
            var catalogLua = await BuildCatalogLua();
            var catalogPath = Path.Combine(addonDir, "Catalog.lua");
            await System.IO.File.WriteAllTextAsync(catalogPath, catalogLua, Encoding.UTF8);
        }
        catch
        {
            // Non-fatal — page still loads even if catalog write fails
        }
    }

    /// <summary>
    /// GET /Downloads/PlacerInfo — Returns metadata about the current Placer catalog.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> PlacerInfo()
    {
        using var conn = _db.Mangos();

        var objectCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM gameobject_template WHERE entry >= @Start AND patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)",
            new { Start = CUSTOM_RANGE_START });

        var spawnCount = 0;
        if (objectCount > 0)
        {
            spawnCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM gameobject WHERE id >= @Start",
                new { Start = CUSTOM_RANGE_START });
        }

        var typeCounts = await conn.QueryAsync<dynamic>(
            @"SELECT type, COUNT(*) AS cnt
              FROM gameobject_template
              WHERE entry >= @Start
                AND patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)
              GROUP BY type ORDER BY cnt DESC",
            new { Start = CUSTOM_RANGE_START });

        return Json(new
        {
            objectCount,
            spawnCount,
            typeCounts
        });
    }

    // ── Catalog Builder ──────────────────────────────────────────────

    private async Task<string> BuildCatalogLua()
    {
        using var conn = _db.Mangos();

        var sql = @"
            SELECT entry, type, displayId, name, data0, data1, data2, data3,
                   data4, data5, data6, data7, data8, data9, data10
            FROM gameobject_template
            WHERE entry >= @CustomStart
              AND patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)
            ORDER BY entry";

        var objects = (await conn.QueryAsync<dynamic>(sql, new { CustomStart = CUSTOM_RANGE_START })).ToList();

        // Resolve spell names
        var spellIds = new HashSet<int>();
        foreach (var obj in objects)
        {
            int type = (int)(obj.type ?? 0);
            int d0 = (int)(obj.data0 ?? 0);
            int d1 = (int)(obj.data1 ?? 0);
            int d3 = (int)(obj.data3 ?? 0);
            if (type == 22 && d0 > 0) spellIds.Add(d0);
            if (type == 6 && d3 > 0) spellIds.Add(d3);
            if (type == 10 && (int)(obj.data10 ?? 0) > 0) spellIds.Add((int)obj.data10);
            if (type == 18 && d1 > 0) spellIds.Add(d1);
            if (type == 30 && (int)(obj.data2 ?? 0) > 0) spellIds.Add((int)obj.data2);
        }

        var spellNames = new Dictionary<int, string>();
        if (spellIds.Count > 0)
        {
            var spells = await conn.QueryAsync<dynamic>(
                "SELECT entry, name FROM spell_template WHERE entry IN @Ids AND build = (SELECT MAX(build) FROM spell_template st2 WHERE st2.entry = spell_template.entry)",
                new { Ids = spellIds.ToArray() });
            foreach (var sp in spells)
                spellNames[(int)sp.entry] = (string)sp.name;
        }

        var spawnCounts = new Dictionary<int, int>();
        if (objects.Count > 0)
        {
            var entries = objects.Select(o => (int)o.entry).ToArray();
            var counts = await conn.QueryAsync<dynamic>(
                "SELECT id, COUNT(*) AS cnt FROM gameobject WHERE id IN @Ids GROUP BY id",
                new { Ids = entries });
            foreach (var c in counts)
                spawnCounts[(int)c.id] = (int)c.cnt;
        }

        var sb = new StringBuilder();
        sb.AppendLine("-- MangosSuperUI_Placer Catalog");
        sb.AppendLine($"-- Auto-generated by MangosSuperUI on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"-- {objects.Count} custom game object(s)");
        sb.AppendLine();
        sb.AppendLine("MSUI_CATALOG = {");

        foreach (var obj in objects)
        {
            int entry = (int)obj.entry;
            int type = (int)(obj.type ?? 0);
            string name = LuaEscape((string)(obj.name ?? "Unknown"));
            int displayId = (int)(obj.displayId ?? 0);
            string desc = BuildCatalogDesc(obj, type, spellNames);
            int spawns = spawnCounts.ContainsKey(entry) ? spawnCounts[entry] : 0;

            sb.AppendLine($"    [{entry}] = {{ name = \"{name}\", type = {type}, displayId = {displayId}, spawns = {spawns}, desc = \"{LuaEscape(desc)}\" }},");
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("MSUI_TYPE_NAMES = {");
        sb.AppendLine("    [0] = \"Door\", [1] = \"Button\", [2] = \"Quest Giver\", [3] = \"Chest\",");
        sb.AppendLine("    [5] = \"Generic\", [6] = \"Trap\", [7] = \"Chair\", [8] = \"Spell Focus\",");
        sb.AppendLine("    [9] = \"Text\", [10] = \"Goober\", [11] = \"Transport\", [13] = \"Camera\",");
        sb.AppendLine("    [15] = \"MO Transport\", [17] = \"Fishing Node\", [18] = \"Ritual\",");
        sb.AppendLine("    [19] = \"Mailbox\", [20] = \"Auction House\", [22] = \"Spell Caster\",");
        sb.AppendLine("    [23] = \"Meeting Stone\", [24] = \"Flag Stand\", [25] = \"Fishing Hole\",");
        sb.AppendLine("    [26] = \"Flag Drop\", [29] = \"Capture Point\", [30] = \"Aura Generator\",");
        sb.AppendLine("    [31] = \"Dungeon Difficulty\",");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string BuildCatalogDesc(dynamic obj, int type, Dictionary<int, string> spellNames)
    {
        int d0 = (int)(obj.data0 ?? 0);
        int d1 = (int)(obj.data1 ?? 0);
        int d3 = (int)(obj.data3 ?? 0);

        return type switch
        {
            22 => d0 > 0
                ? $"Casts {(spellNames.ContainsKey(d0) ? spellNames[d0] : $"Spell #{d0}")}"
                  + (d1 == -1 ? " (unlimited)" : d1 <= 1 ? " (single use)" : $" ({d1} charges)")
                : "Spell Caster",
            6 => d3 > 0 ? $"Trap: {(spellNames.ContainsKey(d3) ? spellNames[d3] : $"Spell #{d3}")}" : "Trap",
            3 => d1 > 0 ? $"Chest (loot #{d1})" : "Chest (no loot)",
            10 => d1 > 0 ? $"Goober (quest #{d1})" : "Clickable object",
            30 => (int)(obj.data2 ?? 0) > 0 && spellNames.ContainsKey((int)obj.data2)
                ? $"Aura: {spellNames[(int)obj.data2]}" : "Aura Generator",
            5 => "Decoration",
            0 => "Door",
            1 => "Button",
            2 => "Quest Giver",
            7 => "Chair",
            8 => "Spell Focus",
            9 => "Text",
            _ => $"Type {type}"
        };
    }

    private static string LuaEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }

}