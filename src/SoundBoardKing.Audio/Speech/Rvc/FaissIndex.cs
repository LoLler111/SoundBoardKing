using System.IO.MemoryMappedFiles;
using System.Numerics.Tensors;
using System.Text;

namespace SoundBoardKing.Audio.Speech.Rvc;

/// <summary>
/// The feature index of an RVC model, a faiss "IVF…,Flat" file, searched as faiss does: the nearest
/// centroid (nprobe 1, as RVC saves it), then the nearest vectors in that list. The file is read in
/// place, mapped in memory, not loaded: an index is often 500 MB.
/// </summary>
internal sealed class FaissIndex : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly float[] _centroids;
    private readonly long[] _listOffsets;
    private readonly int[] _listSizes;

    /// <exception cref="InvalidDataException">Not a faiss IVF-Flat index.</exception>
    public FaissIndex(string path)
    {
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            Expect(reader, "IwFl", "L'indice non è un indice faiss IVF-Flat (quello che crea RVC).");
            (Dimension, _) = ReadHeader(reader);
            long lists = reader.ReadInt64();
            reader.ReadInt64(); // nprobe: RVC saves 1, and searches with that

            // The coarse quantizer: a flat L2 index with one centroid per list.
            string quantizer = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (quantizer is not ("IxF2" or "IxFI"))
                throw new InvalidDataException($"Quantizzatore dell'indice non supportato ({quantizer}).");
            ReadHeader(reader);
            long floats = reader.ReadInt64();
            if (floats != lists * Dimension)
                throw new InvalidDataException("L'indice ha un numero di centroidi inatteso.");
            _centroids = new float[floats];
            var bytes = reader.ReadBytes(checked((int)(floats * 4)));
            Buffer.BlockCopy(bytes, 0, _centroids, 0, bytes.Length);

            // Direct map: a type byte, then an id vector (empty for RVC) and, for hash tables, pairs.
            byte directMap = reader.ReadByte();
            reader.BaseStream.Seek(reader.ReadInt64() * 8, SeekOrigin.Current);
            if (directMap == 2)
                reader.BaseStream.Seek(reader.ReadInt64() * 16, SeekOrigin.Current);

            Expect(reader, "ilar", "Liste dell'indice in un formato non supportato.");
            if (reader.ReadInt64() != lists)
                throw new InvalidDataException("L'indice è incoerente.");
            long codeSize = reader.ReadInt64();
            if (codeSize != Dimension * 4)
                throw new InvalidDataException("L'indice non contiene vettori interi (Flat).");
            var sizes = new long[lists];
            string listType = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (listType == "full")
            {
                if (reader.ReadInt64() != lists)
                    throw new InvalidDataException("L'indice è incoerente.");
                for (int i = 0; i < lists; i++)
                    sizes[i] = reader.ReadInt64();
            }
            else if (listType == "sprs")
            {
                long pairs = reader.ReadInt64();
                for (long j = 0; j + 1 < pairs; j += 2)
                {
                    long list = reader.ReadInt64();
                    sizes[list] = reader.ReadInt64();
                }
            }
            else
            {
                throw new InvalidDataException($"Liste dell'indice in un formato non supportato ({listType}).");
            }

            _listOffsets = new long[lists];
            _listSizes = new int[lists];
            long position = reader.BaseStream.Position;
            for (int i = 0; i < lists; i++)
            {
                _listOffsets[i] = position;
                _listSizes[i] = checked((int)sizes[i]);
                position += sizes[i] * codeSize + sizes[i] * 8; // the vectors, then their ids
            }
            if (position > reader.BaseStream.Length)
                throw new InvalidDataException("L'indice è troncato.");
            Count = sizes.Sum();
        }
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public int Dimension { get; }
    public long Count { get; }

    /// <summary>
    /// The features blended with the index, as RVC does: the 8 nearest stored vectors, weighted by
    /// 1 / distance², replace <paramref name="rate"/> of each frame.
    /// </summary>
    /// <param name="features">frames x Dimension, changed in place.</param>
    public void Blend(float[] features, int frames, float rate, int neighbours = 8)
    {
        int d = Dimension;
        Parallel.For(0, frames, () => new float[d * 200], (frame, _, vectors) =>
        {
            var query = features.AsSpan(frame * d, d);
            int list = NearestCentroid(query);
            int size = _listSizes[list];
            if (vectors.Length < size * d)
                vectors = new float[size * d];
            _view.ReadArray(_listOffsets[list], vectors, 0, size * d);

            // The nearest few, by squared L2 distance as faiss reports it.
            Span<float> best = stackalloc float[neighbours];
            Span<int> which = stackalloc int[neighbours];
            best.Fill(float.PositiveInfinity);
            which.Fill(-1);
            for (int i = 0; i < size; i++)
            {
                float distance = TensorPrimitives.Distance(query, vectors.AsSpan(i * d, d));
                distance *= distance;
                if (distance >= best[^1])
                    continue;
                int at = neighbours - 1;
                while (at > 0 && best[at - 1] > distance)
                {
                    best[at] = best[at - 1];
                    which[at] = which[at - 1];
                    at--;
                }
                best[at] = distance;
                which[at] = i;
            }

            Span<float> blended = stackalloc float[d];
            blended.Clear();
            double total = 0;
            for (int k = 0; k < neighbours && which[k] >= 0; k++)
                total += 1.0 / Math.Max(1e-12, (double)best[k] * best[k]);
            if (total > 0)
            {
                for (int k = 0; k < neighbours && which[k] >= 0; k++)
                {
                    float weight = (float)(1.0 / Math.Max(1e-12, (double)best[k] * best[k]) / total);
                    TensorPrimitives.MultiplyAdd(vectors.AsSpan(which[k] * d, d), weight, blended, blended);
                }
                for (int j = 0; j < d; j++)
                    query[j] = blended[j] * rate + query[j] * (1 - rate);
            }
            return vectors;
        }, _ => { });
    }

    private int NearestCentroid(ReadOnlySpan<float> query)
    {
        int d = Dimension;
        int best = 0;
        float bestDistance = float.PositiveInfinity;
        for (int c = 0; c < _listSizes.Length; c++)
        {
            float distance = TensorPrimitives.Distance(query, _centroids.AsSpan(c * d, d));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = c;
            }
        }
        return best;
    }

    private static (int Dimension, long Count) ReadHeader(BinaryReader reader)
    {
        int d = reader.ReadInt32();
        long total = reader.ReadInt64();
        reader.ReadInt64();
        reader.ReadInt64();
        reader.ReadByte(); // is_trained
        int metric = reader.ReadInt32();
        if (metric > 1)
            reader.ReadSingle();
        if (metric != 1)
            throw new InvalidDataException("L'indice non usa la distanza L2 di RVC.");
        return (d, total);
    }

    private static void Expect(BinaryReader reader, string fourcc, string error)
    {
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != fourcc)
            throw new InvalidDataException(error);
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }
}
