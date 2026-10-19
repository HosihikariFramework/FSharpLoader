#if defined(_WIN32)
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

#ifndef PROBE_VALUE
#define PROBE_VALUE 42
#endif

EXPORT int hosihikari_probe(void)
{
    return PROBE_VALUE;
}
