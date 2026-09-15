using System;
using UnityEngine;

/// <summary>
/// Owns the high-resolution pixel state for one cloud.
///
/// The cloud is simulated as one continuous mass of pixels. Unity Tilemap cells are
/// only a rendering layer which displays 8x8 slices of this mask.
/// </summary>
[Serializable]
public sealed class CloudMass
{
    private readonly bool[,] cloudMask;
    private readonly bool[,] blockedMask;

    public int Width { get; }
    public int Height { get; }
    public int TargetPixelCount { get; }
    public int Seed { get; }

    public bool[,] CloudMask => cloudMask;
    public bool[,] BlockedMask => blockedMask;

    public CloudMass(int width, int height, int targetPixelCount, int seed)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        Width = width;
        Height = height;
        TargetPixelCount = Mathf.Clamp(targetPixelCount, 0, width * height);
        Seed = seed;

        cloudMask = new bool[width, height];
        blockedMask = new bool[width, height];
    }

    public bool IsInside(int x, int y)
    {
        return x >= 0 && y >= 0 && x < Width && y < Height;
    }

    public bool HasCloud(int x, int y)
    {
        return IsInside(x, y) && cloudMask[x, y];
    }

    public bool IsBlocked(int x, int y)
    {
        return IsInside(x, y) && blockedMask[x, y];
    }

    public void SetCloud(int x, int y, bool occupied)
    {
        if (!IsInside(x, y))
            return;

        if (occupied && blockedMask[x, y])
            return;

        cloudMask[x, y] = occupied;
    }

    public void SetBlocked(int x, int y, bool blocked)
    {
        if (!IsInside(x, y))
            return;

        blockedMask[x, y] = blocked;

        if (blocked)
            cloudMask[x, y] = false;
    }

    public int CountCloudPixels()
    {
        int count = 0;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (cloudMask[x, y])
                    count++;
            }
        }

        return count;
    }

    public int CountBlockedPixels()
    {
        int count = 0;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (blockedMask[x, y])
                    count++;
            }
        }

        return count;
    }

    public int AvailableCapacity => (Width * Height) - CountBlockedPixels();

    public void ClearCloud()
    {
        Array.Clear(cloudMask, 0, cloudMask.Length);
    }

    /// <summary>
    /// Removes cloud inside the supplied pixel rectangle and permanently blocks those
    /// pixels. Returns the amount of cloud material removed by the cut.
    /// </summary>
    public int BlockAndClear(RectInt pixelArea)
    {
        int minX = Mathf.Clamp(pixelArea.xMin, 0, Width);
        int minY = Mathf.Clamp(pixelArea.yMin, 0, Height);
        int maxX = Mathf.Clamp(pixelArea.xMax, 0, Width);
        int maxY = Mathf.Clamp(pixelArea.yMax, 0, Height);

        int removed = 0;

        for (int y = minY; y < maxY; y++)
        {
            for (int x = minX; x < maxX; x++)
            {
                if (cloudMask[x, y])
                    removed++;

                cloudMask[x, y] = false;
                blockedMask[x, y] = true;
            }
        }

        return removed;
    }
}
