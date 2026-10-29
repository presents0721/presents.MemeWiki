using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.MemeWiki;

/// <summary>
/// 词库增量同步：只拉 cnmeme 的「列表」（标题 + 摘要 + slug），不逐条拉详情。
/// 列表接口 30 条一页，全量约 220 页；每页间隔 300 ms，一次同步 1~2 分钟。
/// 详情仍然按需在线取，所以同步只解决「新梗标题压根不在本地」的问题。
/// </summary>
internal sealed class MemeSync : IDisposable
{
    const string Api = "https://cnmeme.wiki/api/v1/public/memes";
    const int PageSize = 30;

    readonly string overlayPath;
    readonly HttpClient http;
    readonly ILogger logger;

    public MemeSync(string overlayPath, ILogger logger, bool useSystemProxy = false)
    {
        this.overlayPath = overlayPath;
        this.logger = logger;

        SocketsHttpHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.GZip,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            UseProxy = useSystemProxy,
            ConnectCallback = Ipv4Connect.ConnectAsync
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
    }

    /// <summary>读出本地增量词库（同步产物）。</summary>
    public List<MemeEntry> LoadOverlay(Action<string>? warn = null)
    {
        try
        {
            if (!File.Exists(overlayPath)) return new List<MemeEntry>();

            List<MemeEntry>? list = JsonSerializer.Deserialize<List<MemeEntry>>(File.ReadAllText(overlayPath));
            return list ?? new List<MemeEntry>();
        }
        catch (Exception ex)
        {
            warn?.Invoke("增量词库读取失败：" + ex.Message);
            return new List<MemeEntry>();
        }
    }

    public DateTime LastSyncAt()
    {
        try
        {
            return File.Exists(overlayPath) ? File.GetLastWriteTime(overlayPath) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>全量同步标题 + 摘要；返回本次新增条数。已在列表里的标题不会重复。</summary>
    public async Task<(int Added, int Total, string Message)> SyncAsync(IEnumerable<string> knownTitles, CancellationToken token)
    {
        HashSet<string> known = new(knownTitles, StringComparer.Ordinal);
        Dictionary<string, MemeEntry> result = new(StringComparer.Ordinal);

        int offset = 0;
        int added = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            string url = $"{Api}?limit={PageSize}&offset={offset}";
            using JsonDocument? doc = await TryGetAsync(url);

            if (doc == null) break;

            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array) break;
            if (items.GetArrayLength() == 0) break;

            foreach (JsonElement item in items.EnumerateArray())
            {
                string title = item.TryGetProperty("title", out JsonElement te) ? te.GetString() ?? "" : "";
                string slug = item.TryGetProperty("slug", out JsonElement se) ? se.GetString() ?? "" : "";
                string summary = item.TryGetProperty("summary", out JsonElement me) ? me.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(slug)) continue;

                List<string> tags = new();
                if (item.TryGetProperty("tags", out JsonElement ge) && ge.ValueKind == JsonValueKind.Array)
                    tags.AddRange(ge.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));

                if (!known.Contains(title))
                {
                    added++;
                    known.Add(title);
                }

                result[title] = new MemeEntry
                {
                    Title = title,
                    Slug = slug,
                    Tags = tags,
                    Origin = MemeText.Clip(MemeText.Tidy(summary), 600),
                    UpdatedAt = ReadDate(item)
                };
            }

            bool more = root.TryGetProperty("has_more", out JsonElement hm) && hm.ValueKind == JsonValueKind.True;

            offset += PageSize;
            if (!more) break;
            if (offset > 40_000) break;      // 保险丝：接口异常时别无限翻页

            try
            {
                await Task.Delay(300, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (result.Count == 0)
            return (0, 0, "同步失败：一条都没取回来（网络或接口异常）");

        try
        {
            string? directory = Path.GetDirectoryName(overlayPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(overlayPath, JsonSerializer.Serialize(result.Values.ToList()));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[MemeWiki] 增量词库落盘失败");
            return (added, result.Count, "同步完成但写盘失败：" + ex.Message);
        }

        return (added, result.Count, $"同步完成：共 {result.Count} 条，其中新增 {added} 条");
    }

    /// <summary>
    /// 只同步「最近更新」的条目：按 updated_at 倒序翻页，翻到比上次同步还旧就停。
    ///
    /// 这是应对「梗才出来两三天」的主力手段——全量同步 220 页要好几分钟，
    /// 而新梗都在最前面，通常 1~2 页、几秒钟就够，于是可以放心每天跑好几次。
    /// </summary>
    public async Task<(int Added, int Total, string Message)> SyncRecentAsync(int maxPages, CancellationToken token)
    {
        int pages = Math.Clamp(maxPages, 1, 20);

        // 上次同步往前多留 3 天，覆盖「同一天多次更新」和时区差
        DateTime last = LastSyncAt();
        DateTime cutoff = last > DateTime.MinValue ? last.AddDays(-3) : DateTime.Now.AddDays(-14);

        Dictionary<string, MemeEntry> merged = new(StringComparer.Ordinal);
        foreach (MemeEntry entry in LoadOverlay())
            merged[entry.Title] = entry;

        int added = 0;
        int scanned = 0;
        bool reachedOld = false;

        for (int page = 0; page < pages && !reachedOld; page++)
        {
            token.ThrowIfCancellationRequested();

            string url = $"{Api}?limit={PageSize}&offset={page * PageSize}&sort=updated_at&order=desc";
            using JsonDocument? doc = await TryGetAsync(url);
            if (doc == null) break;

            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array) break;
            if (items.GetArrayLength() == 0) break;

            foreach (JsonElement item in items.EnumerateArray())
            {
                string title = item.TryGetProperty("title", out JsonElement te) ? te.GetString() ?? "" : "";
                string slug = item.TryGetProperty("slug", out JsonElement se) ? se.GetString() ?? "" : "";
                string summary = item.TryGetProperty("summary", out JsonElement me) ? me.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(slug)) continue;

                scanned++;
                string date = ReadDate(item);

                if (DateTime.TryParse(date, out DateTime updated) && updated < cutoff)
                {
                    reachedOld = true;
                    continue;
                }

                List<string> tags = new();
                if (item.TryGetProperty("tags", out JsonElement ge) && ge.ValueKind == JsonValueKind.Array)
                    tags.AddRange(ge.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));

                if (!merged.ContainsKey(title)) added++;

                merged[title] = new MemeEntry
                {
                    Title = title,
                    Slug = slug,
                    Tags = tags,
                    Origin = MemeText.Clip(MemeText.Tidy(summary), 600),
                    UpdatedAt = date
                };
            }

            try
            {
                await Task.Delay(250, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 一条都没扫到 = 网络/接口挂了。这时**不要**把已有的增量库回写一遍，
        // 否则会误报「同步成功，新增 0 条」，把真正的问题盖掉。
        if (scanned == 0)
            return (0, merged.Count, "同步失败：一条都没取回来（网络或接口异常，稍后会自动重试）");

        if (!SaveOverlay(merged.Values))
            return (added, merged.Count, "同步完成但写盘失败");

        return (added, merged.Count, $"最近更新已同步：扫描 {scanned} 条，新增 {added} 条，本地增量库 {merged.Count} 条");
    }

    static string ReadDate(JsonElement item)
    {
        foreach (string key in new[] { "updated_at", "created_at", "published_at" })
        {
            if (!item.TryGetProperty(key, out JsonElement value)) continue;

            string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            if (text.Length >= 10) return text[..10];
            if (text.Length > 0) return text;
        }

        return "";
    }

    bool SaveOverlay(IEnumerable<MemeEntry> entries)
    {
        try
        {
            string? directory = Path.GetDirectoryName(overlayPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(overlayPath, JsonSerializer.Serialize(entries.ToList()));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[MemeWiki] 增量词库落盘失败");
            return false;
        }
    }

    async Task<JsonDocument?> TryGetAsync(string url)
    {
        // cnmeme 走 Cloudflare，国内直连抖动很大：每页给两次机会
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(2000);
                    continue;
                }

                if (!response.IsSuccessStatusCode) return null;

                await using Stream stream = await response.Content.ReadAsStreamAsync();
                return await JsonDocument.ParseAsync(stream);
            }
            catch (Exception ex)
            {
                logger.LogDebug("[MemeWiki] 同步分页失败（第 {Attempt} 次）：{Message}", attempt + 1, ex.Message);
                if (attempt == 0) await Task.Delay(400);
            }
        }

        return null;
    }

    public void Dispose() => http.Dispose();
}
