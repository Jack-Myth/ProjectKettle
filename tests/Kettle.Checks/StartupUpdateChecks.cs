using Kettle.Core;

internal static class StartupUpdateChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string sample = "noise\r\n\x1b[32m[  0%] 正在检查可用更新…\x1b[0m\r[ 37 % ] 下载 甲乙丙\r[----] 正在验证安装…\n[100%] 完成\n[101%] invalid\n[-1%] invalid\n[3.5%] invalid\n";
        foreach (int chunk in new[]
        {
            1,
            7,
            4096
        }

        )
        {
            var updates = new List<SteamUpdateProgress>();
            var parser = new SteamUpdateProgressParser(updates.Add);
            for (int i = 0; i < sample.Length; i += chunk)
                parser.Feed(sample.Substring(i, Math.Min(chunk, sample.Length - i)));
            parser.Feed("", true);
            check(updates.Select(u => u.Percent).SequenceEqual(new int?[] { 0, 37, null, 100 }), "percentage/indeterminate CR refresh and chunking " + chunk);
            check(updates[0].Text == "正在检查可用更新…" && updates[1].Text == "下载 甲乙丙" && updates[2].Text == "正在验证安装…", "localized progress suffix is neither translated nor truncated " + chunk);
        }

        var redraw = new List<SteamUpdateProgress>();
        var redrawParser = new SteamUpdateProgressParser(redraw.Add);
        foreach (char c in "[37%] 正在下载中文文件\x1b[25l\x1b[m\x1b[H\x1b[?25h[----] 正在验证安装…\x1b[H\r\n")
            redrawParser.Feed(c.ToString());
        check(redraw.Count == 2 && redraw[0].Percent == 37 && redraw[0].Text == "正在下载中文文件" && redraw[1].Percent is null && redraw[1].Text == "正在验证安装…", "real ConPTY CR-to-cursor-position redraw separates complete localized progress lines");
        var texts = new List<SteamUpdateProgress>();
        var eof = new SteamUpdateProgressParser(texts.Add);
        eof.Feed("[----] 末尾没有换行");
        check(texts.Count == 0, "partial localized line waits for delimiter or EOF");
        eof.Feed("", true);
        check(texts.Single().Text == "末尾没有换行", "EOF flush preserves full localized line");
    }
}
