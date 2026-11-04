using System.Collections;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>A callback-scoped view: payload values are decoded only when requested.</summary>
internal sealed class TracePayloadValues(string[] names, Func<int, object?> read) : IReadOnlyDictionary<string, object?>
{
    public object? this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
    public IEnumerable<string> Keys => names;
    public IEnumerable<object?> Values => names.Select((_, index) => read(index));
    public int Count => names.Length;
    public bool ContainsKey(string key) => Array.IndexOf(names, key) >= 0;
    public bool TryGetValue(string key, out object? value)
    {
        var index = Array.IndexOf(names, key);
        value = index >= 0 ? read(index) : null;
        return index >= 0;
    }

    public bool IsFailedCall() => TryGetValue("HResult", out var value) && value switch
    {
        int number => number != 0,
        uint number => number != 0,
        long number => number != 0,
        ushort number => number != 0,
        short number => number != 0,
        byte number => number != 0,
        _ => false
    };

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var index = 0; index < names.Length; index++) yield return new(names[index], read(index));
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
