using Unknown6656.Testing;

#if FORCE_USE_UNKNOWN6656_TEST_FRAMEWORK
using System.Runtime.CompilerServices;

namespace Unknown6656.AutoIt3.Testing;


public static class Program
{
    [ModuleInitializer]
    public static void Main() => UnitTestRunner.RunTests();
}
#else
UnitTestRunner.RunTests();
#endif
