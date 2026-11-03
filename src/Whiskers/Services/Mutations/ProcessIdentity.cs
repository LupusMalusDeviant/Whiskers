namespace Whiskers.Services.Mutations;

/// <summary>
/// R1-39: the identity of this Whiskers process, written on every run and update that is put on record before it starts
/// (<c>TaskRunHistory</c>, <c>UpdateHistory</c>). At the next start an open row with another identity was cut off by a
/// stop; one with this identity is running now. An identity, not the clock: a clock set back or forward cannot make a
/// running row look interrupted, nor an interrupted one look current.
/// </summary>
public static class ProcessIdentity
{
    public static string Current { get; } = Guid.NewGuid().ToString("N");
}
