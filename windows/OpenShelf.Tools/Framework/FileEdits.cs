namespace OpenShelf.Tools;

/// <summary>
/// The edit waiting on each file an editor was given, which file is shown, and the "Same for all" switch (SPEC §2.4).
/// <list type="bullet">
/// <item>With Same for all on, one edit applies to every file: changing the shown file's edit carries it to the others
/// (<paramref>carry</paramref> maps it to each, so a crop stays "the top half" on files of any size).</item>
/// <item>Turning it on copies the shown file's edit to the rest; turning it off lets each keep its own.</item>
/// <item>A file counts as edited when its edit would change it; those files are saved, and the rest are counted in the toast.</item>
/// </list>
/// Edits are immutable values (records, lists replaced rather than changed).
/// </summary>
public sealed class FileEdits<T> where T : notnull
{
    private readonly T[] _edits;
    private readonly Func<int, T> _initial;
    private readonly Func<int, T, bool> _isEdited;
    private readonly Func<T, int, int, T>? _carry;
    private int _index;
    private bool _sameForAll;

    /// <param name="count">How many files.</param>
    /// <param name="initial">The unedited value for file <c>i</c>.</param>
    /// <param name="isEdited">Whether an edit would change file <c>i</c>.</param>
    /// <param name="carry">Maps the edit of file <c>from</c> onto file <c>to</c> when Same for all is on; null copies it as is.</param>
    /// <param name="sameForAll">Whether the switch starts on (it only matters with several files).</param>
    public FileEdits(int count, Func<int, T> initial, Func<int, T, bool> isEdited, Func<T, int, int, T>? carry = null, bool sameForAll = false)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        _initial = initial;
        _isEdited = isEdited;
        _carry = carry;
        _edits = Enumerable.Range(0, count).Select(initial).ToArray();
        _sameForAll = sameForAll && count > 1;
    }

    public int Count => _edits.Length;
    public bool IsBatch => _edits.Length > 1;

    /// <summary>Raised after the shown file, an edit or the switch changed.</summary>
    public event Action? Changed;

    /// <summary>The file being shown.</summary>
    public int Index
    {
        get => _index;
        set
        {
            var clamped = Math.Clamp(value, 0, _edits.Length - 1);
            if (clamped == _index) return;
            _index = clamped;
            Changed?.Invoke();
        }
    }

    public bool SameForAll
    {
        get => _sameForAll;
        set
        {
            value &= IsBatch;
            if (value == _sameForAll) return;
            _sameForAll = value;
            // Turning it on spreads the shown file's edit to the rest.
            if (value) PropagateFrom(_index);
            Changed?.Invoke();
        }
    }

    public T this[int index] => _edits[index];

    public IReadOnlyList<T> All => _edits;

    /// <summary>The shown file's edit. With Same for all on, setting it sets every file's.</summary>
    public T Current
    {
        get => _edits[_index];
        set
        {
            _edits[_index] = value;
            if (_sameForAll) PropagateFrom(_index);
            Changed?.Invoke();
        }
    }

    /// <summary>Changes only file <paramref name="index"/>, without carrying it anywhere (for loading a file's own state).</summary>
    public void SetOnly(int index, T value)
    {
        _edits[index] = value;
        Changed?.Invoke();
    }

    private void PropagateFrom(int source)
    {
        var edit = _edits[source];
        for (var i = 0; i < _edits.Length; i++)
            if (i != source) _edits[i] = _carry is null ? edit : _carry(edit, source, i);
    }

    /// <summary>Resets the shown file, or every file while they share one edit.</summary>
    public void Reset()
    {
        if (_sameForAll)
        {
            for (var i = 0; i < _edits.Length; i++) _edits[i] = _initial(i);
            Changed?.Invoke();
        }
        else
        {
            Current = _initial(_index);
        }
    }

    /// <summary>Resets every file, shared or not.</summary>
    public void ResetAll()
    {
        for (var i = 0; i < _edits.Length; i++) _edits[i] = _initial(i);
        Changed?.Invoke();
    }

    public bool IsEdited(int index) => _isEdited(index, _edits[index]);

    /// <summary>Whether each file has an edit waiting to be saved: the filmstrip's dots.</summary>
    public IReadOnlyList<bool> Edited => Enumerable.Range(0, _edits.Length).Select(IsEdited).ToArray();

    public int EditedCount => Enumerable.Range(0, _edits.Length).Count(IsEdited);

    /// <summary>The button text: "Crop 3 Images", "Crop 1 Image", "Crop Images" when nothing is edited yet, or
    /// <paramref name="single"/> with one file.</summary>
    public string PrimaryTitle(string verb, string single = "Apply", string noun = "Image")
    {
        if (!IsBatch) return single;
        return EditedCount switch
        {
            0 => $"{verb} {noun}s",
            1 => $"{verb} 1 {noun}",
            var n => $"{verb} {n} {noun}s",
        };
    }
}
