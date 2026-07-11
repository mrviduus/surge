using BenchmarkDotNet.Running;

namespace LoadSurge.Benchmarks
{
    /// <summary>Entry point: runs the benchmark switcher over all benchmark classes.</summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
    }
}
