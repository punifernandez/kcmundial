using KCMundial.Core.Interfaces;

namespace KCMundial.Storage;

public sealed class FileNaming : IFileNaming
{
    private static readonly char[] Chars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();
    private readonly Random _rnd = new();

    public string NewId()
    {
        var now = DateTime.Now;
        var date = now.ToString("yyyy-MM-dd_HH-mm-ss");
        var suffix = new string(Enumerable.Range(0, 4).Select(_ => Chars[_rnd.Next(Chars.Length)]).ToArray());
        return $"{date}_{suffix}";
    }
}
