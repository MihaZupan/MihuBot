namespace MihuBot.Helpers;

public sealed class Box<T>
    where T : struct
{
    public T Value { get; set; }
}
