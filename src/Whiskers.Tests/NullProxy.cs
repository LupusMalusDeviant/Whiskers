using System.Reflection;

namespace Whiskers.Tests;

/// <summary>A stand-in for any interface that does nothing: void and <see cref="Task"/>/<see cref="ValueTask"/> complete,
/// everything else returns its type's default (a <see cref="Task{T}"/> completed with it).</summary>
public class NullProxy<T> : DispatchProxy where T : class
{
    public static T Create() => DispatchProxy.Create<T, NullProxy<T>>();

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var type = method!.ReturnType;
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type == typeof(ValueTask)) return ValueTask.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = type.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
