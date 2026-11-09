using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace UmaAiSharp.Core;

public static class ExecutionPlatform
{
    public static void EnsureSupported()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !Avx2.IsSupported)
            throw new PlatformNotSupportedException("执行引擎要求 x64 进程与 AVX2；没有标量降级路径。");
    }

    public static bool HasAvx512 => Avx512F.IsSupported;
}

public readonly record struct ExecutionMemoryPlan(long SharedBytes, long WorkspaceBytes)
{
    public const int WorkerCapacity = 32;
    public const long LimitBytes = 8L * 1024 * 1024 * 1024;
    public long ReservedBytes => checked(SharedBytes + WorkspaceBytes * WorkerCapacity);

    public void Validate()
    {
        if (SharedBytes < 0 || WorkspaceBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(SharedBytes), "内存容量账本不能为负数。");
        if (ReservedBytes > LimitBytes)
            throw new InvalidOperationException($"共享数据及32个工作区需要 {ReservedBytes} 字节，超过8 GiB上限。");
    }
}
