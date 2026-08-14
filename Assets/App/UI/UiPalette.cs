using UnityEngine;

namespace PointCloud.App.UI
{
    /// <summary>
    /// The application palette, in one place.
    ///
    /// Theme.uss carries the same values as USS custom properties — stylesheets cannot read
    /// C# — so treat this file as the source of truth and keep the two in step. The scene's
    /// camera background is driven from here so the viewport and the panels can never drift
    /// apart.
    ///
    /// Measured WCAG contrast against the panel surface (#322A51):
    ///
    ///   Linen      #F7ECE1  11.4:1  — primary text, AAA
    ///   Lavender   #CAC4CE   7.8:1  — secondary text, AAA
    ///   Violet     #8D86C9   4.0:1  — accents, marks and borders ONLY, never body text
    ///   Purple     #725AC1   2.5:1  — fills and selection ONLY; carries Linen at 4.6:1
    ///   SpaceCadet #242038          — the viewport background
    ///
    /// The two purples are deliberately excluded from small text. Violet reads fine as a
    /// glyph or a border but lands just under the 4.5:1 threshold at body sizes, and using
    /// it for labels would quietly make the densest part of the UI the least legible.
    /// </summary>
    public static class UiPalette
    {
        public static readonly Color SpaceCadet = Hex(0x24, 0x20, 0x38);
        public static readonly Color Purple     = Hex(0x72, 0x5A, 0xC1);
        public static readonly Color Violet     = Hex(0x8D, 0x86, 0xC9);
        public static readonly Color Lavender   = Hex(0xCA, 0xC4, 0xCE);
        public static readonly Color Linen      = Hex(0xF7, 0xEC, 0xE1);

        /// <summary>Camera clear colour. The darkest palette entry, so points read brightest.</summary>
        public static readonly Color SceneBackground = SpaceCadet;

        /// <summary>
        /// Panel surface: SpaceCadet lifted 18% toward Purple. Panels must separate from the
        /// viewport behind them, and the palette has no mid-tone neutral to do it with.
        /// </summary>
        public static readonly Color PanelSurface = Hex(0x32, 0x2A, 0x51);

        /// <summary>Default colour for Flat render mode — the brightest entry, against the darkest background.</summary>
        public static readonly Color FlatPointColor = Linen;

        static Color Hex(byte r, byte g, byte b) => new Color32(r, g, b, 255);
    }
}
