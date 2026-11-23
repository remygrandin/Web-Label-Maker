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
}
