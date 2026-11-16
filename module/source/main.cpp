#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <Windows.h>
#include <sddl.h>
#else
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#endif

#include <cstring>
#include <cstdint>
#include <cerrno>
#include <cstdlib>
#include <string>
#include <thread>
#include <chrono>
#include <atomic>
#include <mutex>
#include <vector>
#include <algorithm>
#include <cctype>
#include <condition_variable>
#include <deque>
#include "PortConfig.h"

#include <GarrysMod/Lua/Interface.h>
#include <GarrysMod/FactoryLoader.hpp>
#include <ByteBuffer.hpp>
#include <Platform.hpp>
#include <color.h>
#include <eiface.h>

namespace Tier0
{
	typedef int32_t SpewRetval_t;
	typedef SpewRetval_t (*SpewOutputFunc_t)(int32_t spewType, const char* pMsg);

	struct LoggingContext_t
	{
		int32_t m_ChannelID;
		int32_t m_Flags;
		int32_t m_Severity;
		Color m_Color;
	};

	struct LoggingChannel_t
	{
		int32_t m_ID;
		int32_t m_Flags;
		int32_t m_MinimumSeverity;
		Color m_SpewColor;
		char m_Name[32];
		void* m_pFirstTag;
	};

	class ILoggingListener
	{
	public:
		virtual void Log(const LoggingContext_t* pContext, const char* pMessage) = 0;
	};

	static void (*SpewOutputFunc)(SpewOutputFunc_t func) = nullptr;
	static SpewOutputFunc_t (*GetSpewOutputFunc)() = nullptr;
	static const char* (*GetSpewOutputGroup)() = nullptr;
	static int (*GetSpewOutputLevel)() = nullptr;
	static const Color* (*GetSpewOutputColor)() = nullptr;

	static void (*LoggingSystem_RegisterLoggingListener)(ILoggingListener* pListener) = nullptr;
	static void (*LoggingSystem_UnregisterLoggingListener)(ILoggingListener* pListener) = nullptr;
	static void (*LoggingSystem_PushLoggingState)(bool bThreadLocal, bool bClearState) = nullptr;
	static void (*LoggingSystem_PopLoggingState)(bool bThreadLocal) = nullptr;
	static const LoggingChannel_t* (*LoggingSystem_GetChannel)(int32_t channelID) = nullptr;
}

#ifdef _WIN32
typedef SOCKET socket_t;
#define INVALID_SOCKET_VALUE INVALID_SOCKET
#define CloseSocket(s) closesocket(s)
#else
typedef int socket_t;
#define INVALID_SOCKET_VALUE (-1)
#define CloseSocket(s) close(s)
#endif

// A per-process override lets a dedicated server and a local player coexist.
static const uint16_t GTERM_DEFAULT_PORT = 27514;
static const char* GTERM_HOST = "127.0.0.1";
// Bound a peer's command frame before allocating its payload.
static const uint32_t GTERM_MAX_COMMAND_BYTES = 1024 * 1024;
enum class ModuleTransport { Tcp, WindowsNamedPipe };
static ModuleTransport moduleTransport = ModuleTransport::Tcp;

static bool GetGTermModuleTransport(ModuleTransport& transport)
{
    transport = ModuleTransport::Tcp;
    char value[64] = {};
#ifdef _WIN32
    SetLastError(ERROR_SUCCESS);
    const DWORD length = GetEnvironmentVariableA("GTERM_MODULE_TRANSPORT", value, sizeof(value));
    if (length == 0 && GetLastError() == ERROR_ENVVAR_NOT_FOUND) return true;
    if (length == 0 || length >= sizeof(value)) return false;
#else
    const char* configured = std::getenv("GTERM_MODULE_TRANSPORT");
    if (!configured) return true;
    if (!*configured || std::strlen(configured) >= sizeof(value)) return false;
    std::strncpy(value, configured, sizeof(value) - 1);
#endif

    std::string normalized(value);
    std::transform(normalized.begin(), normalized.end(), normalized.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    if (normalized == "tcp") return true;
    if (normalized == "windows_named_pipe")
    {
#ifdef _WIN32
        transport = ModuleTransport::WindowsNamedPipe;
        return true;
#else
        return false;
#endif
    }
    return false;
}

static bool GetGTermModulePort(uint16_t& port)
{
    port = GTERM_DEFAULT_PORT;
#ifdef _WIN32
    char value[6];
    SetLastError(ERROR_SUCCESS);
    const DWORD length = GetEnvironmentVariableA("GTERM_MODULE_PORT", value, sizeof(value));
    if (length == 0 && GetLastError() == ERROR_ENVVAR_NOT_FOUND) return true;
    if (length == 0 || length >= sizeof(value)) return false;
    return ParseGTermModulePort(value, port);
#else
    const char* value = std::getenv("GTERM_MODULE_PORT");
    return value ? ParseGTermModulePort(value, port) : true;
#endif
}

static socket_t listenSocket = INVALID_SOCKET_VALUE;
static std::atomic<socket_t> clientSocket{ INVALID_SOCKET_VALUE };
static std::mutex sendMutex;
#ifdef _WIN32
static std::atomic<HANDLE> clientPipe{ INVALID_HANDLE_VALUE };
static std::string modulePipeName;
static std::mutex pipeQueueMutex;
static std::condition_variable pipeQueueReady;
static std::deque<std::vector<uint8_t>> pipeQueue;
static size_t pipeQueuedBytes = 0;
static std::atomic<bool> pipeWriterActive{ false };
static const size_t GTERM_MAX_QUEUED_LOG_BYTES = 1024 * 1024;
#endif

static std::atomic<bool> serverShutdown{ false };
static std::atomic<bool> serverConnected{ false };
static std::thread serverThread;

// Writes a single framed message to the connected client: a little-endian uint32
// length prefix followed by the message body. Framing makes the stream
// self-delimiting (replacing the old <EOL> sentinel) on every platform.
static void SendBuffer(const uint8_t* data, size_t size)
{
	std::lock_guard<std::mutex> lock(sendMutex);

	uint32_t length = static_cast<uint32_t>(size);
	uint8_t header[4] = {
		static_cast<uint8_t>(length & 0xFF),
		static_cast<uint8_t>((length >> 8) & 0xFF),
		static_cast<uint8_t>((length >> 16) & 0xFF),
		static_cast<uint8_t>((length >> 24) & 0xFF)
	};

	if (moduleTransport == ModuleTransport::WindowsNamedPipe)
	{
#ifdef _WIN32
		// Logging callbacks must never wait on a client that has stopped reading.
		// Drop whole frames once the bounded per-connection queue is full.
		if (size > GTERM_MAX_QUEUED_LOG_BYTES - sizeof(header)) return;
		std::lock_guard<std::mutex> queueLock(pipeQueueMutex);
		if (!pipeWriterActive || pipeQueuedBytes + sizeof(header) + size > GTERM_MAX_QUEUED_LOG_BYTES) return;
		std::vector<uint8_t> frame;
		frame.reserve(sizeof(header) + size);
		frame.insert(frame.end(), header, header + sizeof(header));
		frame.insert(frame.end(), data, data + size);
		pipeQueuedBytes += frame.size();
		pipeQueue.emplace_back(std::move(frame));
		pipeQueueReady.notify_one();
		return;
#else
		return;
#endif
	}

	socket_t sock = clientSocket.load();
	if (sock == INVALID_SOCKET_VALUE)
	{
		serverConnected = false;
		return;
	}
	auto sendAll = [sock](const uint8_t* buf, size_t len) -> bool
	{
		size_t sent = 0;
		while (sent < len)
		{
#ifdef _WIN32
			int n = send(sock, reinterpret_cast<const char*>(buf + sent), static_cast<int>(len - sent), 0);
#else
			ssize_t n = send(sock, buf + sent, len - sent, MSG_NOSIGNAL);
#endif
			if (n <= 0)
				return false;

			sent += static_cast<size_t>(n);
		}

		return true;
	};

	if (!sendAll(header, sizeof(header)) || !sendAll(data, size))
		serverConnected = false;
}

class GTermConsoleListener : public Tier0::ILoggingListener
{
public:
	void Log(const Tier0::LoggingContext_t* pContext, const char* pMessage) override
	{
		const Tier0::LoggingChannel_t* chan = Tier0::LoggingSystem_GetChannel(pContext->m_ChannelID);
		const Color* color = &pContext->m_Color;
		int size = 12 + std::strlen(chan->m_Name) + std::strlen(pMessage);

		MultiLibrary::ByteBuffer buffer;
		buffer.Reserve(size);

		buffer <<
			chan->m_ID <<
			pContext->m_Severity <<
			chan->m_Name <<
			color->GetRawColor() <<
			pMessage;

		SendBuffer(buffer.GetBuffer(), buffer.Size());
	}
};

static GTermConsoleListener listener;
static bool useLoggingSystem = false;

template<typename T>
static bool ResolveSymbol(const SourceSDK::ModuleLoader& module, T& out, const char* name)
{
	out = reinterpret_cast<T>(module.GetSymbol(name));
	return out != nullptr;
}

#ifdef _WIN32
template<typename T>
static bool ResolveSymbol(HMODULE module, T& out, const char* name)
{
	out = reinterpret_cast<T>(GetProcAddress(module, name));
	return out != nullptr;
}
#endif

static bool ResolveLoggingSystem(const SourceSDK::ModuleLoader& tier0)
{
	return
		ResolveSymbol(tier0, Tier0::LoggingSystem_RegisterLoggingListener, "LoggingSystem_RegisterLoggingListener") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_UnregisterLoggingListener, "LoggingSystem_UnregisterLoggingListener") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_PushLoggingState, "LoggingSystem_PushLoggingState") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_PopLoggingState, "LoggingSystem_PopLoggingState") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_GetChannel, "LoggingSystem_GetChannel");
}

static bool ResolveSpew(const SourceSDK::ModuleLoader& tier0)
{
	return
		ResolveSymbol(tier0, Tier0::SpewOutputFunc, "SpewOutputFunc") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputFunc, "GetSpewOutputFunc") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputGroup, "GetSpewOutputGroup") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputLevel, "GetSpewOutputLevel") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputColor, "GetSpewOutputColor");
}

#ifdef _WIN32
static bool ResolveLoggingSystem(HMODULE tier0)
{
	return
		ResolveSymbol(tier0, Tier0::LoggingSystem_RegisterLoggingListener, "LoggingSystem_RegisterLoggingListener") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_UnregisterLoggingListener, "LoggingSystem_UnregisterLoggingListener") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_PushLoggingState, "LoggingSystem_PushLoggingState") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_PopLoggingState, "LoggingSystem_PopLoggingState") &&
		ResolveSymbol(tier0, Tier0::LoggingSystem_GetChannel, "LoggingSystem_GetChannel");
}

static bool ResolveSpew(HMODULE tier0)
{
	return
		ResolveSymbol(tier0, Tier0::SpewOutputFunc, "SpewOutputFunc") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputFunc, "GetSpewOutputFunc") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputGroup, "GetSpewOutputGroup") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputLevel, "GetSpewOutputLevel") &&
		ResolveSymbol(tier0, Tier0::GetSpewOutputColor, "GetSpewOutputColor");
}
#endif

static Tier0::SpewOutputFunc_t spewFunction = nullptr;
static Tier0::SpewRetval_t EngineSpewReceiver(int32_t type, const char* msg)
{
	if (!serverConnected)
		return spewFunction(type, msg);

	const Color* color = Tier0::GetSpewOutputColor();
	MultiLibrary::ByteBuffer buffer;
	buffer.Reserve(512);

	buffer <<
		type <<
		Tier0::GetSpewOutputLevel() <<
		Tier0::GetSpewOutputGroup() <<
		color->GetRawColor() <<
		msg;

	SendBuffer(buffer.GetBuffer(), buffer.Size());

	return spewFunction(type, msg);
}

static void RunCommand(std::string cmd)
{
	// in case the command hasnt been passed with a newline
	if (cmd.empty())
		return;

	if (cmd[cmd.length() - 1] != '\n')
		cmd.append("\n");

	SourceSDK::FactoryLoader engine_loader("engine");
	IVEngineServer* engine_server = engine_loader.GetInterface<IVEngineServer>(INTERFACEVERSION_VENGINESERVER);
	if (engine_server == nullptr) return;
	engine_server->ServerCommand(cmd.c_str());
}

// Reads exactly 'len' bytes from the socket into 'out'. Returns false on
// disconnect/error so the caller can drop the client.
static bool RecvAll(socket_t sock, uint8_t* out, size_t len)
{
	size_t received = 0;
	while (received < len)
	{
#ifdef _WIN32
		int n = recv(sock, reinterpret_cast<char*>(out + received), static_cast<int>(len - received), 0);
#else
		ssize_t n = recv(sock, out + received, len - received, 0);
#endif
		if (n > 0)
		{
			received += static_cast<size_t>(n);
			continue;
		}

		// n == 0 means the peer closed the connection: a real disconnect.
		if (n == 0)
			return false;

		// n < 0: the accepted socket inherits SO_RCVTIMEO from the listen
		// socket, so an idle read trips the timeout roughly every 250ms. That
		// is not a disconnect - keep waiting unless we're shutting down. Any
		// other error is a genuine failure and drops the client.
#ifdef _WIN32
		int err = WSAGetLastError();
		if (err == WSAETIMEDOUT || err == WSAEWOULDBLOCK || err == WSAEINTR)
#else
		if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)
#endif
		{
			if (serverShutdown)
				return false;

			continue;
		}

		return false;
	}

	return true;
}

static void ServerThreadTcp()
{
	while (!serverShutdown)
	{
		socket_t client = accept(listenSocket, nullptr, nullptr);
		if (client == INVALID_SOCKET_VALUE) continue;

		clientSocket.store(client);
		serverConnected = true;

		while (!serverShutdown)
		{
			uint8_t header[4];
			if (!RecvAll(client, header, sizeof(header))) break;

			uint32_t length =
				static_cast<uint32_t>(header[0]) |
				(static_cast<uint32_t>(header[1]) << 8) |
				(static_cast<uint32_t>(header[2]) << 16) |
				(static_cast<uint32_t>(header[3]) << 24);

			if (length == 0) continue;
			if (length > GTERM_MAX_COMMAND_BYTES) break;

			std::vector<uint8_t> payload(length);
			if (!RecvAll(client, payload.data(), length)) break;
			RunCommand(std::string(payload.begin(), payload.end()));
		}

		clientSocket.store(INVALID_SOCKET_VALUE);
		serverConnected = false;
		CloseSocket(client);
	}
}

#ifdef _WIN32
static bool CreateCurrentUserPipeSecurity(SECURITY_ATTRIBUTES& attributes, PSECURITY_DESCRIPTOR& descriptor)
{
	HANDLE token = nullptr;
	if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;

	DWORD needed = 0;
	GetTokenInformation(token, TokenUser, nullptr, 0, &needed);
	if (needed == 0)
	{
		CloseHandle(token);
		return false;
	}
	std::vector<uint8_t> tokenInfo(needed);
	if (!GetTokenInformation(token, TokenUser, tokenInfo.data(), needed, &needed))
	{
		CloseHandle(token);
		return false;
	}
	CloseHandle(token);

	TOKEN_USER* user = reinterpret_cast<TOKEN_USER*>(tokenInfo.data());
	LPSTR userSid = nullptr;
	if (!ConvertSidToStringSidA(user->User.Sid, &userSid)) return false;
	std::string sddl = "D:P(A;;GA;;;SY)(A;;GA;;;";
	sddl += userSid;
	sddl += ")";
	LocalFree(userSid);

	if (!ConvertStringSecurityDescriptorToSecurityDescriptorA(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr))
		return false;
	attributes.nLength = sizeof(attributes);
	attributes.lpSecurityDescriptor = descriptor;
	attributes.bInheritHandle = FALSE;
	return true;
}

static bool PipeIO(HANDLE pipe, uint8_t* buffer, DWORD length, DWORD& count, bool write)
{
	OVERLAPPED operation = {};
	operation.hEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
	if (!operation.hEvent) return false;
	BOOL ok = write ? WriteFile(pipe, buffer, length, &count, &operation)
	                : ReadFile(pipe, buffer, length, &count, &operation);
	if (!ok && GetLastError() == ERROR_IO_PENDING)
	{
		for (;;)
		{
			DWORD result = WaitForSingleObject(operation.hEvent, 50);
			if (result == WAIT_OBJECT_0) { ok = GetOverlappedResult(pipe, &operation, &count, FALSE); break; }
			if (result != WAIT_TIMEOUT || serverShutdown || (write && !pipeWriterActive))
			{
				CancelIoEx(pipe, &operation);
				GetOverlappedResult(pipe, &operation, &count, TRUE);
				ok = FALSE;
				break;
			}
		}
	}
	CloseHandle(operation.hEvent);
	return ok != FALSE && count != 0;
}

static bool RecvAllPipe(HANDLE pipe, uint8_t* out, size_t len)
{
	size_t received = 0;
	while (received < len && !serverShutdown)
	{
		DWORD count = 0;
		DWORD chunk = static_cast<DWORD>((std::min)(len - received, static_cast<size_t>(0x7fffffff)));
		if (!PipeIO(pipe, out + received, chunk, count, false)) return false;
		received += count;
	}
	return received == len;
}

static void PipeLogWriter(HANDLE pipe)
{
	for (;;)
	{
		std::vector<uint8_t> frame;
		{
			std::unique_lock<std::mutex> lock(pipeQueueMutex);
			pipeQueueReady.wait(lock, [] { return !pipeWriterActive || !pipeQueue.empty(); });
			if (!pipeWriterActive) return;
			frame = std::move(pipeQueue.front());
			pipeQueue.pop_front();
			pipeQueuedBytes -= frame.size();
		}
		size_t sent = 0;
		while (sent < frame.size())
		{
			DWORD written = 0;
			DWORD chunk = static_cast<DWORD>((std::min)(frame.size() - sent, static_cast<size_t>(0x7fffffff)));
			if (!PipeIO(pipe, frame.data() + sent, chunk, written, true))
			{
				serverConnected = false;
				// Unblock the receiver, which owns the pipe and joins this thread.
				DisconnectNamedPipe(pipe);
				return;
			}
			sent += written;
		}
	}
}

static void ServerThreadNamedPipe()
{
	while (!serverShutdown)
	{
		SECURITY_ATTRIBUTES attributes = {};
		PSECURITY_DESCRIPTOR descriptor = nullptr;
		if (!CreateCurrentUserPipeSecurity(attributes, descriptor))
		{
			std::this_thread::sleep_for(std::chrono::milliseconds(250));
			continue;
		}
		HANDLE pipe = CreateNamedPipeA(
			modulePipeName.c_str(),
			PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
			PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
			1, 65536, 65536, 0, &attributes);
		LocalFree(descriptor);
		if (pipe == INVALID_HANDLE_VALUE)
		{
			std::this_thread::sleep_for(std::chrono::milliseconds(250));
			continue;
		}

		OVERLAPPED accept = {};
		accept.hEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
		if (!accept.hEvent) { CloseHandle(pipe); continue; }
		BOOL connected = ConnectNamedPipe(pipe, &accept);
		DWORD acceptError = connected ? ERROR_SUCCESS : GetLastError();
		if (!connected && acceptError == ERROR_PIPE_CONNECTED) connected = TRUE;
		if (!connected && acceptError == ERROR_IO_PENDING)
		{
			DWORD waitResult = WAIT_TIMEOUT;
			while (!serverShutdown && waitResult == WAIT_TIMEOUT)
				waitResult = WaitForSingleObject(accept.hEvent, 50);
			if (serverShutdown || waitResult != WAIT_OBJECT_0)
			{
				CancelIoEx(pipe, &accept);
				DWORD ignored;
				GetOverlappedResult(pipe, &accept, &ignored, TRUE);
			}
			else { DWORD ignored; connected = GetOverlappedResult(pipe, &accept, &ignored, FALSE); }
		}
		CloseHandle(accept.hEvent);
		if (!connected)
		{
			CloseHandle(pipe);
			continue;
		}
		if (serverShutdown)
		{
			DisconnectNamedPipe(pipe);
			CloseHandle(pipe);
			continue;
		}
		clientPipe.store(pipe);
		{
			std::lock_guard<std::mutex> lock(pipeQueueMutex);
			pipeQueue.clear();
			pipeQueuedBytes = 0;
			pipeWriterActive = true;
		}
		std::thread writer(PipeLogWriter, pipe);
		serverConnected = true;

		while (!serverShutdown)
		{
			uint8_t header[4];
			if (!RecvAllPipe(pipe, header, sizeof(header))) break;

			uint32_t length =
				static_cast<uint32_t>(header[0]) |
				(static_cast<uint32_t>(header[1]) << 8) |
				(static_cast<uint32_t>(header[2]) << 16) |
				(static_cast<uint32_t>(header[3]) << 24);
			if (length == 0) continue;
			if (length > GTERM_MAX_COMMAND_BYTES) break;

			std::vector<uint8_t> payload(length);
			if (!RecvAllPipe(pipe, payload.data(), length)) break;
			RunCommand(std::string(payload.begin(), payload.end()));
		}

		clientPipe.store(INVALID_HANDLE_VALUE);
		serverConnected = false;
		{
			std::lock_guard<std::mutex> lock(pipeQueueMutex);
			pipeWriterActive = false;
			pipeQueue.clear();
			pipeQueuedBytes = 0;
		}
		pipeQueueReady.notify_one();
		while (WaitForSingleObject(writer.native_handle(), 50) == WAIT_TIMEOUT)
			CancelSynchronousIo(writer.native_handle());
		writer.join();
		DisconnectNamedPipe(pipe);
		CloseHandle(pipe);
	}
}
#endif

// Owns the lifetime of the client socket: accepts a connection, reads framed
// commands until the client disconnects, then loops back to accept again.
static void ServerThread()
{
#ifdef _WIN32
	if (moduleTransport == ModuleTransport::WindowsNamedPipe) ServerThreadNamedPipe();
	else ServerThreadTcp();
#else
	ServerThreadTcp();
#endif
}

GMOD_MODULE_OPEN()
{
    serverShutdown.store(false);
    serverConnected.store(false);

    uint16_t modulePort;
    if (!GetGTermModulePort(modulePort))
    {
        LUA->ThrowError("GTERM_MODULE_PORT must be a decimal port between 1 and 65535");
        return 0;
    }
    if (!GetGTermModuleTransport(moduleTransport))
    {
        LUA->ThrowError("GTERM_MODULE_TRANSPORT must be tcp or windows_named_pipe (Windows only)");
        return 0;
    }
	{
		SourceSDK::ModuleLoader tier0("tier0");
		useLoggingSystem = ResolveLoggingSystem(tier0);
		bool useSpew = !useLoggingSystem && ResolveSpew(tier0);
#ifdef _WIN32
		// GetModuleHandleEx may not resolve the relative SDK search path when
		// GMod loaded tier0 under its absolute path. The already-loaded basename
		// is unambiguous in this process, so retry symbols against that handle.
		if (!useLoggingSystem && !useSpew)
		{
			HMODULE loadedTier0 = GetModuleHandleA("tier0.dll");
			if (loadedTier0 != nullptr)
			{
				useLoggingSystem = ResolveLoggingSystem(loadedTier0);
				useSpew = !useLoggingSystem && ResolveSpew(loadedTier0);
			}
		}
#endif
		if (!useLoggingSystem && !useSpew)
			LUA->ThrowError("failed to find a console output hook in tier0");
	}

#ifdef _WIN32
	if (moduleTransport == ModuleTransport::Tcp)
	{
		WSADATA wsaData;
		if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0)
			LUA->ThrowError("failed to initialize Winsock");
	}
#endif

	if (moduleTransport == ModuleTransport::Tcp)
	{
		listenSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
		if (listenSocket == INVALID_SOCKET_VALUE)
			LUA->ThrowError("failed to create socket");

        int reuse = 1;
#ifdef _WIN32
	setsockopt(listenSocket, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char*>(&reuse), sizeof(reuse));
#else
	setsockopt(listenSocket, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));
#endif

	// Give accept() a timeout so ServerThread can observe serverShutdown.
#ifdef _WIN32
	DWORD timeout = 250; // milliseconds
	setsockopt(listenSocket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
#else
	struct timeval timeout;
	timeout.tv_sec = 0;
	timeout.tv_usec = 250000;
	setsockopt(listenSocket, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
#endif

	sockaddr_in addr;
	std::memset(&addr, 0, sizeof(addr));
	addr.sin_family = AF_INET;
	addr.sin_port = htons(modulePort);
	inet_pton(AF_INET, GTERM_HOST, &addr.sin_addr);

        if (bind(listenSocket, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0)
			LUA->ThrowError("failed to bind socket");

		if (listen(listenSocket, 1) != 0)
			LUA->ThrowError("failed to listen on socket");
	}
#ifdef _WIN32
	else
	{
		modulePipeName = "\\\\.\\pipe\\GTerm.Module." + std::to_string(modulePort);
	}
#endif

	serverThread = std::thread(ServerThread);

	if (useLoggingSystem)
	{
		Tier0::LoggingSystem_PushLoggingState(false, false);
		Tier0::LoggingSystem_RegisterLoggingListener(&listener);
	}
	else
	{
		spewFunction = Tier0::GetSpewOutputFunc();
		Tier0::SpewOutputFunc(EngineSpewReceiver);
	}

	return 0;
}

GMOD_MODULE_CLOSE()
{
	if (useLoggingSystem)
	{
		Tier0::LoggingSystem_UnregisterLoggingListener(&listener);
		Tier0::LoggingSystem_PopLoggingState(false);
	}
	else if (spewFunction != nullptr)
	{
		Tier0::SpewOutputFunc(spewFunction);
	}

	serverShutdown.store(true);

	// Closing the listen socket unblocks accept() if it is mid-call.
	if (listenSocket != INVALID_SOCKET_VALUE)
		CloseSocket(listenSocket);

	if (serverThread.joinable())
	{
#ifdef _WIN32
		// A pipe read may race with the first cancellation; keep canceling while
		// the worker is alive so shutdown cannot strand the module thread.
		HANDLE threadHandle = serverThread.native_handle();
		while (WaitForSingleObject(threadHandle, 50) == WAIT_TIMEOUT)
			CancelSynchronousIo(threadHandle);
#endif
		serverThread.join();
	}

	socket_t client = clientSocket.exchange(INVALID_SOCKET_VALUE);
	if (client != INVALID_SOCKET_VALUE)
		CloseSocket(client);


#ifdef _WIN32
	if (moduleTransport == ModuleTransport::Tcp)
	WSACleanup();
#endif

	return 0;
}
