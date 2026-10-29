using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace presents.MemeWiki;

/// <summary>
/// 用户自定义词条（自己圈子里的黑话 / 内部梗）。
/// 优先级高于内置词库和在线源：自己人定的说法比网上的解释更准。
/// 存到插件目录下的 user-terms.json，重装插件不会丢（插件目录更新只覆盖代码文件）。
/// </summary>
internal sealed class UserTerms
{
    readonly object gate = new();
    readonly List<UserTerm> terms = new();
    readonly string path;

    public UserTerms(string path) => this.path = path;

    public int Count { get { lock (gate) return terms.Count; } }

    public IReadOnlyList<UserTerm> All { get { lock (gate) return terms.ToList(); } }

    public void Load(Action<string>? warn = null)
    {
        try
        {
            if (!File.Exists(path)) return;

            List<UserTerm>? data = JsonSerializer.Deserialize<List<UserTerm>>(File.ReadAllText(path));
            if (data == null) return;

            lock (gate)
            {
                terms.Clear();
                terms.AddRange(data.Where(x => !string.IsNullOrWhiteSpace(x.Name)));
            }
        }
        catch (Exception ex)
        {
            warn?.Invoke("自定义词条读取失败：" + ex.Message);
        }
    }

    /// <summary>查询词里出现完整词条名即命中（和词库同一套规则）。</summary>
    public bool TryMatch(string query, out UserTerm term)
    {
        term = new UserTerm();
        string q = MemeText.Normalize(query);
        if (q.Length == 0) return false;

        lock (gate)
        {
            UserTerm? exact = terms.FirstOrDefault(x => MemeText.Normalize(x.Name) == q);
            if (exact != null)
            {
                term = exact;
                return true;
            }

            UserTerm? contains = terms
                .Where(x => MemeText.Normalize(x.Name).Length >= 2 && q.Contains(MemeText.Normalize(x.Name), StringComparison.Ordinal))
                .OrderByDescending(x => MemeText.Normalize(x.Name).Length)
                .FirstOrDefault();

            if (contains == null) return false;

            term = contains;
            return true;
        }
    }

    public (bool Ok, string Message) Add(string name, string meaning)
    {
        name = (name ?? "").Trim();
        meaning = (meaning ?? "").Trim();

        if (name.Length == 0) return (false, "词条名为空");
        if (meaning.Length == 0) return (false, "还没给含义");
        if (name.Length > 40) return (false, "词条名太长（上限 40 字）");
        if (meaning.Length > 500) meaning = meaning[..500];

        lock (gate)
        {
            UserTerm? exist = terms.FirstOrDefault(x => MemeText.Normalize(x.Name) == MemeText.Normalize(name));
            bool updated = exist != null;

            if (exist != null)
            {
                exist.Meaning = meaning;
                exist.At = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            }
            else
            {
                terms.Add(new UserTerm { Name = name, Meaning = meaning, At = DateTime.Now.ToString("yyyy-MM-dd HH:mm") });
            }

            Save();
            return (true, updated ? $"已更新词条「{name}」" : $"已记住「{name}」");
        }
    }

    public (bool Ok, string Message) Remove(string name)
    {
        string key = MemeText.Normalize(name);
        lock (gate)
        {
            int removed = terms.RemoveAll(x => MemeText.Normalize(x.Name) == key);
            if (removed == 0) return (false, $"没有「{name}」这条自定义词条");

            Save();
            return (true, $"已删除「{name}」");
        }
    }

    void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(terms, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 写盘失败不影响这次查询
        }
    }
}
