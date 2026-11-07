using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SoundBoardKing.Audio.Speech.Rvc;

/// <summary>A tensor read from a checkpoint, as float32 whatever it was stored as.</summary>
internal sealed record TorchTensor(long[] Shape, float[] Data);

/// <summary>
/// Reads a PyTorch checkpoint (.pth / pytorch_model.bin, the zip format) without running any of it.
/// Python's pickle can call any function; this reader understands only plain data (numbers, strings,
/// lists, tuples, dictionaries) and tensors, and refuses everything else — the same rule as
/// torch.load(weights_only=True), so a file that tries to run code is rejected.
/// </summary>
internal static class TorchCheckpoint
{
    // The only classes a weights file needs.
    private static readonly HashSet<string> AllowedGlobals =
    [
        "collections OrderedDict",
        "torch._utils _rebuild_tensor_v2",
        "torch._utils _rebuild_parameter",
        "torch FloatStorage",
        "torch HalfStorage",
        "torch BFloat16Storage",
        "torch DoubleStorage",
        "torch LongStorage",
        "torch IntStorage",
    ];

    /// <summary>The unpickled object: dictionaries, lists, strings, numbers and <see cref="TorchTensor"/>.</summary>
    /// <exception cref="InvalidDataException">Not a checkpoint, or it contains something other than data.</exception>
    public static object? Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var pickle = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/data.pkl", StringComparison.Ordinal) || e.FullName == "data.pkl")
            ?? throw new InvalidDataException("Il file non è un modello PyTorch (manca data.pkl).");
        string prefix = pickle.FullName[..^"data.pkl".Length];
        byte[] data;
        using (var stream = pickle.Open())
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            data = memory.ToArray();
        }
        var storages = new Dictionary<string, float[]>();
        return new Unpickler(data, key =>
        {
            if (!storages.TryGetValue(key.Key, out var floats))
            {
                var entry = zip.GetEntry(prefix + "data/" + key.Key)
                    ?? throw new InvalidDataException($"Nel modello manca il blocco di dati {key.Key}.");
                floats = storages[key.Key] = ReadStorage(entry, key.Type);
            }
            return floats;
        }).Run();
    }

    /// <summary>The tensors of a dictionary (a state dict), by name.</summary>
    public static Dictionary<string, TorchTensor> Tensors(object? dictionary) =>
        dictionary is Dictionary<object, object?> map
            ? map.Where(p => p.Key is string && p.Value is TorchTensor)
                .ToDictionary(p => (string)p.Key, p => (TorchTensor)p.Value!)
            : throw new InvalidDataException("Il modello non contiene un elenco di pesi.");

    private static float[] ReadStorage(ZipArchiveEntry entry, string type)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        stream.CopyTo(memory);
        var bytes = memory.GetBuffer().AsSpan(0, (int)memory.Length);
        switch (type)
        {
            case "FloatStorage":
            {
                var result = new float[bytes.Length / 4];
                for (int i = 0; i < result.Length; i++)
                    result[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
                return result;
            }
            case "HalfStorage":
            {
                var result = new float[bytes.Length / 2];
                for (int i = 0; i < result.Length; i++)
                    result[i] = (float)BinaryPrimitives.ReadHalfLittleEndian(bytes[(i * 2)..]);
                return result;
            }
            case "BFloat16Storage":
            {
                var result = new float[bytes.Length / 2];
                for (int i = 0; i < result.Length; i++)
                    result[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]) << 16);
                return result;
            }
            case "DoubleStorage":
            {
                var result = new float[bytes.Length / 8];
                for (int i = 0; i < result.Length; i++)
                    result[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes[(i * 8)..]);
                return result;
            }
            case "LongStorage":
            {
                var result = new float[bytes.Length / 8];
                for (int i = 0; i < result.Length; i++)
                    result[i] = BinaryPrimitives.ReadInt64LittleEndian(bytes[(i * 8)..]);
                return result;
            }
            case "IntStorage":
            {
                var result = new float[bytes.Length / 4];
                for (int i = 0; i < result.Length; i++)
                    result[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 4)..]);
                return result;
            }
            default:
                throw new InvalidDataException($"Tipo di dati non supportato nel modello: {type}.");
        }
    }

    private sealed record StorageKey(string Key, string Type);

    private sealed record Global(string Name);

    private sealed class Mark;

    /// <summary>A pickle virtual machine for data only (protocols 2-5).</summary>
    private sealed class Unpickler(byte[] data, Func<StorageKey, float[]> storage)
    {
        private readonly Stack<object?> _stack = new();
        private readonly Dictionary<int, object?> _memo = [];
        private int _position;

        public object? Run()
        {
            while (true)
            {
                byte op = data[_position++];
                switch (op)
                {
                    case 0x80: _position++; break; // PROTO
                    case 0x95: _position += 8; break; // FRAME
                    case (byte)'.': return _stack.Pop(); // STOP
                    case (byte)'(': _stack.Push(new Mark()); break; // MARK
                    case (byte)'}': _stack.Push(new Dictionary<object, object?>()); break; // EMPTY_DICT
                    case (byte)']': _stack.Push(new List<object?>()); break; // EMPTY_LIST
                    case (byte)')': _stack.Push(Array.Empty<object?>()); break; // EMPTY_TUPLE
                    case (byte)'N': _stack.Push(null); break;
                    case 0x88: _stack.Push(true); break;
                    case 0x89: _stack.Push(false); break;
                    case (byte)'K': _stack.Push((long)data[_position++]); break; // BININT1
                    case (byte)'M': _stack.Push((long)BinaryPrimitives.ReadUInt16LittleEndian(Take(2))); break; // BININT2
                    case (byte)'J': _stack.Push((long)BinaryPrimitives.ReadInt32LittleEndian(Take(4))); break; // BININT
                    case 0x8a: // LONG1
                    {
                        int length = data[_position++];
                        var bytes = Take(length);
                        long value = 0;
                        for (int i = length - 1; i >= 0; i--)
                            value = (value << 8) | bytes[i];
                        if (length is > 0 and < 8 && (bytes[length - 1] & 0x80) != 0)
                            value -= 1L << (8 * length);
                        _stack.Push(value);
                        break;
                    }
                    case (byte)'G': _stack.Push(BinaryPrimitives.ReadDoubleBigEndian(Take(8))); break; // BINFLOAT
                    case (byte)'X': _stack.Push(Text(BinaryPrimitives.ReadInt32LittleEndian(Take(4)))); break; // BINUNICODE
                    case 0x8c: _stack.Push(Text(data[_position++])); break; // SHORT_BINUNICODE
                    case 0x8d: _stack.Push(Text((int)BinaryPrimitives.ReadInt64LittleEndian(Take(8)))); break; // BINUNICODE8
                    case (byte)'U': _stack.Push(Encoding.Latin1.GetString(Take(data[_position++]))); break; // SHORT_BINSTRING
                    case (byte)'T': _stack.Push(Encoding.Latin1.GetString(Take(BinaryPrimitives.ReadInt32LittleEndian(Take(4))))); break;
                    case (byte)'C': _stack.Push(Take(data[_position++]).ToArray()); break; // SHORT_BINBYTES
                    case (byte)'B': _stack.Push(Take(BinaryPrimitives.ReadInt32LittleEndian(Take(4))).ToArray()); break; // BINBYTES
                    case (byte)'t': _stack.Push(PopToMark().ToArray()); break; // TUPLE
                    case 0x85: _stack.Push(new[] { _stack.Pop() }); break; // TUPLE1
                    case 0x86: { var b = _stack.Pop(); var a = _stack.Pop(); _stack.Push(new[] { a, b }); break; } // TUPLE2
                    case 0x87: { var c = _stack.Pop(); var b = _stack.Pop(); var a = _stack.Pop(); _stack.Push(new[] { a, b, c }); break; }
                    case (byte)'l': _stack.Push(PopToMark()); break; // LIST
                    case (byte)'a': { var item = _stack.Pop(); ((List<object?>)_stack.Peek()!).Add(item); break; } // APPEND
                    case (byte)'e': { var items = PopToMark(); ((List<object?>)_stack.Peek()!).AddRange(items); break; } // APPENDS
                    case (byte)'d': // DICT
                    {
                        var items = PopToMark();
                        var dict = new Dictionary<object, object?>();
                        for (int i = 0; i + 1 < items.Count; i += 2)
                            dict[Key(items[i])] = items[i + 1];
                        _stack.Push(dict);
                        break;
                    }
                    case (byte)'s': { var value = _stack.Pop(); var key = _stack.Pop(); Dict()[Key(key)] = value; break; } // SETITEM
                    case (byte)'u': // SETITEMS
                    {
                        var items = PopToMark();
                        var dict = Dict();
                        for (int i = 0; i + 1 < items.Count; i += 2)
                            dict[Key(items[i])] = items[i + 1];
                        break;
                    }
                    case (byte)'q': _memo[data[_position++]] = _stack.Peek(); break; // BINPUT
                    case (byte)'r': _memo[BinaryPrimitives.ReadInt32LittleEndian(Take(4))] = _stack.Peek(); break; // LONG_BINPUT
                    case 0x94: _memo[_memo.Count] = _stack.Peek(); break; // MEMOIZE
                    case (byte)'h': _stack.Push(_memo[data[_position++]]); break; // BINGET
                    case (byte)'j': _stack.Push(_memo[BinaryPrimitives.ReadInt32LittleEndian(Take(4))]); break; // LONG_BINGET
                    case (byte)'c': // GLOBAL "module\nname\n"
                    {
                        string module = Line();
                        string name = Line();
                        _stack.Push(Allow(module + " " + name));
                        break;
                    }
                    case 0x93: // STACK_GLOBAL
                    {
                        var name = (string)_stack.Pop()!;
                        var module = (string)_stack.Pop()!;
                        _stack.Push(Allow(module + " " + name));
                        break;
                    }
                    case (byte)'Q': _stack.Push(PersistentLoad(_stack.Pop())); break; // BINPERSID
                    case (byte)'R': // REDUCE
                    {
                        var args = (object?[])_stack.Pop()!;
                        var callable = (Global)_stack.Pop()!;
                        _stack.Push(Reduce(callable, args));
                        break;
                    }
                    case (byte)'b': _stack.Pop(); break; // BUILD: the state of an OrderedDict or a parameter, not needed
                    default:
                        throw new InvalidDataException($"Il modello contiene un'istruzione pickle non ammessa (0x{op:x2}).");
                }
            }
        }

        private static Global Allow(string name) => AllowedGlobals.Contains(name)
            ? new Global(name)
            : throw new InvalidDataException($"Il modello contiene codice o oggetti non ammessi ({name}): non lo carico.");

        private object PersistentLoad(object? id)
        {
            // ('storage', torch.HalfStorage, '0', 'cpu', numel)
            if (id is not object?[] { Length: >= 3 } parts || parts[0] is not "storage" || parts[1] is not Global type || parts[2] is not string key)
                throw new InvalidDataException("Riferimento a dati del modello non valido.");
            return new StorageKey(key, type.Name[(type.Name.IndexOf(' ') + 1)..]);
        }

        private object? Reduce(Global callable, object?[] args)
        {
            switch (callable.Name)
            {
                case "collections OrderedDict":
                    return new Dictionary<object, object?>();
                case "torch._utils _rebuild_parameter":
                    return args[0];
                case "torch._utils _rebuild_tensor_v2":
                {
                    // (storage, storage_offset, size, stride, requires_grad, backward_hooks[, metadata])
                    var key = (StorageKey)args[0]!;
                    long offset = (long)args[1]!;
                    long[] shape = ((object?[])args[2]!).Select(x => (long)x!).ToArray();
                    long[] stride = ((object?[])args[3]!).Select(x => (long)x!).ToArray();
                    long count = shape.Aggregate(1L, (a, b) => a * b);
                    long expected = 1;
                    for (int i = shape.Length - 1; i >= 0; i--)
                    {
                        if (shape[i] != 1 && stride[i] != expected)
                            throw new InvalidDataException("Il modello contiene un tensore non contiguo: non supportato.");
                        expected *= shape[i];
                    }
                    float[] all = storage(key);
                    if (offset + count > all.Length)
                        throw new InvalidDataException("Il modello è troncato.");
                    return new TorchTensor(shape, all.AsSpan((int)offset, (int)count).ToArray());
                }
                default:
                    throw new InvalidDataException($"Oggetto non ammesso nel modello: {callable.Name}.");
            }
        }

        private Dictionary<object, object?> Dict() =>
            _stack.Peek() as Dictionary<object, object?> ?? throw new InvalidDataException("Struttura del modello non valida.");

        private static object Key(object? key) => key ?? throw new InvalidDataException("Chiave vuota nel modello.");

        private List<object?> PopToMark()
        {
            var items = new List<object?>();
            while (_stack.Peek() is not Mark)
                items.Add(_stack.Pop());
            _stack.Pop();
            items.Reverse();
            return items;
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _position + count > data.Length)
                throw new InvalidDataException("Il modello è troncato.");
            var span = data.AsSpan(_position, count);
            _position += count;
            return span;
        }

        private string Text(int length) => Encoding.UTF8.GetString(Take(length));

        private string Line()
        {
            int end = Array.IndexOf(data, (byte)'\n', _position);
            string line = Encoding.ASCII.GetString(data, _position, end - _position);
            _position = end + 1;
            return line;
        }
    }
}
