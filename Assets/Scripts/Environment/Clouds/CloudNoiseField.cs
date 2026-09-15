using System;
using UnityEngine;

/// <summary>
/// Produces the scalar field that the cloud tries to occupy.
///
/// The field is intentionally dominated by a rounded support body. Lobes, noise,
/// domain warp, and a curl-noise-like flow only distort that body. This gives small
/// 2x2 to 8x8 tile clouds a soft cloud silhouette instead of a noisy island shape.
/// </summary>
public sealed class CloudNoiseField
{
    private readonly int width;
    private readonly int height;
    private readonly float[,] values;

    private readonly float roundness;
    private readonly float lobeAmount;
    private readonly float lobeSpread;
    private readonly float largeNoiseScale;
    private readonly float largeNoiseStrength;
    private readonly float mediumNoiseScale;
    private readonly float mediumNoiseStrength;
    private readonly float warpScale;
    private readonly float warpStrength;

    private readonly Vector2[] lobeSeeds;

    private readonly float largeOffsetX;
    private readonly float largeOffsetY;
    private readonly float mediumOffsetX;
    private readonly float mediumOffsetY;
    private readonly float warpOffsetX;
    private readonly float warpOffsetY;
    private readonly float flowOffsetX;
    private readonly float flowOffsetY;

    public int Width => width;
    public int Height => height;
    public float CurrentTime { get; private set; }

    public CloudNoiseField(
        int width,
        int height,
        int seed,
        int lobeCount,
        float lobeSpread,
        float roundness,
        float lobeAmount,
        float largeNoiseScale,
        float largeNoiseStrength,
        float mediumNoiseScale,
        float mediumNoiseStrength,
        float warpScale,
        float warpStrength)
    {
        this.width = Mathf.Max(1, width);
        this.height = Mathf.Max(1, height);
        this.roundness = Mathf.Clamp01(roundness);
        this.lobeAmount = Mathf.Clamp01(lobeAmount);
        this.lobeSpread = Mathf.Clamp(lobeSpread, 0.05f, 0.48f);
        this.largeNoiseScale = Mathf.Max(0.01f, largeNoiseScale);
        this.largeNoiseStrength = Mathf.Max(0f, largeNoiseStrength);
        this.mediumNoiseScale = Mathf.Max(0.01f, mediumNoiseScale);
        this.mediumNoiseStrength = Mathf.Max(0f, mediumNoiseStrength);
        this.warpScale = Mathf.Max(0.01f, warpScale);
        this.warpStrength = Mathf.Max(0f, warpStrength);

        values = new float[this.width, this.height];

        System.Random random = new System.Random(seed);

        largeOffsetX = NextRange(random, -5000f, 5000f);
        largeOffsetY = NextRange(random, -5000f, 5000f);
        mediumOffsetX = NextRange(random, -5000f, 5000f);
        mediumOffsetY = NextRange(random, -5000f, 5000f);
        warpOffsetX = NextRange(random, -5000f, 5000f);
        warpOffsetY = NextRange(random, -5000f, 5000f);
        flowOffsetX = NextRange(random, -5000f, 5000f);
        flowOffsetY = NextRange(random, -5000f, 5000f);

        lobeSeeds = CreateLobeSeeds(random, Mathf.Max(1, lobeCount));

        UpdateTime(0f, 0f, 0f, 0f);
    }

    public float GetValue(int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
            return 0f;

        return values[x, y];
    }

    /// <summary>
    /// Rebuilds the desired scalar field for the supplied animation time.
    ///
    /// Drift moves through the noise field, warp changes the domain deformation, and
    /// flowStrength adds a small curl-like advection offset. The cloud mask itself is
    /// not changed here. CloudSolver gradually moves material toward this new field.
    /// </summary>
    public void UpdateTime(float time, float driftSpeed, float warpSpeed, float flowStrength)
    {
        CurrentTime = time;

        float minValue = float.PositiveInfinity;
        float maxValue = float.NegativeInfinity;

        for (int y = 0; y < height; y++)
        {
            float ny = height <= 1 ? 0.5f : y / (float)(height - 1);

            for (int x = 0; x < width; x++)
            {
                float nx = width <= 1 ? 0.5f : x / (float)(width - 1);
                float value = EvaluateField(nx, ny, time, driftSpeed, warpSpeed, flowStrength);

                values[x, y] = value;
                minValue = Mathf.Min(minValue, value);
                maxValue = Mathf.Max(maxValue, value);
            }
        }

        // Keep the solver-facing field in a stable 0..1 range even while the animated
        // offsets change. The exact mass solver then chooses the strongest region.
        float range = Mathf.Max(0.0001f, maxValue - minValue);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                values[x, y] = Mathf.Clamp01((values[x, y] - minValue) / range);
        }
    }

    private float EvaluateField(
        float nx,
        float ny,
        float time,
        float driftSpeed,
        float warpSpeed,
        float flowStrength)
    {
        float driftTime = time * driftSpeed;
        float warpTime = time * warpSpeed;

        // Curl-like flow bends the sampling position instead of directly moving cloud
        // pixels. The solver then follows this changing desired shape while conserving
        // material one boundary swap at a time.
        Vector2 flow = SampleCurlFlow(nx, ny, warpTime);
        float flowScale = Mathf.Clamp(flowStrength, 0f, 1f) * 0.055f;

        float flowedX = nx + (flow.x * flowScale);
        float flowedY = ny + (flow.y * flowScale);

        float warpX = SignedPerlin(
            (flowedX * warpScale) + warpOffsetX + (warpTime * 0.13f),
            (flowedY * warpScale) + warpOffsetY - (warpTime * 0.09f));

        float warpY = SignedPerlin(
            (flowedX * warpScale) + warpOffsetY + 41.73f - (warpTime * 0.08f),
            (flowedY * warpScale) + warpOffsetX - 19.41f + (warpTime * 0.12f));

        float sampleX = flowedX + (warpX * warpStrength);
        float sampleY = flowedY + (warpY * warpStrength);

        float body = EvaluateRoundedBody(sampleX, sampleY);
        float lobes = EvaluateLobeInfluence(sampleX, sampleY);

        float largeNoise = SignedPerlin(
            (sampleX * largeNoiseScale) + largeOffsetX + (driftTime * 0.15f),
            (sampleY * largeNoiseScale) + largeOffsetY + (driftTime * 0.07f));

        float mediumNoise = SignedPerlin(
            (sampleX * mediumNoiseScale) + mediumOffsetX - (driftTime * 0.11f),
            (sampleY * mediumNoiseScale) + mediumOffsetY + (driftTime * 0.17f));

        // Roundness controls how strongly the smooth body dominates all other terms.
        // At the default value this is a rounded cloud with lumpy edges, not a circle
        // and not a thresholded noise island.
        float bodyWeight = Mathf.Lerp(1.05f, 2.65f, roundness);
        float noiseDamping = Mathf.Lerp(1f, 0.58f, roundness);

        return
            (body * bodyWeight) +
            (lobes * lobeAmount * 0.95f) +
            (largeNoise * largeNoiseStrength * noiseDamping) +
            (mediumNoise * mediumNoiseStrength * noiseDamping);
    }

    private float EvaluateRoundedBody(float nx, float ny)
    {
        float dx = (nx - 0.5f) * 2f;
        float dy = (ny - 0.5f) * 2f;
        float distance = Mathf.Sqrt((dx * dx) + (dy * dy));

        // Keep a broad high-value centre and then roll off smoothly. This makes the
        // exact-mass threshold naturally produce a soft ellipse-like body that remains
        // inside the configured tile bounds.
        float body = 1f - Mathf.SmoothStep(0.18f, 1.04f, distance);
        return Mathf.Clamp01(body);
    }

    private float EvaluateLobeInfluence(float nx, float ny)
    {
        float sigma = Mathf.Clamp(lobeSpread * 0.42f, 0.035f, 0.24f);
        float denominator = 2f * sigma * sigma;
        float strongest = 0f;

        for (int i = 0; i < lobeSeeds.Length; i++)
        {
            float dx = nx - lobeSeeds[i].x;
            float dy = ny - lobeSeeds[i].y;
            float distanceSq = (dx * dx) + (dy * dy);
            float influence = Mathf.Exp(-distanceSq / denominator);
            strongest = Mathf.Max(strongest, influence);
        }

        return strongest;
    }

    private Vector2 SampleCurlFlow(float nx, float ny, float time)
    {
        const float epsilon = 0.0125f;
        const float flowFrequency = 1.65f;

        float left = SampleFlowPotential(nx - epsilon, ny, time, flowFrequency);
        float right = SampleFlowPotential(nx + epsilon, ny, time, flowFrequency);
        float down = SampleFlowPotential(nx, ny - epsilon, time, flowFrequency);
        float up = SampleFlowPotential(nx, ny + epsilon, time, flowFrequency);

        float dPdx = (right - left) / (2f * epsilon);
        float dPdy = (up - down) / (2f * epsilon);

        Vector2 curl = new Vector2(dPdy, -dPdx);
        float magnitude = curl.magnitude;

        if (magnitude > 1f)
            curl /= magnitude;

        return curl;
    }

    private float SampleFlowPotential(float nx, float ny, float time, float frequency)
    {
        return Mathf.PerlinNoise(
            (nx * frequency) + flowOffsetX + (time * 0.07f),
            (ny * frequency) + flowOffsetY - (time * 0.05f));
    }

    private Vector2[] CreateLobeSeeds(System.Random random, int count)
    {
        Vector2[] result = new Vector2[count];

        result[0] = new Vector2(
            0.5f + NextRange(random, -0.035f, 0.035f),
            0.5f + NextRange(random, -0.035f, 0.035f));

        for (int i = 1; i < count; i++)
        {
            float angle = NextRange(random, 0f, Mathf.PI * 2f);
            float radius = Mathf.Sqrt(NextRange(random, 0.05f, 1f)) * lobeSpread;

            result[i] = new Vector2(
                Mathf.Clamp(0.5f + (Mathf.Cos(angle) * radius), 0.12f, 0.88f),
                Mathf.Clamp(0.5f + (Mathf.Sin(angle) * radius), 0.12f, 0.88f));
        }

        return result;
    }

    private static float SignedPerlin(float x, float y)
    {
        return (Mathf.PerlinNoise(x, y) * 2f) - 1f;
    }

    private static float NextRange(System.Random random, float min, float max)
    {
        return min + ((float)random.NextDouble() * (max - min));
    }
}
