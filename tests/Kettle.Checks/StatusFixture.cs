internal static class StatusFixture
{
    public static string Body(uint id, ulong? bytes) => $"AppID {id} (Synthetic game):\r\n - install state: {(bytes.HasValue ? "Fully Installed," : "uninstalled")}\r\n" + (bytes.HasValue ? $" - size on disk: {bytes} bytes, BuildID 1\r\n" : "") + " - user config: \"UserConfig\"\r\n{\r\n \"nested\" { \"quoted\" \"brace } in value\" }\r\n}\r\n";
}
