#include "CommandWindow.h"
#include <bcrypt.h>
#include <cstdarg>
#include <string>
#include <vector>
#include <strsafe.h>

#pragma comment(lib, "bcrypt.lib")

static const wchar_t* c_szPipeName = L"\\\\.\\pipe\\PhoneUnlockPipe";
static const wchar_t* c_szLogPath = L"C:\\Windows\\Temp\\PhoneUnlock_DLL.log";

static void LogLine(const wchar_t* format, ...)
{
    HANDLE hFile = CreateFileW(
        c_szLogPath,
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (hFile == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME time;
    GetLocalTime(&time);
    wchar_t message[1024] = {};
    wchar_t line[1200] = {};
    va_list args;
    va_start(args, format);
    StringCchVPrintfW(message, ARRAYSIZE(message), format, args);
    va_end(args);
    StringCchPrintfW(
        line,
        ARRAYSIZE(line),
        L"[%04u-%02u-%02u %02u:%02u:%02u.%03u] %s\r\n",
        time.wYear, time.wMonth, time.wDay,
        time.wHour, time.wMinute, time.wSecond, time.wMilliseconds,
        message);

    DWORD bytesWritten = 0;
    WriteFile(hFile, line, static_cast<DWORD>(wcslen(line) * sizeof(wchar_t)), &bytesWritten, NULL);
    CloseHandle(hFile);
}

CCommandWindow::CCommandWindow() :
    _pProvider(NULL),
    _hThread(NULL),
    _hStopEvent(CreateEvent(NULL, TRUE, FALSE, NULL)),
    _fConnected(FALSE)
{
    LogLine(L"Pipe worker initialized; pipe=%s", c_szPipeName);
}

CCommandWindow::~CCommandWindow()
{
    LogLine(L"Pipe worker stopping");
    if (_hStopEvent) SetEvent(_hStopEvent);
    if (_hThread)
    {
        WaitForSingleObject(_hThread, INFINITE);
        CloseHandle(_hThread);
        _hThread = NULL;
    }
    if (_hStopEvent)
    {
        CloseHandle(_hStopEvent);
        _hStopEvent = NULL;
    }
    if (_pProvider)
    {
        _pProvider->Release();
        _pProvider = NULL;
    }
}

HRESULT CCommandWindow::Initialize(__in CSampleProvider *pProvider)
{
    if (!pProvider || !_hStopEvent) return E_INVALIDARG;
    _pProvider = pProvider;
    _pProvider->AddRef();
    _hThread = CreateThread(NULL, 0, _ThreadProc, this, 0, NULL);
    if (!_hThread)
    {
        LogLine(L"CreateThread failed; error=%lu", GetLastError());
    }
    else
    {
        LogLine(L"Pipe worker thread started");
    }
    return _hThread ? S_OK : HRESULT_FROM_WIN32(GetLastError());
}

BOOL CCommandWindow::GetConnectedStatus()
{
    return InterlockedCompareExchange(&_fConnected, 0, 0) != FALSE;
}

bool CCommandWindow::_WritePipeMessage(HANDLE hPipe, const std::wstring& text)
{
    DWORD bytesWritten = 0;
    const DWORD bytes = static_cast<DWORD>(text.size() * sizeof(wchar_t));
    const BOOL ok = WriteFile(hPipe, text.data(), bytes, &bytesWritten, NULL);
    if (!ok)
    {
        LogLine(L"WriteFile failed; error=%lu, bytes=%lu", GetLastError(), bytes);
        return false;
    }
    LogLine(L"Sent pipe message: chars=%lu, bytes=%lu", static_cast<DWORD>(text.size()), bytesWritten);
    return bytesWritten == bytes;
}

bool CCommandWindow::_SendUnlockRequest(HANDLE hPipe)
{
    BYTE randomBytes[32] = {};
    const NTSTATUS randomStatus = BCryptGenRandom(NULL, randomBytes, ARRAYSIZE(randomBytes), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    if (randomStatus != 0)
    {
        LogLine(L"BCryptGenRandom failed; status=0x%08lx", static_cast<unsigned long>(randomStatus));
        return false;
    }

    static const wchar_t hex[] = L"0123456789abcdef";
    std::wstring nonce;
    nonce.reserve(64);
    for (BYTE value : randomBytes)
    {
        nonce.push_back(hex[value >> 4]);
        nonce.push_back(hex[value & 0x0F]);
    }

    LogLine(L"Sending unlock request; nonce=%s", nonce.c_str());
    return _WritePipeMessage(hPipe, L"UNLOCK\n")
        && _WritePipeMessage(hPipe, nonce + L"\n");
}

bool CCommandWindow::_ConnectAndRead()
{
    LogLine(L"Attempting pipe connection");
    if (!WaitNamedPipe(c_szPipeName, 5000))
    {
        LogLine(L"WaitNamedPipe failed; error=%lu", GetLastError());
        return false;
    }
    LogLine(L"WaitNamedPipe succeeded");

    HANDLE hPipe = CreateFile(
        c_szPipeName,
        GENERIC_READ | GENERIC_WRITE,
        0,
        NULL,
        OPEN_EXISTING,
        0,
        NULL);
    if (hPipe == INVALID_HANDLE_VALUE)
    {
        LogLine(L"CreateFile pipe failed; error=%lu", GetLastError());
        return false;
    }
    LogLine(L"Pipe connected successfully");

    DWORD mode = PIPE_READMODE_MESSAGE;
    if (!SetNamedPipeHandleState(hPipe, &mode, NULL, NULL))
    {
        LogLine(L"SetNamedPipeHandleState failed; error=%lu", GetLastError());
        CloseHandle(hPipe);
        return false;
    }
    if (!_SendUnlockRequest(hPipe))
    {
        CloseHandle(hPipe);
        return false;
    }

    std::vector<wchar_t> buffer(32768);
    std::wstring response;
    DWORD bytesRead = 0;
    const BOOL readOk = ReadFile(
        hPipe,
        buffer.data(),
        static_cast<DWORD>(buffer.size() * sizeof(wchar_t)),
        &bytesRead,
        NULL);
    if (readOk && bytesRead > 0)
        response.assign(buffer.data(), bytesRead / sizeof(wchar_t));
    CloseHandle(hPipe);

    if (!readOk)
    {
        LogLine(L"ReadFile failed; error=%lu", GetLastError());
        return false;
    }
    LogLine(L"Received pipe response; bytes=%lu, chars=%lu", bytesRead, static_cast<DWORD>(response.size()));
    if (response.empty())
    {
        LogLine(L"Received empty pipe response");
        return false;
    }

    size_t first = response.find(L'\n');
    size_t second = first == std::wstring::npos ? std::wstring::npos : response.find(L'\n', first + 1);
    size_t third = second == std::wstring::npos ? std::wstring::npos : response.find(L'\n', second + 1);
    if (response.rfind(L"APPROVED\n", 0) != 0 || first == std::wstring::npos || second == std::wstring::npos)
    {
        LogLine(L"Pipe response was not a valid APPROVED payload");
        return false;
    }

    const std::wstring username = response.substr(first + 1, second - first - 1);
    const std::wstring password = response.substr(second + 1, third == std::wstring::npos ? std::wstring::npos : third - second - 1);
    if (username.empty() || password.empty())
    {
        LogLine(L"APPROVED response had an empty username or password");
        return false;
    }

    InterlockedExchange(&_fConnected, TRUE);
    if (FAILED(_pProvider->OnCredentialsApproved(username.c_str(), password.c_str())))
    {
        LogLine(L"Provider rejected approved credentials");
        InterlockedExchange(&_fConnected, FALSE);
        return false;
    }
    LogLine(L"Approved credentials delivered to provider");
    return true;
}

void CCommandWindow::_Run()
{
    LogLine(L"Pipe worker run loop entered");
    while (WaitForSingleObject(_hStopEvent, 0) != WAIT_OBJECT_0)
    {
        InterlockedExchange(&_fConnected, FALSE);
        _ConnectAndRead();
        if (WaitForSingleObject(_hStopEvent, 0) == WAIT_OBJECT_0) break;
        WaitForSingleObject(_hStopEvent, 2000);
    }
    LogLine(L"Pipe worker run loop exited");
}

DWORD WINAPI CCommandWindow::_ThreadProc(__in LPVOID lpParameter)
{
    CCommandWindow* self = static_cast<CCommandWindow*>(lpParameter);
    if (self) self->_Run();
    return 0;
}
