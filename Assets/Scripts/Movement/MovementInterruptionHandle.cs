using System;

/// <summary>
/// Opaque handle returned when an outside system temporarily blocks player
/// movement. Keep the handle and call Release/Dispose when that system is done.
///
/// Multiple handles may exist at once. Movement resumes only after the last
/// active handle is released, so Combat and a random event cannot accidentally
/// resume movement while the other still owns an interruption.
/// </summary>
public sealed class MovementInterruptionHandle : IDisposable
{
    private PlayerGridController owner;

    internal int Id { get; }

    public string Reason { get; }

    public bool IsReleased
    {
        get { return owner == null; }
    }


    internal MovementInterruptionHandle(
        PlayerGridController owner,
        int id,
        string reason)
    {
        this.owner = owner;
        Id = id;
        Reason = string.IsNullOrWhiteSpace(reason)
            ? "Unspecified"
            : reason;
    }


    /// <summary>
    /// Releases this interruption once. Safe to call more than once.
    /// </summary>
    public void Release()
    {
        if (owner == null)
        {
            return;
        }

        PlayerGridController currentOwner = owner;
        owner = null;

        currentOwner.ReleaseMovementInterruption(this);
    }


    public void Dispose()
    {
        Release();
    }


    internal void MarkReleasedByOwner()
    {
        owner = null;
    }
}
