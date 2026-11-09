using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace UmaAiSharp.Benchmarks;

[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 1)]
public class CopyBenchmarks
{
    [Params(32, 256, 4096)] public int Length { get; set; }
    int[] source = [];
    int[] destination = [];

    [GlobalSetup]
    public void Setup()
    {
        if (!Vector256.IsHardwareAccelerated || !Vector512.IsHardwareAccelerated)
            throw new PlatformNotSupportedException("此对照基准要求AVX2和AVX-512，不用于生产CPU降级。");
        source = Enumerable.Range(0, Length).ToArray();
        destination = new int[Length];
        Avx2();
        if (!source.AsSpan().SequenceEqual(destination)) throw new InvalidDataException("AVX2复制核不一致。");
        destination.AsSpan().Clear();
        Avx512();
        if (!source.AsSpan().SequenceEqual(destination)) throw new InvalidDataException("AVX-512复制核不一致。");
    }

    [Benchmark(Baseline = true)]
    public void RuntimeCopy() => source.AsSpan().CopyTo(destination);

    [Benchmark]
    public void Avx2()
    {
        var index = 0;
        ref var from = ref source[0];
        ref var to = ref destination[0];
        for (; index <= Length - Vector256<int>.Count; index += Vector256<int>.Count)
            Vector256.LoadUnsafe(ref from, (nuint)index).StoreUnsafe(ref to, (nuint)index);
        source.AsSpan(index).CopyTo(destination.AsSpan(index));
    }

    [Benchmark]
    public void Avx512()
    {
        var index = 0;
        ref var from = ref source[0];
        ref var to = ref destination[0];
        for (; index <= Length - Vector512<int>.Count; index += Vector512<int>.Count)
            Vector512.LoadUnsafe(ref from, (nuint)index).StoreUnsafe(ref to, (nuint)index);
        source.AsSpan(index).CopyTo(destination.AsSpan(index));
    }
}
