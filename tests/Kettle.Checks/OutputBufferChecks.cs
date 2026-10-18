using Kettle.Core;

internal static class OutputBufferChecks
{
    public static void Run(Action<bool, string> check)
    {
        var response = new ConsoleOutputBuffer();
        var full = new string('x', ConsoleOutputBuffer.ResponseCapacity - 4) + "end!";
        response.AppendNormalized(full, "", false);
        check(response.Text == full && response.Length * sizeof(char) == 1024 * 1024, "one MiB response is retained exactly across buffer growth");
        bool refused = false;
        try { response.AppendNormalized("!", "", false); }
        catch (IOException) { refused = true; }
        check(refused && response.Text == full, "oversized response is refused without overwriting its beginning");
        response.Clear();
        response.Append("new\bxt\r\n");
        check(response.Text == "next\n", "cleared response buffer is reusable with no previous content");

        var idle = new ConsoleOutputBuffer(rolling: true);
        var source = string.Concat(Enumerable.Range(0, ConsoleOutputBuffer.IdleCapacity * 3).Select(i => (char)('a' + i % 26)));
        for (int at = 0; at < source.Length; at += 137)
            idle.AppendNormalized(source.Substring(at, Math.Min(137, source.Length - at)), "", false);
        check(idle.Text == source[^ConsoleOutputBuffer.IdleCapacity..], "rolling output keeps exactly the latest characters through multiple wraps");
        idle.AppendNormalized("\b\bZ", "", false);
        check(idle.Text == source[^ConsoleOutputBuffer.IdleCapacity..^2] + "Z", "backspace works after the ring wraps");
        check(idle.Diagnostic("<idle>", "test", false, "Idle").Truncated, "discarded idle history is marked truncated");
        idle.Clear();
        idle.Append("Steam>");
        check(idle.Text == "Steam>" && !idle.Diagnostic("<idle>", "test", false, "Idle").Truncated, "clear resets ring indexes and truncation state");
    }
}
