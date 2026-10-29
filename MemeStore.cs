using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace presents.MemeWiki;

/// <summary>
/// 内置词库（6668 条，gzip 过的 JSON）。
///
/// 为什么不用 SQLite：插件是宿主用 Roslyn 现编译的，运行时没有 Microsoft.Data.Sqlite，
/// 分发 native dll 又太重。词库解压后 10 MB、对象化约 20 MB，直接常驻内存换取「毫秒级查询」更划算。
/// </summary>
internal sealed class MemeStore
{
    readonly List<MemeEntry> entries = new();
    readonly Dictionary<string, MemeEntry> byName = new(StringComparer.Ordinal);
    readonly Dictionary<char, List<MemeEntry>> byHead = new();

    public int Count => entries.Count;
    public string BuiltAt { get; private set; } = "";

    /// <summary>增量库里最新一条的更新时间，用来判断「我这库新不新」。</summary>
    public string LatestDate { get; private set; } = "";
    public string SourceUrl { get; private set; } = "";

    public static MemeStore Load(string gzipPath, Action<string>? warn = null)
    {
        MemeStore store = new();
        if (!File.Exists(gzipPath))
        {
            warn?.Invoke($"找不到内置词库：{gzipPath}（只能在线查）");
            return store;
        }

        try
        {
            using FileStream file = File.OpenRead(gzipPath);
            using GZipStream gzip = new(file, CompressionMode.Decompress);
            MemeData? data = JsonSerializer.Deserialize<MemeData>(gzip);

            if (data?.Entries == null || data.Entries.Count == 0)
            {
                warn?.Invoke("内置词库解析为空");
                return store;
            }

            store.BuiltAt = data.BuiltAt;
            store.SourceUrl = data.Source;

            foreach (MemeEntry entry in data.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Title)) continue;

                store.entries.Add(entry);

                string key = MemeText.Normalize(entry.Title);
                if (key.Length > 0) store.byName.TryAdd(key, entry);

                string slugKey = MemeText.Normalize(entry.Slug);
                if (slugKey.Length > 0) store.byName.TryAdd(slugKey, entry);

                // 模糊匹配按首字分桶：只和同首字、长度相近的条目比，6668 条也不会慢
                char head = key.Length > 0 ? key[0] : '#';
                if (!store.byHead.TryGetValue(head, out List<MemeEntry>? bucket))
                    store.byHead[head] = bucket = new List<MemeEntry>();
                bucket.Add(entry);
            }
        }
        catch (Exception ex)
        {
            warn?.Invoke("内置词库加载失败：" + ex.Message);
        }

        return store;
    }

    /// <summary>
    /// 检索。规则沿用上游「只认完整标题」的思路，但多做了两件事：
    /// 一是指向性更明确（「你是什么」不会命中「你是什么冠军」）；
    /// 二是没有任何命中时才做一次模糊匹配（「赛博庞克」→「赛博朋克」）。
    /// </summary>
    public List<MemeHit> Search(string query, int max, bool fuzzy)
    {
        List<MemeHit> result = new();
        string q = MemeText.Normalize(query);
        if (q.Length == 0 || entries.Count == 0) return result;

        Dictionary<string, MemeHit> best = new(StringComparer.Ordinal);

        void Offer(MemeEntry entry, int score, string how)
        {
            if (best.TryGetValue(entry.Title, out MemeHit? exist) && exist.Score >= score) return;
            best[entry.Title] = new MemeHit { Entry = entry, Score = score, How = how };
        }

        // ① 同名（标题 / slug 都算）
        if (byName.TryGetValue(q, out MemeEntry? exact))
            Offer(exact, 1000, "同名");

        // ② 查询里包含某个完整标题：取最长的那条（「你说你是什么冠军」→「你是什么冠军」）
        if (q.Length >= 2)
        {
            MemeEntry? longest = null;
            int longestLength = 0;

            foreach (MemeEntry entry in entries)
            {
                string title = MemeText.Normalize(entry.Title);
                if (title.Length < 2 || title.Length <= longestLength) continue;
                if (q.Contains(title, StringComparison.Ordinal) == false) continue;

                longest = entry;
                longestLength = title.Length;
            }

            if (longest != null)
                Offer(longest, 900 - Math.Min(150, (q.Length - longestLength) * 10), "标题出现在查询里");
        }

        // ③ 查询是某个标题的一部分（前缀优先）
        if (q.Length >= 2)
        {
            foreach (MemeEntry entry in entries)
            {
                string title = MemeText.Normalize(entry.Title);
                if (title.Length < 2 || title.Length == q.Length) continue;
                if (title.Contains(q, StringComparison.Ordinal) == false) continue;

                bool prefix = title.StartsWith(q, StringComparison.Ordinal);
                int score = prefix ? 800 - Math.Min(80, (title.Length - q.Length) * 4)
                                   : 700 - Math.Min(80, (title.Length - q.Length) * 4);

                Offer(entry, score, prefix ? "标题以查询开头" : "标题包含查询");
            }
        }

        // ④ 一个字的近似（只在前三类都没结果时做，避免把「大赛」和「大筛」混起来）
        if (fuzzy && best.Count == 0 && q.Length >= 2)
        {
            char head = q[0];
            if (byHead.TryGetValue(head, out List<MemeEntry>? bucket))
            {
                foreach (MemeEntry entry in bucket)
                {
                    string title = MemeText.Normalize(entry.Title);
                    if (Math.Abs(title.Length - q.Length) > 1) continue;

                    int distance = MemeText.Distance(q, title, 1);
                    if (distance <= 1) Offer(entry, 500 - distance * 100, "近似写法");
                }
            }
        }

        result.AddRange(best.Values.OrderByDescending(x => x.Score)
                                  .ThenBy(x => x.Entry.Title.Length)
                                  .Take(Math.Max(1, max)));

        return result;
    }

    public MemeEntry? Random(Random random)
        => entries.Count == 0 ? null : entries[random.Next(entries.Count)];

    public IEnumerable<string> Titles() => entries.Select(x => x.Title);

    /// <summary>把增量同步得到的条目并进来（同名跳过）。返回新增条数。</summary>
    public int Merge(IEnumerable<MemeEntry> extra)
    {
        int added = 0;

        foreach (MemeEntry entry in extra)
        {
            if (string.IsNullOrWhiteSpace(entry.Title)) continue;

            string key = MemeText.Normalize(entry.Title);
            if (key.Length == 0 || byName.ContainsKey(key)) continue;

            entries.Add(entry);
            byName[key] = entry;

            string slugKey = MemeText.Normalize(entry.Slug);
            if (slugKey.Length > 0) byName.TryAdd(slugKey, entry);

            char head = key[0];
            if (!byHead.TryGetValue(head, out List<MemeEntry>? bucket))
                byHead[head] = bucket = new List<MemeEntry>();
            bucket.Add(entry);

            if (string.CompareOrdinal(entry.UpdatedAt, LatestDate) > 0) LatestDate = entry.UpdatedAt;
            added++;
        }

        return added;
    }
}
