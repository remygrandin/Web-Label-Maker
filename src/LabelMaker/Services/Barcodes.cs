using LabelMaker.Models;
using Microsoft.JSInterop;
using ZXing;
using ZXing.QrCode.Internal;

namespace LabelMaker.Services;

/// <summary>
/// Encodes barcode nodes into module matrices (via ZXing) and hands them to the
/// renderer, which caches them by content key.
/// </summary>
public static class Barcodes
{
    static readonly Dictionary<string, string?> Done = new(); // key -> error (null = ok)

    // must match matrixKey() in lm.render.js
    public static string Key(BarcodeNode n) => $"{n.Format}|{n.Ecc}|{(n.QuietZone ? 1 : 0)}|{n.Value}";

    public static string? Error(BarcodeNode n) => Done.GetValueOrDefault(Key(n));

    public static void Ensure(BarcodeNode n, IJSInProcessRuntime js)
    {
        var key = Key(n);
        if (Done.ContainsKey(key)) return;
        try
        {
            if (string.IsNullOrEmpty(n.Value)) throw new ArgumentException("Enter a value to encode.");
            var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
            if (n.Format == CodeFormat.QrCode)
            {
                hints[EncodeHintType.ERROR_CORRECTION] = n.Ecc switch
                {
                    EccLevel.L => ErrorCorrectionLevel.L,
                    EccLevel.Q => ErrorCorrectionLevel.Q,
                    EccLevel.H => ErrorCorrectionLevel.H,
                    _ => ErrorCorrectionLevel.M,
                };
                if (n.Value.Any(c => c > 0x7f)) hints[EncodeHintType.CHARACTER_SET] = "UTF-8";
            }
            var m = new MultiFormatWriter().encode(n.Value, Map(n.Format), 0, 0, hints);

            // quiet zone in modules: all round for 2D codes, left/right only for linear ones
            int qx = !n.QuietZone ? 0 : n.Format switch
            {
                CodeFormat.QrCode or CodeFormat.Pdf417 => 2,
                CodeFormat.DataMatrix or CodeFormat.Aztec => 1,
                _ => 10,
            };
            int qy = n.Is2D ? qx : 0;
            int w = m.Width + 2 * qx, h = m.Height + 2 * qy;
            var bits = new byte[w * h];
            for (int y = 0; y < m.Height; y++)
                for (int x = 0; x < m.Width; x++)
                    if (m[x, y]) bits[(y + qy) * w + x + qx] = 1;
            js.InvokeVoid("LM.setMatrix", key, w, h, bits, null);
            Done[key] = null;
        }
        catch (Exception ex)
        {
            var msg = Friendly(n.Format, ex);
            js.InvokeVoid("LM.setMatrix", key, 0, 0, null, msg);
            Done[key] = msg;
        }
    }

    static BarcodeFormat Map(CodeFormat f) => f switch
    {
        CodeFormat.QrCode => BarcodeFormat.QR_CODE,
        CodeFormat.DataMatrix => BarcodeFormat.DATA_MATRIX,
        CodeFormat.Aztec => BarcodeFormat.AZTEC,
        CodeFormat.Pdf417 => BarcodeFormat.PDF_417,
        CodeFormat.Code128 => BarcodeFormat.CODE_128,
        CodeFormat.Code39 => BarcodeFormat.CODE_39,
        CodeFormat.Code93 => BarcodeFormat.CODE_93,
        CodeFormat.Ean13 => BarcodeFormat.EAN_13,
        CodeFormat.Ean8 => BarcodeFormat.EAN_8,
        CodeFormat.UpcA => BarcodeFormat.UPC_A,
        CodeFormat.Itf => BarcodeFormat.ITF,
        CodeFormat.Codabar => BarcodeFormat.CODABAR,
        _ => BarcodeFormat.QR_CODE,
    };

    static string Friendly(CodeFormat f, Exception ex) => f switch
    {
        CodeFormat.Ean13 => "EAN-13 needs 12 digits (check digit is added) or 13 with a valid check digit.",
        CodeFormat.Ean8 => "EAN-8 needs 7 digits (check digit is added) or 8 with a valid check digit.",
        CodeFormat.UpcA => "UPC-A needs 11 digits (check digit is added) or 12 with a valid check digit.",
        CodeFormat.Itf => "ITF needs an even number of digits.",
        CodeFormat.Codabar => "Codabar takes digits and - $ : / . +, optionally wrapped in A–D start/stop letters.",
        CodeFormat.Code39 => "Code 39 takes capitals, digits and - . $ / + % space.",
        _ => string.IsNullOrEmpty(ex.Message) ? "This value cannot be encoded." : ex.Message,
    };
}
