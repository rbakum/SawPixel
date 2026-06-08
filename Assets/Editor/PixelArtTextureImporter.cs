using UnityEditor;
using UnityEngine;

// Any texture dropped into the project (a "stamp" source) gets crisp pixel-art
// import settings automatically, so it never comes in squashed or blurred:
//   * NPOT = None      → 32x45 stays 32x45 (no power-of-2 squish to square)
//   * Point filter     → no bilinear half-tone interpolation
//   * no mipmaps       → no extra blur
//   * uncompressed     → exact pixel colors, no DXT artifacts
//   * readable         → the game can read pixels directly
// Only runs on FIRST import (importSettingsMissing) so manual tweaks are kept.
public class PixelArtTextureImporter : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        var ti = (TextureImporter)assetImporter;
        if (!ti.importSettingsMissing) return;          // respect manual settings

        ti.textureType = TextureImporterType.Default;
        ti.npotScale = TextureImporterNPOTScale.None;
        ti.filterMode = FilterMode.Point;
        ti.mipmapEnabled = false;
        ti.isReadable = true;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.maxTextureSize = 8192;
    }
}
