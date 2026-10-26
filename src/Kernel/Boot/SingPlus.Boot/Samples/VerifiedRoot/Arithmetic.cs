namespace SingPlus.Boot.Samples;

// Static admission root only. No runtime capabilities or managed execution host.
public static class Arithmetic
{
#if NEXT_GENERATION
    public static int Increment(int value) => value + 2;
#else
    public static int Increment(int value) => value + 1;
#endif
}
