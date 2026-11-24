namespace LabelMaker.Services;

/// <summary>
/// Fonts offered up front. Everything is served by Google Fonts; any other
/// family from that catalogue can be loaded by typing its name.
/// </summary>
public static class FontCatalog
{
    public static readonly (string Group, string[] Families)[] Curated =
    [
        ("Monospace", ["IBM Plex Mono", "JetBrains Mono", "Roboto Mono", "Space Mono", "Source Code Pro", "DM Mono", "B612 Mono", "Overpass Mono", "Share Tech Mono", "Courier Prime"]),
        ("Clear at small sizes", ["Inter", "Atkinson Hyperlegible", "IBM Plex Sans", "Roboto", "Source Sans 3", "Public Sans", "Lexend", "DM Sans"]),
        ("Condensed", ["Roboto Condensed", "Barlow Condensed", "IBM Plex Sans Condensed", "Archivo Narrow", "Saira Condensed", "Oswald", "Sofia Sans Extra Condensed"]),
        ("Display & pixel", ["Bebas Neue", "Anton", "Staatliches", "Silkscreen", "Pixelify Sans", "VT323", "Press Start 2P"]),
        ("Serif & script", ["IBM Plex Serif", "Roboto Slab", "Merriweather", "Playfair Display", "Caveat", "Permanent Marker"]),
    ];

    /// <summary>Other well-known Google Fonts families, for search suggestions.</summary>
    public static readonly string[] More =
    [
        "Open Sans", "Lato", "Montserrat", "Poppins", "Noto Sans", "Noto Serif", "Noto Sans Mono", "Raleway", "Nunito", "Nunito Sans",
        "Ubuntu", "Ubuntu Mono", "Ubuntu Condensed", "Rubik", "Work Sans", "Fira Sans", "Fira Code", "Fira Mono", "Fira Sans Condensed",
        "Barlow", "Barlow Semi Condensed", "Mulish", "Quicksand", "Karla", "Manrope", "Outfit", "Plus Jakarta Sans", "Figtree", "Sora",
        "Space Grotesk", "Archivo", "Archivo Black", "Libre Franklin", "Libre Baskerville", "Libre Barcode 39", "Libre Barcode 128",
        "PT Sans", "PT Sans Narrow", "PT Serif", "PT Mono", "Titillium Web", "Exo 2", "Josefin Sans", "Cabin", "Cabin Condensed",
        "Heebo", "Hind", "Kanit", "Prompt", "Asap", "Asap Condensed", "Dosis", "Abel", "Yanone Kaffeesatz", "Pathway Gothic One",
        "Fjalla One", "Teko", "Rajdhani", "Orbitron", "Chakra Petch", "Russo One", "Bungee", "Black Ops One", "Stardos Stencil",
        "Allerta Stencil", "Saira", "Saira Extra Condensed", "Big Shoulders Display", "League Gothic", "Alfa Slab One", "Righteous",
        "Lobster", "Pacifico", "Dancing Script", "Satisfy", "Kalam", "Patrick Hand", "Indie Flower", "Shadows Into Light", "Amatic SC",
        "Lora", "EB Garamond", "Cormorant Garamond", "Crimson Text", "Bitter", "Arvo", "Zilla Slab", "Spectral", "Source Serif 4",
        "Inconsolata", "Anonymous Pro", "Cousine", "Cutive Mono", "Major Mono Display", "Nova Mono", "Oxygen", "Oxygen Mono",
        "Red Hat Display", "Red Hat Mono", "Red Hat Text", "Sometype Mono", "Martian Mono", "Geist", "Geist Mono", "Azeret Mono",
        "Chivo", "Chivo Mono", "Syne", "Syne Mono", "Xanh Mono", "Victor Mono", "Commit Mono", "Kode Mono", "Reddit Mono",
        "Noto Sans JP", "Noto Sans KR", "Noto Sans SC", "Noto Sans Arabic", "Noto Sans Hebrew", "Noto Sans Thai", "Noto Sans Symbols 2",
        "Jersey 10", "Tiny5", "Micro 5", "DotGothic16", "Doto", "Workbench", "Sixtyfour",
    ];

    public static readonly string[] AllCurated = Curated.SelectMany(g => g.Families).ToArray();

    /// <summary>Every Google Fonts family, once the list has been downloaded; until then the built-in suggestions.</summary>
    public static IReadOnlyList<string> Everything { get; private set; } = AllCurated.Concat(More).Distinct().ToArray();
    public static bool FullListLoaded { get; private set; }

    public static void SetFullList(IEnumerable<string> families)
    {
        var all = families.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToList();
        if (all.Count < 100) return; // not a real catalogue
        Everything = AllCurated.Concat(More).Concat(all).Distinct().ToArray();
        FullListLoaded = true;
    }

    // ---- search ---------------------------------------------------------------------------
    // Google shows some families under a longer name than the one they are loaded by
    // ("Playwrite Australia SA Guides" is "Playwrite AU SA Guides"). The search therefore
    // matches word by word, and lets a short upper-case code stand for a longer word.

    // country codes used in family names, with the names people type for them
    static readonly Dictionary<string, string[]> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AR"] = ["argentina"], ["AT"] = ["austria", "osterreich"], ["AU"] = ["australia"], ["BE"] = ["belgium", "belgie", "belgique"],
        ["BR"] = ["brazil", "brasil"], ["CA"] = ["canada"], ["CL"] = ["chile"], ["CO"] = ["colombia"], ["CU"] = ["cuba"],
        ["CZ"] = ["czech", "cesko"], ["DE"] = ["germany", "deutschland"], ["DK"] = ["denmark", "danmark"], ["ES"] = ["spain", "espana"],
        ["FR"] = ["france"], ["GB"] = ["england", "britain", "uk"], ["HR"] = ["croatia", "hrvatska"], ["HU"] = ["hungary", "magyarorszag"],
        ["ID"] = ["indonesia"], ["IE"] = ["ireland"], ["IN"] = ["india"], ["IS"] = ["iceland", "island"], ["IT"] = ["italy", "italia"],
        ["MX"] = ["mexico"], ["NG"] = ["nigeria"], ["NL"] = ["netherlands", "nederland"], ["NO"] = ["norway", "norge"],
        ["NZ"] = ["new", "zealand"], ["PE"] = ["peru"], ["PL"] = ["poland", "polska"], ["PT"] = ["portugal"], ["RO"] = ["romania"],
        ["SK"] = ["slovakia", "slovensko"], ["TZ"] = ["tanzania"], ["US"] = ["usa", "america", "united", "states"],
        ["VN"] = ["vietnam", "viet", "nam"], ["ZA"] = ["south", "africa"],
        ["JP"] = ["japanese", "japan"], ["KR"] = ["korean", "korea"], ["SC"] = ["simplified", "chinese"], ["TC"] = ["traditional", "chinese"],
        ["HK"] = ["hong", "kong"],
    };

    static string Plain(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => c switch
        {
            'á' or 'à' or 'â' or 'ä' or 'ã' or 'å' => 'a', 'é' or 'è' or 'ê' or 'ë' or 'ě' => 'e', 'í' or 'ì' or 'î' or 'ï' => 'i',
            'ó' or 'ò' or 'ô' or 'ö' or 'õ' or 'ø' => 'o', 'ú' or 'ù' or 'û' or 'ü' => 'u', 'ñ' => 'n', 'ç' or 'č' => 'c', 'š' => 's', 'ž' => 'z',
            _ => char.IsLetterOrDigit(c) ? c : ' ',
        });
        return string.Join(' ', new string(chars.ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Families matching what was typed, best first: the name itself, then names that contain
    /// it, then names matching every typed word (a code such as AU may stand for "Australia").
    /// </summary>
    public static List<string> Search(string query, IEnumerable<string>? extra = null, int take = 40)
    {
        string q = Plain(query);
        if (q.Length == 0) return new();
        var words = q.Split(' ');
        var hits = new List<(int Rank, string Family)>();
        foreach (var family in extra is null ? Everything : Everything.Concat(extra).Distinct())
        {
            string f = Plain(family);
            int rank = f == q ? 0 : f.StartsWith(q, StringComparison.Ordinal) ? 1 : f.Contains(q, StringComparison.Ordinal) ? 2 : WordRank(words, family);
            if (rank >= 0) hits.Add((rank, family));
        }
        return hits.OrderBy(h => h.Rank).ThenBy(h => h.Family.Length).ThenBy(h => h.Family, StringComparer.Ordinal).Take(take).Select(h => h.Family).ToList();
    }

    /// <summary>3 or more when every typed word matches a word of the family (lower is closer), otherwise -1.</summary>
    static int WordRank(string[] words, string family)
    {
        var raw = family.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var names = raw.Select(Plain).ToArray();
        var used = new bool[raw.Length];
        int direct = 0, viaCode = 0;
        foreach (var word in words)
        {
            int at = Array.FindIndex(names, 0, n => n.StartsWith(word, StringComparison.Ordinal));
            if (at < 0) at = Array.FindIndex(names, n => n.Contains(word, StringComparison.Ordinal) && word.Length >= 3);
            if (at >= 0) { used[at] = true; direct++; continue; }
            // a short upper-case code in the family name standing for the typed word: AU for "australia"
            at = -1;
            for (int i = 0; i < raw.Length && at < 0; i++)
            {
                bool code = raw[i].Length is >= 2 and <= 4 && raw[i].All(char.IsUpper);
                if (!code) continue;
                if (word.StartsWith(names[i], StringComparison.Ordinal)
                    || (Codes.TryGetValue(raw[i], out var meanings) && meanings.Any(m => m.StartsWith(word, StringComparison.Ordinal) || word.StartsWith(m, StringComparison.Ordinal))))
                    at = i;
            }
            if (at < 0) return -1;
            used[at] = true; viaCode++;
        }
        // a code match alone is too loose ("sans" must not find every family with an SA in it)
        if (direct == 0) return -1;
        return 3 + viaCode + used.Count(u => !u);
    }
}
