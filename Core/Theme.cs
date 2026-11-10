using System.Windows;
using System.Windows.Media;

namespace Recorder;

public sealed record ThemeDef(string Id, string Name, string Bg, string Surface, string Surface2, string Border,
    string Text, string Dim, string Accent, string OnAccent);

public static class Themes
{
    public static readonly ThemeDef[] All =
    {
        new("white",    "White",    "FFFFFF", "F7F7F8", "EEEEF0", "DEDEE3", "111114", "6E6E78", "18181B", "FFFFFF"),
        new("black",    "Black",    "000000", "0B0B0C", "161618", "262629", "F4F4F5", "8E8E96", "FAFAFA", "000000"),
        new("violet",   "Violet",   "0E0B16", "161224", "1F1A31", "2E2848", "ECE9F7", "9A93B5", "8B5CF6", "FFFFFF"),
        new("ocean",    "Blue",     "090D16", "101726", "172036", "24314F", "E6ECF8", "8C9BB8", "3B82F6", "FFFFFF"),
        new("ember",    "Orange",   "0D0A08", "17110D", "221913", "38281C", "F5EBE3", "A8927F", "F97316", "1A0C02"),
        new("rose",     "Pink",     "120A0E", "1C1117", "27171F", "41212F", "F8E8EE", "B58C9C", "F43F5E", "FFFFFF"),
        new("emerald",  "Emerald",  "080F0D", "0F1A17", "15251F", "1F3A31", "E4F3EE", "85A89B", "10B981", "03140E"),
        new("aurora",   "Aurora",   "07110F", "0D1B19", "132624", "1E3A37", "E3F6F2", "86ABA5", "2DD4BF", "03201B"),
        new("cosmos",   "Cyan",     "05040A", "0E0B18", "161226", "2A2140", "EDE9FF", "9C94C0", "22D3EE", "03161A"),
        new("sunset",   "Sunset",   "140A0C", "1F1015", "2A161C", "46222C", "FBE9E4", "B9918C", "FF7A59", "1F0A05"),
        new("midnight", "Midnight", "0A0B1A", "12142B", "1A1D3A", "2A2E57", "E8E9FA", "9497C2", "FBBF24", "1A1200"),
        new("lavender", "Lavender", "F6F4FB", "FFFFFF", "EFEBF8", "DDD6EE", "1D1730", "6E668A", "7C3AED", "FFFFFF"),
        new("sakura",   "Sakura",   "FCF4F7", "FFFFFF", "F8E7EE", "EDD0DC", "33141F", "8A6474", "DB2777", "FFFFFF"),
        new("mint",     "Mint",     "F2FAF6", "FFFFFF", "E4F3EC", "CFE6DA", "15302A", "5E7F75", "0E9F6E", "FFFFFF"),
        new("latte",    "Latte",    "EADFCC", "F5ECDF", "E3D6C0", "D2C1A5", "2E2318", "7A6650", "9A6A3E", "FFFFFF"),
    };

    public static ThemeDef Current { get; private set; } = All[0];

    public static event Action? Changed;

    public static ThemeDef Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString("#" + hex);

    public static void Apply(string? id)
    {
        var t = Find(id);
        Current = t;
        var a = C(t.Accent);
        var tx = C(t.Text);
        var map = new Dictionary<string, Color>
        {
            ["Bg"] = C(t.Bg), ["Surface"] = C(t.Surface), ["Surface2"] = C(t.Surface2), ["Border"] = C(t.Border),
            ["Text"] = tx, ["Dim"] = C(t.Dim), ["Accent"] = a, ["OnAccent"] = C(t.OnAccent),
            ["AccentSoft"] = Color.FromArgb(0x30, a.R, a.G, a.B),
            ["Hover"] = Color.FromArgb(0x14, tx.R, tx.G, tx.B),
        };
        var res = Application.Current.Resources;
        foreach (var kv in map)
        {
            var b = new SolidColorBrush(kv.Value);
            b.Freeze();
            res[kv.Key] = b;
        }
        Changed?.Invoke();
    }
}
