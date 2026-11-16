#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <sddl.h>
#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <string>
#include <vector>
#include <thread>

using ModuleFunction = int (*)(void*);

struct FakeLuaState
{
    // Garry's Mod's x64 lua_State reserves 92 + 22 bytes before luabase.
    unsigned char ignored[114] = {};
    void* luaBase = nullptr;
};

static const char* PipePath = "\\\\.\\pipe\\GTerm.Module.27687";
static HMODULE Module = nullptr;
static HMODULE Tier0 = nullptr;
static ModuleFunction ModuleClose = nullptr;
static bool ModuleOpen = false;
static std::string ModulePath;

static void Fail(const char* message)
{
    std::fprintf(stderr, "FAIL: %s (Win32 %lu)\n", message, GetLastError());
    std::exit(1);
}

static void Check(bool condition, const char* message)
{
    if (!condition) Fail(message);
}

static HANDLE ConnectPipe(DWORD timeoutMs = 5000)
{
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    do
    {
        HANDLE pipe = CreateFileA(PipePath, GENERIC_READ | GENERIC_WRITE, 0,
            nullptr, OPEN_EXISTING, 0, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) return pipe;

        const DWORD error = GetLastError();
        if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND)
        {
            SetLastError(error);
            return INVALID_HANDLE_VALUE;
        }

        WaitNamedPipeA(PipePath, 100);
        Sleep(20);
    } while (GetTickCount64() < deadline);

    SetLastError(ERROR_TIMEOUT);
    return INVALID_HANDLE_VALUE;
}

static void Disconnect(HANDLE& pipe)
{
    if (pipe != INVALID_HANDLE_VALUE)
    {
        FlushFileBuffers(pipe);
        CloseHandle(pipe);
        pipe = INVALID_HANDLE_VALUE;
    }
}

static HANDLE OpenRestrictedToken()
{
    HANDLE processToken = nullptr;
    Check(OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_IMPERSONATE,
        &processToken) != FALSE, "OpenProcessToken");

    DWORD tokenInfoBytes = 0;
    GetTokenInformation(processToken, TokenUser, nullptr, 0, &tokenInfoBytes);
    Check(tokenInfoBytes != 0, "query current user SID size");
    std::vector<unsigned char> tokenInfo(tokenInfoBytes);
    Check(GetTokenInformation(processToken, TokenUser, tokenInfo.data(), tokenInfoBytes,
        &tokenInfoBytes) != FALSE, "query current user SID");
    auto* tokenUser = reinterpret_cast<TOKEN_USER*>(tokenInfo.data());

    PSID restrictedSid = nullptr;
    Check(ConvertStringSidToSidA("S-1-5-21-123456789-987654321-135792468-2468",
        &restrictedSid) != FALSE, "create test-only restricted SID");
    SID_AND_ATTRIBUTES disabledUser = { tokenUser->User.Sid, 0 };
    SID_AND_ATTRIBUTES restrictingSid = { restrictedSid, 0 };
    HANDLE restrictedToken = nullptr;
    const BOOL ok = CreateRestrictedToken(processToken, DISABLE_MAX_PRIVILEGE,
        1, &disabledUser, 0, nullptr, 1, &restrictingSid, &restrictedToken);
    const DWORD error = GetLastError();
    LocalFree(restrictedSid);
    CloseHandle(processToken);
    SetLastError(error);
    Check(ok != FALSE, "CreateRestrictedToken(restricted user and SID)");
    return restrictedToken;
}

static void VerifyDeniedRestrictedIdentity()
{
    HANDLE restrictedToken = OpenRestrictedToken();
    Check(ImpersonateLoggedOnUser(restrictedToken) != FALSE, "ImpersonateLoggedOnUser");
    HANDLE pipe = ConnectPipe(3000);
    const DWORD error = pipe == INVALID_HANDLE_VALUE ? GetLastError() : ERROR_SUCCESS;
    if (pipe != INVALID_HANDLE_VALUE) CloseHandle(pipe);
    RevertToSelf();
    CloseHandle(restrictedToken);

    Check(pipe == INVALID_HANDLE_VALUE, "restricted same-user token unexpectedly connected");
    Check(error == ERROR_ACCESS_DENIED, "restricted token was not rejected by the pipe DACL");
    std::puts("PASS: restricted token denied by named-pipe DACL");
}

static HANDLE VerifyAuthorizedConnection()
{
    HANDLE pipe = ConnectPipe();
    Check(pipe != INVALID_HANDLE_VALUE, "authorized same-user connection");

    ULONG serverPid = 0;
    Check(GetNamedPipeServerProcessId(pipe, &serverPid) != FALSE, "GetNamedPipeServerProcessId");
    Check(serverPid == GetCurrentProcessId(), "pipe server PID does not match the module host");
    std::puts("PASS: same-user connection and expected server PID verified");
    return pipe;
}

static void WriteLength(HANDLE pipe, uint32_t length)
{
    const unsigned char header[4] = {
        static_cast<unsigned char>(length & 0xff),
        static_cast<unsigned char>((length >> 8) & 0xff),
        static_cast<unsigned char>((length >> 16) & 0xff),
        static_cast<unsigned char>((length >> 24) & 0xff)
    };
    DWORD written = 0;
    Check(WriteFile(pipe, header, sizeof(header), &written, nullptr) != FALSE
        && written == sizeof(header), "write frame header");
}

static void VerifyZeroFrameAndIdle(HANDLE pipe)
{
    WriteLength(pipe, 0);
    Sleep(350);
    ULONG serverPid = 0;
    Check(GetNamedPipeServerProcessId(pipe, &serverPid) != FALSE,
        "idle or zero-length frame disconnected the peer");
    std::puts("PASS: zero-length frame and idle connection remain bounded and connected");
}

static void VerifyLogFrame(HANDLE pipe, void (*emitLogs)(int, int))
{
    Sleep(150);
    emitLogs(1, 128);
    DWORD available = 0;
    const ULONGLONG deadline = GetTickCount64() + 3000;
    while (GetTickCount64() < deadline)
    {
        Check(PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr) != FALSE, "peek log frame");
        if (available >= 4) break;
        Sleep(10);
    }
    Check(available >= 4, "log frame header timeout");
    uint8_t header[4];
    DWORD got = 0;
    Check(ReadFile(pipe, header, 4, &got, nullptr) && got == 4, "read log frame header");
    const uint32_t len = uint32_t(header[0]) | (uint32_t(header[1]) << 8)
        | (uint32_t(header[2]) << 16) | (uint32_t(header[3]) << 24);
    Check(len >= 128 && len < 1024, "log frame length");
    std::vector<char> frame(len);
    Check(ReadFile(pipe, frame.data(), len, &got, nullptr) && got == len, "read log frame payload");
    const std::string payload(frame.begin(), frame.end());
    Check(payload.find(std::string(128, 'x')) != std::string::npos, "log payload corrupted");
    std::puts("PASS: native log callback produces a complete framed payload");
}

static void VerifyPartialFrameReconnect()
{
    HANDLE partial = VerifyAuthorizedConnection();
    const unsigned char twoBytes[] = { 0x01, 0x00 };
    DWORD written = 0;
    Check(WriteFile(partial, twoBytes, sizeof(twoBytes), &written, nullptr) != FALSE
        && written == sizeof(twoBytes), "write partial header");
    CloseHandle(partial);
    Sleep(150);

    HANDLE reconnected = VerifyAuthorizedConnection();
    Disconnect(reconnected);
    std::puts("PASS: partial frame disconnect allows reconnect");
}

static void VerifyOversizedFrameReconnect()
{
    HANDLE oversized = VerifyAuthorizedConnection();
    WriteLength(oversized, 1024 * 1024 + 1);
    Sleep(150);

    unsigned char byte = 0;
    DWORD received = 0;
    const BOOL readOk = ReadFile(oversized, &byte, 1, &received, nullptr);
    const DWORD error = readOk ? ERROR_SUCCESS : GetLastError();
    CloseHandle(oversized);
    Check(!readOk && (error == ERROR_BROKEN_PIPE || error == ERROR_PIPE_NOT_CONNECTED),
        "oversized frame was not disconnected");

    HANDLE reconnected = VerifyAuthorizedConnection();
    Disconnect(reconnected);
    std::puts("PASS: oversized frame is rejected and a fresh client can reconnect");
}

static void OpenModule()
{
    Module = LoadLibraryA(ModulePath.c_str());
    Check(Module != nullptr, "LoadLibrary candidate module");

    auto moduleOpen = reinterpret_cast<ModuleFunction>(GetProcAddress(Module, "gmod13_open"));
    ModuleClose = reinterpret_cast<ModuleFunction>(GetProcAddress(Module, "gmod13_close"));
    Check(moduleOpen != nullptr && ModuleClose != nullptr, "resolve module entry points");

    FakeLuaState state;
    Check(moduleOpen(&state) == 0, "open candidate module");
    ModuleOpen = true;
}

static void CloseModule()
{
    if (ModuleOpen)
    {
        FakeLuaState state;
        const ULONGLONG start = GetTickCount64();
        Check(ModuleClose(&state) == 0, "close candidate module");
        Check(GetTickCount64() - start < 3000, "module shutdown exceeded 3 seconds");
        ModuleOpen = false;
        std::puts("PASS: module shutdown completes within 3 seconds");
    }
    if (Module != nullptr)
    {
        FreeLibrary(Module);
        Module = nullptr;
    }
}

int main(int argc, char** argv)
{
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    std::setvbuf(stderr, nullptr, _IONBF, 0);
    if (argc != 3)
    {
        std::fprintf(stderr, "Usage: %s <server-root> <candidate-module-dir>\n", argv[0]);
        return 2;
    }

    std::puts("Starting named-pipe module integration test");
    Check(SetCurrentDirectoryA(argv[1]) != FALSE, "set GMod server root as working directory");
    std::string modulePath = argv[2];
    if (!modulePath.empty() && modulePath.back() != '\\' && modulePath.back() != '/')
        modulePath.push_back('\\');
    modulePath += "gmsv_gterm_win64.dll";
    ModulePath = modulePath;

    std::puts("Loading isolated Tier0 test stub");
    Tier0 = LoadLibraryA("bin/win64/tier0.dll");
    Check(Tier0 != nullptr, "load isolated Tier0 test stub");
    Check(SetDllDirectoryA("bin\\win64") != FALSE, "set module dependency search path");

    SetEnvironmentVariableA("GTERM_MODULE_TRANSPORT", "windows_named_pipe");
    SetEnvironmentVariableA("GTERM_MODULE_PORT", "27687");

    // Keep module load explicit so its normal GMod runtime dependencies resolve.
    OpenModule();
    std::puts("Candidate module opened");

    HANDLE existing = ConnectPipe();
    Check(existing != INVALID_HANDLE_VALUE, "connect to candidate named pipe");
    Disconnect(existing);

    VerifyDeniedRestrictedIdentity();

    HANDLE zeroFrame = VerifyAuthorizedConnection();
    VerifyZeroFrameAndIdle(zeroFrame);
    Disconnect(zeroFrame);

    auto emitLogs = reinterpret_cast<void (*)(int, int)>(GetProcAddress(Tier0, "TestEmitLogs"));
    Check(emitLogs != nullptr, "resolve test log emitter");
    auto hasListener = reinterpret_cast<bool (*)()>(GetProcAddress(Tier0, "TestHasListener"));
    Check(hasListener && hasListener(), "module did not register stub listener");
    HANDLE logPeer = VerifyAuthorizedConnection();
    VerifyLogFrame(logPeer, emitLogs);
    Disconnect(logPeer);

    VerifyPartialFrameReconnect();
    VerifyOversizedFrameReconnect();

    // Fill the pipe and queue while the peer deliberately does not read.
    // Logging callbacks and shutdown must still finish promptly.
    HANDLE stalled = VerifyAuthorizedConnection();
    Sleep(150);
    const ULONGLONG logStart = GetTickCount64();
    emitLogs(400, 16384);
    Check(GetTickCount64() - logStart < 3000, "stalled reader blocked logging callback");
    std::thread racingLogs([emitLogs] { emitLogs(400, 16384); });
    CloseHandle(stalled);
    racingLogs.join();
    Sleep(150);
    HANDLE afterStall = VerifyAuthorizedConnection();
    Disconnect(afterStall);
    std::puts("PASS: stalled reader cannot block logging and reconnect remains available");

    // Exercise shutdown while the pipe worker is blocked waiting for a client.
    CloseModule();

    // Reopen the same DLL in this process to verify transport state resets.
    OpenModule();
    HANDLE afterReload = VerifyAuthorizedConnection();
    Disconnect(afterReload);

    // Exercise shutdown while the worker is blocked in ReadFile on an idle peer.
    HANDLE idle = VerifyAuthorizedConnection();
    Sleep(150);
    emitLogs(400, 16384);
    CloseModule();
    CloseHandle(idle);

    FreeLibrary(Tier0);
    Tier0 = nullptr;
    std::puts("All named-pipe transport integration checks passed");
    return 0;
}
