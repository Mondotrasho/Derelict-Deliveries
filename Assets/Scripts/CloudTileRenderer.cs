using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Displays the global cloud mask as 8x8 Tilemap slices.
///
/// V2 keeps one reusable runtime texture/sprite/tile per Tilemap cell. This matters
/// for animation: a drifting cloud can create many unique 8x8 masks over time, so a
/// permanent cache keyed by every historical mask would grow forever.
/// </summary>
public sealed class CloudTileRenderer : IDisposable
{
    private readonly Tilemap tilemap;
    private readonly int tilePixelSize;
    private readonly int pixelsPerUnit;
    private readonly Color32 cloudColour;

    private readonly Dictionary<Vector3Int, CellVisual> visuals = new Dictionary<Vector3Int, CellVisual>();
    private readonly Dictionary<Vector3Int, ulong> renderedMasks = new Dictionary<Vector3Int, ulong>();

    public CloudTileRenderer(Tilemap tilemap, int tilePixelSize, int pixelsPerUnit, Color cloudColour)
    {
        this.tilemap = tilemap != null ? tilemap : throw new ArgumentNullException(nameof(tilemap));
        this.tilePixelSize = Mathf.Clamp(tilePixelSize, 1, 8);
        this.pixelsPerUnit = Mathf.Max(1, pixelsPerUnit);
        this.cloudColour = cloudColour;
    }

    public void RenderAll(CloudMass mass, Vector3Int tileOrigin, Vector2Int tileSize)
    {
        if (mass == null)
            return;

        ClearRenderedTiles();
        renderedMasks.Clear();
        UpdateChangedTiles(mass, tileOrigin, tileSize);
    }

    /// <summary>
    /// Updates only cells whose 8x8 pixel mask changed since the previous render.
    /// Existing Texture2D, Sprite, and Tile objects are reused.
    /// </summary>
    public int UpdateChangedTiles(CloudMass mass, Vector3Int tileOrigin, Vector2Int tileSize)
    {
        if (mass == null)
            return 0;

        int changed = 0;

        for (int tileY = 0; tileY < tileSize.y; tileY++)
        {
            for (int tileX = 0; tileX < tileSize.x; tileX++)
            {
                Vector3Int cell = new Vector3Int(
                    tileOrigin.x + tileX,
                    tileOrigin.y + tileY,
                    tileOrigin.z);

                ulong mask = ExtractMask(
                    mass,
                    tileX * tilePixelSize,
                    tileY * tilePixelSize);

                if (renderedMasks.TryGetValue(cell, out ulong previousMask) && previousMask == mask)
                    continue;

                renderedMasks[cell] = mask;
                changed++;

                if (mask == 0UL)
                {
                    tilemap.SetTile(cell, null);
                    continue;
                }

                CellVisual visual = GetOrCreateVisual(cell);
                UpdateTexture(visual.Texture, mask);

                // A cell may have become empty on a previous frame. Re-place its
                // existing Tile object rather than allocating a new one.
                if (tilemap.GetTile(cell) != visual.Tile)
                    tilemap.SetTile(cell, visual.Tile);
            }
        }

        return changed;
    }

    public ulong ExtractMask(CloudMass mass, int pixelStartX, int pixelStartY)
    {
        ulong mask = 0UL;
        int bit = 0;

        for (int y = 0; y < tilePixelSize; y++)
        {
            for (int x = 0; x < tilePixelSize; x++)
            {
                if (mass.HasCloud(pixelStartX + x, pixelStartY + y))
                    mask |= 1UL << bit;

                bit++;
            }
        }

        return mask;
    }

    public void Dispose()
    {
        ClearRenderedTiles();
        renderedMasks.Clear();

        foreach (CellVisual visual in visuals.Values)
            DestroyRuntimeObject(visual.Tile);

        foreach (CellVisual visual in visuals.Values)
            DestroyRuntimeObject(visual.Sprite);

        foreach (CellVisual visual in visuals.Values)
            DestroyRuntimeObject(visual.Texture);

        visuals.Clear();
    }

    private CellVisual GetOrCreateVisual(Vector3Int cell)
    {
        if (visuals.TryGetValue(cell, out CellVisual existing))
            return existing;

        Texture2D texture = new Texture2D(
            tilePixelSize,
            tilePixelSize,
            TextureFormat.RGBA32,
            false)
        {
            name = $"CloudCellTexture_{cell.x}_{cell.y}",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };

        Sprite sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, tilePixelSize, tilePixelSize),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit,
            0,
            SpriteMeshType.FullRect);

        sprite.name = $"CloudCellSprite_{cell.x}_{cell.y}";
        sprite.hideFlags = HideFlags.HideAndDontSave;

        Tile tile = ScriptableObject.CreateInstance<Tile>();
        tile.name = $"CloudCellTile_{cell.x}_{cell.y}";
        tile.sprite = sprite;
        tile.color = Color.white;
        tile.colliderType = Tile.ColliderType.None;
        tile.hideFlags = HideFlags.HideAndDontSave;

        CellVisual created = new CellVisual(texture, sprite, tile);
        visuals.Add(cell, created);
        return created;
    }

    private void UpdateTexture(Texture2D texture, ulong mask)
    {
        Color32 transparent = new Color32(0, 0, 0, 0);
        Color32[] pixels = new Color32[tilePixelSize * tilePixelSize];

        for (int bit = 0; bit < pixels.Length; bit++)
            pixels[bit] = ((mask >> bit) & 1UL) != 0UL ? cloudColour : transparent;

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
    }

    private void ClearRenderedTiles()
    {
        foreach (Vector3Int cell in renderedMasks.Keys)
        {
            if (tilemap != null)
                tilemap.SetTile(cell, null);
        }
    }

    private static void DestroyRuntimeObject(UnityEngine.Object obj)
    {
        if (obj == null)
            return;

#if UNITY_EDITOR
        if (!Application.isPlaying)
            UnityEngine.Object.DestroyImmediate(obj);
        else
            UnityEngine.Object.Destroy(obj);
#else
        UnityEngine.Object.Destroy(obj);
#endif
    }

    private sealed class CellVisual
    {
        public Texture2D Texture { get; }
        public Sprite Sprite { get; }
        public Tile Tile { get; }

        public CellVisual(Texture2D texture, Sprite sprite, Tile tile)
        {
            Texture = texture;
            Sprite = sprite;
            Tile = tile;
        }
    }
}
