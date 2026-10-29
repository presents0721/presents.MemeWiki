using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace presents.MemeWiki;

/// <summary>文本处理：归一化、截断、HTML 转纯文本、按小标题拆段、编辑距离。</summary>
internal static class MemeText
{
    /// <summary>
    /// 归一化查询词：全角转半角、去空白、去标点、英文小写。
    /// 梗名里常带 · ～ ！这些符号，用户输入时又常常不带，所以匹配前统一抹掉。
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        StringBuilder sb = new(value.Length);
        foreach (char raw in value)
        {
            char c = raw;

            // 全角 ASCII → 半角
            if (c >= '\uFF01' && c <= '\uFF5E') c = (char)(c - 0xFEE0);

            // 全角空格 / 各种空白
            if (c == '\u3000' || char.IsWhiteSpace(c)) continue;

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                continue;
            }

            // 其余（标点、括号、引号、emoji 里的符号）一律丢掉
        }

        return sb.ToString();
    }

    public static string Clip(string? value, int limit)
    {
        string s = (value ?? "").Trim();
        if (limit <= 0 || s.Length <= limit) return s;
        return s[..limit] + "…";
    }

    /// <summary>cnmeme 的 content_html → 带「## 小标题」的纯文本。</summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        StringBuilder sb = new(html.Length);
        StringBuilder tag = new(16);
        bool inTag = false;

        foreach (char c in html)
        {
            if (c == '<')
            {
                inTag = true;
                tag.Clear();
                continue;
            }

            if (inTag)
            {
                if (c != '>')
                {
                    if (tag.Length < 20) tag.Append(c);
                    continue;
                }

                inTag = false;
                string name = tag.ToString().Trim().ToLowerInvariant();

                if (name.StartsWith("h2", StringComparison.Ordinal) || name.StartsWith("h3", StringComparison.Ordinal))
                    sb.Append("\n## ");
                else if (name.StartsWith("li", StringComparison.Ordinal))
                    sb.Append("\n- ");
                else if (name.StartsWith("br", StringComparison.Ordinal))
                    sb.Append('\n');
                else if (name.StartsWith("p", StringComparison.Ordinal) || name.StartsWith("div", StringComparison.Ordinal))
                    sb.Append('\n');

                tag.Clear();
                continue;
            }

            sb.Append(c);
        }

        return Tidy(WebUtility.HtmlDecode(sb.ToString()));
    }

    /// <summary>压掉多余空白和空行。</summary>
    public static string Tidy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        StringBuilder sb = new(text.Length);
        int newlines = 0;
        bool lastSpace = false;

        foreach (char c in text)
        {
            if (c == '\r') continue;

            if (c == '\n')
            {
                newlines++;
                lastSpace = false;
                continue;
            }

            if (c == ' ' || c == '\t')
            {
                if (lastSpace || newlines > 0) continue;
                lastSpace = true;
                sb.Append(' ');
                continue;
            }

            if (newlines > 0)
                sb.Append(newlines >= 2 ? "\n\n" : "\n");

            newlines = 0;
            // 关键：普通字符之后必须复位。否则「A / B」里的第二个空格会被上一个空格吞掉
            lastSpace = false;
            sb.Append(c);
        }

        // 网页正文转过来时常留下孤零零的「-」「·」「#」这类项目符号残渣，逐行清掉
        List<string> kept = new();
        foreach (string line in sb.ToString().Split('\n'))
        {
            string trimmed = line.TrimEnd();

            if (trimmed.Length == 0)
            {
                if (kept.Count > 0 && kept[^1].Length != 0) kept.Add("");
                continue;
            }

            if (IsNoise(trimmed)) continue;

            kept.Add(trimmed);
        }

        return string.Join("\n", kept).Trim();
    }

    /// <summary>整行只有符号 / 空白，没有实际信息。</summary>
    static bool IsNoise(string line)
    {
        foreach (char c in line)
        {
            if (char.IsLetterOrDigit(c)) return false;

            if (c is not ('-' or '·' or '*' or '#' or '—' or '–' or '_' or '=' or '~' or '　' or ' ' or '\t'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 把正文按「## 小标题」拆成 用法 / 出处 / 其它。
    /// 和上游一致：认「用法/怎么用/例句」进 usage，认「来源/出处/背景/介绍」进 origin，其余归 other。
    /// </summary>
    public static (string Usage, string Origin, string Other) SplitSections(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("", "", "");

        string[] parts = ("\n" + text).Split("\n##", StringSplitOptions.RemoveEmptyEntries);

        List<string> usage = new();
        List<string> origin = new();
        List<string> other = new();

        foreach (string raw in parts)
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;

            int cut = part.IndexOf('\n');
            string head = cut < 0 ? part : part[..cut].Trim();
            string rest = cut < 0 ? "" : part[(cut + 1)..].Trim();

            if (head.Contains("用法") || head.Contains("怎么用") || head.Contains("如何用") ||
                head.Contains("例句") || head.Contains("接梗"))
                usage.Add(rest.Length > 0 ? rest : head);
            else if (head.Contains("来源") || head.Contains("出处") || head.Contains("背景") ||
                     head.Contains("起源") || head.Contains("介绍"))
                origin.Add(rest.Length > 0 ? rest : head);
            else
                other.Add(rest.Length > 0 ? head + "\n" + rest : head);
        }

        return (string.Join("\n", usage).Trim(), string.Join("\n", origin).Trim(), string.Join("\n", other).Trim());
    }

    /// <summary>带上限的编辑距离：超过 limit 立刻返回 limit + 1，避免做无用的整表计算。</summary>
    public static int Distance(string a, string b, int limit)
    {
        if (a == b) return 0;
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;

        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            int rowMin = current[0];
            char ca = a[i - 1];

            for (int j = 1; j <= b.Length; j++)
            {
                int cost = ca == b[j - 1] ? 0 : 1;
                int value = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                current[j] = value;
                if (value < rowMin) rowMin = value;
            }

            if (rowMin > limit) return limit + 1;

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>数字转成「1.2k」这种短写法，日志和状态里用。</summary>
    public static string Short(long value)
        => value < 1000 ? value.ToString() : value < 1_000_000 ? $"{value / 1000.0:0.#}k" : $"{value / 1_000_000.0:0.#}M";
}
