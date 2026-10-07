using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GD = Godot.GD;
using OS = Godot.OS;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Modding;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2FunUploader;

/// <summary>
/// Uploads finished runs to sts2.fun. On startup and after each run ends it
/// collects every .run file in the game's history folders (normal and modded
/// profiles), asks the site which ones it lacks (/api/check-runs, by SHA-256),
/// and uploads those as a zip to /upload. The player's earliest solo run is
/// always included, because the site identifies a player by that file; it is
/// skipped server-side as a duplicate. Only .run files are read or sent.
/// </summary>
[ModInitializer("Initialize")]
public static class Uploader
{
    const string Tag = "[sts2fun]";
    const int MaxFilesPerZip = 500;           // server limit is 1000
    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    static readonly SemaphoreSlim Busy = new(1, 1);
    static string _userDir = "";
    static Config _cfg = new();
    static State _state = new();
    static string _stateDir = "";
    static LinkButton? _badge;
    static string _status = "";

    public static void Initialize()
    {
        try
        {
            _userDir = OS.GetUserDataDir();
            _stateDir = Path.Combine(_userDir, "sts2fun_stats");
            _cfg = Config.Load(_stateDir);
            _state = State.Load(_stateDir);
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("sts2fun-stats/0.1 (+https://sts2.fun)");
            new Harmony("sts2fun.stats").PatchAll(typeof(Uploader).Assembly);
            GD.Print($"{Tag} loaded; uploads {(_cfg.Enabled ? "ON" : "OFF")} -> {_cfg.Server}");
            if (_cfg.Enabled) Schedule(TimeSpan.FromSeconds(5), "startup");
        }
        catch (Exception e) { GD.PrintErr($"{Tag} init failed: {e.Message}"); }
    }

    /// Called after a run ends (game over, victory, or abandon); the .run file
    /// is written by then, a short delay gives the save time to land.
    public static void OnRunEnded()
    {
        RecordMods();
        if (_cfg.Enabled) Schedule(TimeSpan.FromSeconds(5), "run ended");
    }

    /// Note which mods were loaded when a run ended, so the site can leave out
    /// runs played with gameplay mods (the mod's own affects_gameplay flag,
    /// which the game also uses; it defaults to true when a mod omits it).
    static void RecordMods()
    {
        try
        {
            var mods = ModManager.GetLoadedMods().Where(m => m.manifest != null).Select(m => new ModInfo
            {
                Id = m.manifest!.id ?? "", Version = m.manifest.version ?? "",
                AffectsGameplay = m.manifest.affectsGameplay, WorkshopId = m.workshopId?.ToString() ?? "",
            }).ToList();
            _state.ModLog.Add(new ModSnapshot { T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Mods = mods });
            if (_state.ModLog.Count > 1000) _state.ModLog.RemoveRange(0, _state.ModLog.Count - 1000);
            _state.Save(_stateDir);
        }
        catch (Exception e) { GD.PrintErr($"{Tag} mod snapshot: {e.Message}"); }
    }

    /// The snapshot taken when this run ended: the first one after it started,
    /// within its run time plus a generous margin for pauses / save-and-quit.
    static List<ModInfo>? ModsFor(RunFile r)
    {
        if (r.StartTime <= 0) return null;
        long end = r.StartTime + r.RunTime + 12 * 3600;
        return _state.ModLog.Where(x => x.T >= r.StartTime && x.T <= end).OrderBy(x => x.T).FirstOrDefault()?.Mods;
    }

    static void Schedule(TimeSpan delay, string why) => Task.Run(async () =>
    {
        await Task.Delay(delay);
        await SyncAsync(why);
    });

    static async Task SyncAsync(string why)
    {
        if (!await Busy.WaitAsync(0)) return;     // one sync at a time
        try
        {
            SetStatus("uploading…");
            var runs = CollectRuns();
            if (runs.Count == 0) { GD.Print($"{Tag} {why}: no run files found"); return; }
            var needed = await CheckAsync(runs.Select(r => r.Hash).ToList());
            var anchor = runs.Where(r => r.Solo).OrderBy(r => r.StartTime).FirstOrDefault();
            if (needed.Count == 0)
            {
                GD.Print($"{Tag} {why}: {runs.Count} runs, all already on sts2.fun");
                if (string.IsNullOrEmpty(_state.Username) && anchor != null)
                {   // learn the account name: the identity run alone is a duplicate the site skips
                    var (u, _) = await UploadAsync(new List<RunFile> { anchor });
                    if (!string.IsNullOrEmpty(u)) { _state.Username = u; _state.Save(_stateDir); GD.Print($"{Tag} account: {u}"); }
                }
                SetStatus(""); return;
            }
            var todo = runs.Where(r => needed.Contains(r.Hash)).ToList();
            for (int i = 0; i < todo.Count; i += MaxFilesPerZip)
            {
                var batch = todo.Skip(i).Take(MaxFilesPerZip).ToList();
                if (anchor != null && !batch.Contains(anchor)) batch.Insert(0, anchor);
                var (user, added) = await UploadAsync(batch);
                GD.Print($"{Tag} {why}: uploaded {batch.Count} runs -> player {user}, {added} new");
                if (!string.IsNullOrEmpty(user)) { _state.Username = user; _state.Save(_stateDir); }
                SetStatus(added > 0 ? $"+{added} run{(added == 1 ? "" : "s")}" : "");
            }
        }
        catch (Exception e) { GD.PrintErr($"{Tag} {why}: sync failed: {e.Message}"); SetStatus("upload failed"); }
        finally { Busy.Release(); }
    }

    sealed record RunFile(string Path, string Name, string Hash, long StartTime, long RunTime, bool Solo);

    /// Every .run file under <user data>/steam/<id>/[modded/]profile*/saves/history.
    static List<RunFile> CollectRuns()
    {
        var list = new List<RunFile>();
        var seen = new HashSet<string>();
        string root = Path.Combine(_userDir, "steam");
        if (!Directory.Exists(root)) return list;
        foreach (string hist in Directory.EnumerateDirectories(root, "history", SearchOption.AllDirectories))
        {
            if (!hist.Replace('\\', '/').EndsWith("/saves/history")) continue;
            foreach (string f in Directory.EnumerateFiles(hist, "*.run"))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(f);
                    string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (!seen.Add(hash)) continue;
                    long start = 0, runTime = 0; bool solo = false;
                    using (var doc = JsonDocument.Parse(bytes))
                    {
                        var r = doc.RootElement;
                        if (r.TryGetProperty("start_time", out var st) && st.ValueKind == JsonValueKind.Number) start = st.GetInt64();
                        if (r.TryGetProperty("run_time", out var rt) && rt.ValueKind == JsonValueKind.Number) runTime = (long)rt.GetDouble();
                        if (r.TryGetProperty("players", out var pl) && pl.ValueKind == JsonValueKind.Array) solo = pl.GetArrayLength() == 1;
                    }
                    list.Add(new RunFile(f, hash[..12] + "_" + System.IO.Path.GetFileName(f), hash, start, runTime, solo));
                }
                catch { /* unreadable or still being written: next sync picks it up */ }
            }
        }
        return list;
    }

    static async Task<HashSet<string>> CheckAsync(List<string> hashes)
    {
        var body = JsonSerializer.Serialize(new { hashes });
        using var resp = await Http.PostAsync(_cfg.Server.TrimEnd('/') + "/api/check-runs",
                                              new StringContent(body, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("needed").EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet();
    }

    static async Task<(string user, int added)> UploadAsync(List<RunFile> batch)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var r in batch)
            {
                var entry = zip.CreateEntry(r.Name, CompressionLevel.Optimal);
                using var es = entry.Open();
                await es.WriteAsync(await File.ReadAllBytesAsync(r.Path));
            }
        var file = new ByteArrayContent(ms.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var form = new MultipartFormDataContent { { file, "file", "runs.zip" } };
        var mods = batch.Select(r => (r.Hash, Mods: ModsFor(r))).Where(x => x.Mods != null)
                        .ToDictionary(x => x.Hash, x => x.Mods!);
        if (mods.Count > 0) form.Add(new StringContent(JsonSerializer.Serialize(mods)), "mods");
        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Server.TrimEnd('/') + "/upload") { Content = form };
        req.Headers.Accept.ParseAdd("application/json");       // JSON result (username, counts), not the HTML page
        using var resp = await Http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode}: {text[..Math.Min(200, text.Length)]}");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        return (root.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
                root.TryGetProperty("new_runs", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0);
    }

    // ---- main-menu badge: "sts2.fun: <username>", links to the player page ----

    public static void AddBadge(Control menu)
    {
        _badge = new LinkButton { Name = "Sts2FunBadge", Underline = LinkButton.UnderlineMode.OnHover };
        _badge.AddThemeFontSizeOverride("font_size", 18);
        _badge.AddThemeColorOverride("font_color", new Color(0.85f, 0.78f, 0.55f));
        // Anchor to the bottom-left corner of the (full-screen) menu; offsets are
        // relative to that corner, so this works before the menu has a size.
        _badge.AnchorLeft = 0; _badge.AnchorRight = 0; _badge.AnchorTop = 1; _badge.AnchorBottom = 1;
        _badge.OffsetLeft = 28; _badge.OffsetRight = 600; _badge.OffsetTop = -64; _badge.OffsetBottom = -30;
        _badge.GrowVertical = Control.GrowDirection.Begin;
        _badge.ZIndex = 50;
        _badge.MouseFilter = Control.MouseFilterEnum.Stop;
        menu.AddChild(_badge);
        UpdateBadge();
        GD.Print($"[sts2fun] badge added to main menu (menu size {menu.Size}, text '{_badge.Text}')");
    }

    static void SetStatus(string s) { _status = s; Callable.From(UpdateBadge).CallDeferred(); }

    static void UpdateBadge()
    {
        if (_badge == null || !GodotObject.IsInstanceValid(_badge)) return;
        string who = string.IsNullOrEmpty(_state.Username) ? "your stats" : _state.Username;
        string status = !_cfg.Enabled ? "uploads off" : _status;
        _badge.Text = $"sts2.fun: {who}" + (string.IsNullOrEmpty(status) ? "" : $"  ({status})");
        _badge.Uri = string.IsNullOrEmpty(_state.Username) ? _cfg.Server : $"{_cfg.Server.TrimEnd('/')}/player/{_state.Username}";
        _badge.TooltipText = _cfg.Enabled
            ? "Your runs upload to sts2.fun after each run. Click to open your page."
            : "Uploading is off. To turn it back on, set \"Enabled\": true in sts2fun_stats/config.json (game user data folder).";
    }
}

[HarmonyPatch(typeof(NMainMenu), "_Ready")]
static class Patch_NMainMenu_Ready
{
    static void Postfix(NMainMenu __instance)
    {
        try { Uploader.AddBadge(__instance); }
        catch (Exception e) { GD.PrintErr($"[sts2fun] badge: {e.Message}"); }
    }
}

/// <user data>/sts2fun_stats/state.json: the account the site assigned.
sealed class ModInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("affects_gameplay")] public bool AffectsGameplay { get; set; }
    [JsonPropertyName("workshop_id")] public string WorkshopId { get; set; } = "";
}

sealed class ModSnapshot
{
    public long T { get; set; }
    public List<ModInfo> Mods { get; set; } = new();
}

sealed class State
{
    public string Username { get; set; } = "";
    /// Mods loaded at the end of each run (unix time of the snapshot).
    public List<ModSnapshot> ModLog { get; set; } = new();
    public static State Load(string dir)
    {
        try
        {
            string path = Path.Combine(dir, "state.json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
        }
        catch { }
        return new State();
    }
    public void Save(string dir)
    {
        try { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(this)); }
        catch { }
    }
}

/// Hook the end of a run. RunManager.CleanUp runs when a run is left (game
/// over, victory, abandon), after the run file has been saved.
[HarmonyPatch(typeof(RunManager), "CleanUp")]
static class Patch_RunManager_CleanUp
{
    static bool Prepare() => AccessTools.Method(typeof(RunManager), "CleanUp") != null;
    static void Postfix() => Uploader.OnRunEnded();
}

/// <user data>/sts2fun_stats/config.json (created on first launch).
sealed class Config
{
    /// Uploads are on unless the player sets this to false (installing the mod is the opt-in).
    public bool Enabled { get; set; } = true;
    public string Server { get; set; } = "https://sts2.fun";

    public static Config Load(string dir)
    {
        string path = Path.Combine(dir, "config.json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Config();
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(new Config(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { GD.PrintErr($"[sts2fun] config: {e.Message}"); }
        return new Config();
    }
}
