using System;

namespace ChestCounter;

/// <summary>
/// The chest artwork, embedded once in the program and reused by the report, the native overlay
/// and the web interface. Keeping the exact same PNG everywhere avoids the old hand-drawn SVGs
/// drifting apart from the app icon.
/// </summary>
public static class ChestArt
{
    private static readonly Lazy<byte[]> Payload = new(Load, isThreadSafe: true);

    /// <summary>Raw PNG bytes (transparent background).</summary>
    public static byte[] Bytes => Payload.Value;

    /// <summary>Data URI used by the self-contained HTML pages (no external file needed).</summary>
    public static string DataUri => "data:image/png;base64," + Convert.ToBase64String(Bytes);

    private static byte[] Load()
    {
        using var stream = typeof(ChestArt).Assembly.GetManifestResourceStream("ChestArt.png")
            ?? throw new InvalidOperationException("ChestArt.png is missing from the program resources.");
        using var memory = new System.IO.MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
