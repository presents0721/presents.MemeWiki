using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace presents.MemeWiki;

/// <summary>把查到的资料拼成人能读、模型也能直接接梗的文本。</summary>
internal static class MemeRender
{
    public static string Format(MemeAnswer answer, int maxChars)
    {
        StringBuilder sb = new();

        string head = string.IsNullOrWhiteSpace(answer.Title) ? answer.Query : answer.Title;
        sb.Append("【梗百科】").Append(head);
        if (!string.IsNullOrWhiteSpace(answer.How)) sb.Append("　（").Append(answer.How).Append('）');
        sb.AppendLine();

        if (answer.Tags.Count > 0)
            sb.Append("标签：").AppendLine(string.Join("、", answer.Tags.Take(8)));

        // 自定义词条只有「含义」，没有出处
        if (!string.IsNullOrWhiteSpace(answer.Usage))
        {
            sb.AppendLine();
            sb.AppendLine(answer.Custom ? "【含义】" : "【含义 / 怎么接】");
            sb.AppendLine(MemeText.Clip(answer.Usage, 700));
        }

        if (!string.IsNullOrWhiteSpace(answer.Origin))
        {
            sb.AppendLine();
            sb.AppendLine("【出处】");
            sb.AppendLine(MemeText.Clip(answer.Origin, 500));
        }

        if (!string.IsNullOrWhiteSpace(answer.Other))
        {
            sb.AppendLine();
            sb.AppendLine("【补充】");
            sb.AppendLine(MemeText.Clip(answer.Other, 350));
        }

        foreach (OnlineSnippet extra in answer.Extras.Take(3))
        {
            List<string> lines = new();

            if (extra.Facts.Count > 0)
                lines.Add(string.Join(" · ", extra.Facts.Select(x => $"{x.Key}：{x.Value}")));

            if (!string.IsNullOrWhiteSpace(extra.Usage))
                lines.Add(MemeText.Clip(extra.Usage, 260));
            else if (!string.IsNullOrWhiteSpace(extra.Origin))
                lines.Add(MemeText.Clip(extra.Origin, 260));
            else if (!string.IsNullOrWhiteSpace(extra.Other))
                lines.Add(MemeText.Clip(extra.Other, 200));

            if (lines.Count == 0) continue;

            sb.AppendLine();
            sb.Append("· ").Append(extra.Source);
            if (!string.IsNullOrWhiteSpace(extra.Title) && extra.Title != head)
                sb.Append('「').Append(extra.Title).Append('」');
            sb.AppendLine("：");
            foreach (string line in lines) sb.AppendLine(line);
        }

        if (answer.Related.Count > 0)
        {
            sb.AppendLine();
            sb.Append("相关：").AppendLine(string.Join("、", answer.Related.Take(6)));
        }

        if (!string.IsNullOrWhiteSpace(answer.Url))
            sb.AppendLine("来源：" + answer.Url);

        string text = MemeText.Tidy(sb.ToString());
        int limit = Math.Clamp(maxChars, 300, 8000);
        return text.Length <= limit ? text : text[..limit] + "\n…（已截断，需要更完整的解释可以再问）";
    }

    public static string FormatMiss(string query, bool onlineTried, bool hasLocal)
    {
        StringBuilder sb = new();
        sb.Append("【梗百科】没查到「").Append(query).Append('」');
        sb.AppendLine();
        sb.AppendLine(hasLocal
            ? "本地词库里没有同名条目。"
            : "本地词库（6668 条）没有收录。");

        if (onlineTried)
            sb.Append("已尝试在线源（cnmeme.wiki / 百度百科 / 维基），也没有给出可靠解释。");
        else
            sb.Append("当前没开在线补充，只有本地词库和自定义词条。");

        sb.AppendLine();
        sb.Append("可以换个更短的写法再试（比如只留梗的核心词），或者用「记住梗」把它的含义存下来。");
        return sb.ToString();
    }

    public static string FormatStatus(int localCount, string builtAt, int userCount, IReadOnlyList<string> sources, long hits, long misses, string latestDate)
    {
        StringBuilder sb = new();
        sb.AppendLine("【梗百科】状态");
        sb.AppendLine($"内置词库：{localCount} 条（构建于 {(string.IsNullOrWhiteSpace(builtAt) ? "未知" : builtAt)}）");
        sb.AppendLine($"自定义词条：{userCount} 条");
        if (!string.IsNullOrWhiteSpace(latestDate))
            sb.AppendLine($"增量同步：最近收录到 {latestDate}");
        sb.AppendLine("在线源：" + (sources.Count == 0 ? "全部关闭" : string.Join("、", sources)));
        sb.AppendLine($"本次运行：命中 {hits} 次，未命中 {misses} 次");
        return sb.ToString().Trim();
    }
}
