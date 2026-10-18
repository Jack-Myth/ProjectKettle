namespace Kettle.Core;
/// <summary>Root markers and relative paths are wire identities, never local path strings.</summary>
public static class AutoCloudWire
{
    public static AutoCloudKey Key(string wire)
    {
        if (wire.Length > 2048 || !wire.StartsWith('%'))
            throw new InvalidDataException("AutoCloud 文件缺少已验证的根前缀。");
        var end = wire.IndexOf('%', 1);
        if (end <= 1)
            throw new InvalidDataException("AutoCloud 根前缀无效。");
        var root = AutoCloudRootNames.Canonical(wire[1..end]);
        var name = AutoCloudPathSafety.Relative(wire[(end + 1)..], false, false);
        return new(root, name);
    }

    public static string Name(AutoCloudKey key) => "%" + AutoCloudRootNames.Canonical(key.Root) + "%" + AutoCloudPathSafety.Relative(key.RelativeName, false, false);
}
