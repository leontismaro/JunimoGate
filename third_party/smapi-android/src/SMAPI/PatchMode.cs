namespace StardewModdingAPI;

/// <summary>Indicates how an image should be patched.</summary>
public enum PatchMode
{
    /// <summary>Erase the original content within the area before drawing the new content.</summary>
    Replace,

    /// <summary>Draw the new content over the original content, so the original content shows through any transparent or semi-transparent pixels.</summary>
    Overlay,

    /// <summary>Apply the new content over the original content as a transparency mask.</summary>
    /// <remarks>This subtracts the alpha value of each pixel in the new content from the corresponding pixel in the original content. Colors in the new content are ignored. For example, a fully opaque pixel in the new content will result in a fully transparent pixel in the final image.</remarks>
    Mask
}
