#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <cstdint>
#include <string>

// Minimal no-op Tier0 exports for exercising the module transport without
// loading a GMod engine or registering a listener with a live runtime.
struct Context { int32_t channel, flags, severity; uint32_t color; };
struct Channel { int32_t id, flags, severity; uint32_t color; char name[32]; void* tags; };
struct Listener { virtual void Log(const Context*, const char*) = 0; };
static Listener* listener = nullptr;
static Channel channel = {0, 0, 0, 0xffffffff, "test", nullptr};
extern "C" __declspec(dllexport) void LoggingSystem_RegisterLoggingListener(void* value) { listener = static_cast<Listener*>(value); }
extern "C" __declspec(dllexport) void LoggingSystem_UnregisterLoggingListener(void*) { listener = nullptr; }
extern "C" __declspec(dllexport) void LoggingSystem_PushLoggingState(bool, bool) {}
extern "C" __declspec(dllexport) void LoggingSystem_PopLoggingState(bool) {}
extern "C" __declspec(dllexport) const void* LoggingSystem_GetChannel(int32_t) { return &channel; }
extern "C" __declspec(dllexport) void TestEmitLogs(int count, int size) {
    std::string text(size, 'x');
    Context context = {0, 0, 0, 0xffffffff};
    for (int i = 0; i < count; ++i) if (listener) listener->Log(&context, text.c_str());
}
extern "C" __declspec(dllexport) bool TestHasListener() { return listener != nullptr; }

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID)
{
    return TRUE;
}
